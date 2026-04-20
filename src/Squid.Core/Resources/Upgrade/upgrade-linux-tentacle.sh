#!/usr/bin/env bash
# ==============================================================================
# Squid Tentacle self-upgrade script
# ------------------------------------------------------------------------------
# Sent over a Halibut polling RPC by the server's LinuxTentacleUpgradeStrategy
# at ScriptIsolationLevel.FullIsolation, so the agent serialises this behind
# any in-flight deployment scripts — we never restart a tentacle mid-deploy.
#
# Placeholders ({{...}}) are filled by the server before transmission.
#
# ARCHITECTURE (Phase 1 — post self-kill fix):
#   The bash script initially runs as a child of the tentacle service (in its
#   systemd cgroup). Halibut streams stdout back to the server in this phase.
#
#   Before any step that would touch the service itself, the script re-execs
#   ITSELF into a transient systemd scope via `systemd-run --scope`. This
#   MIGRATES the remaining steps into a SEPARATE cgroup — so when we later
#   call `systemctl restart squid-tentacle`, systemd only kills the service's
#   cgroup, NOT the scope we're running in. Without this detachment the
#   bash process would die along with the tentacle during `systemctl stop`
#   (systemd default KillMode=control-group kills the whole cgroup).
#
#   Because the Halibut connection is child of the tentacle, it dies when
#   the service restarts. The server can't observe the outcome via Halibut
#   after that point. Out-of-band status is written to
#     /var/lib/squid-tentacle/last-upgrade.json
#   which the server reads on the next health check (matching Octopus's
#   "exit-code-to-disk + server polls" pattern).
#
# Status progression:
#   IN_PROGRESS            — logged BEFORE scope detach so a crash before the
#                            handoff is still visible
#   SWAPPED                — new binary is on disk, past the point of no return
#   SUCCESS                — service restarted, healthz OK, --version matches
#   ROLLED_BACK            — new binary broke; previous version restored and
#                            running; no operator action needed
#   ROLLBACK_CRITICAL_FAILED
#                          — new binary broke AND restoring old also failed;
#                            agent is in unknown state → operator must SSH
#
# Exit codes (Halibut-visible, from the PRE-scope phase only):
#   0  — dispatched to scope successfully (terminal outcome written to status
#        file; server reads via next health check)
#   1  — unsupported architecture
#   2  — download failure
#   3  — missing binary in extracted archive
#   5  — insufficient disk space
#   6  — target tarball URL not reachable (pre-flight HEAD failed)
#   7  — SHA256 mismatch
#   8  — libc/glibc compat probe found unresolved deps in new binary
#   12 — systemd too old for `systemd-run --scope` (needs v239+)
#   13 — failed to spawn the scope (sudo/systemd-run not available)
#
# Exit codes inside the scope are NOT Halibut-visible (connection has died);
# they're encoded as status-file "status" field values above.
# ==============================================================================
set -euo pipefail

# ── Arch detection MUST run before DOWNLOAD_URL is assigned ───────────────────
# The rendered DOWNLOAD_URL contains the shell variable RID (server leaves it
# un-expanded so one rendered script covers both x64 and arm64 agents). Under
# `set -u`, bash expands variables in double-quoted RHS at assignment time —
# if RID isn't set when we hit the DOWNLOAD_URL line, the shell aborts with
# "RID: unbound variable" before anything useful happens.
# Regression pinned by Script_RidAssignedBeforeFirstExpansion_StrictModeSafe.
ARCH=$(uname -m)
case "$ARCH" in
  x86_64)         RID="linux-x64"   ;;
  aarch64|arm64)  RID="linux-arm64" ;;
  *) echo "::error:: Unsupported architecture: $ARCH"; exit 1 ;;
esac

TARGET_VERSION="{{TARGET_VERSION}}"
DOWNLOAD_URL="{{DOWNLOAD_URL}}"
EXPECTED_SHA256="{{EXPECTED_SHA256}}"
INSTALL_DIR="{{INSTALL_DIR}}"
SERVICE_NAME="{{SERVICE_NAME}}"
SERVICE_USER="{{SERVICE_USER}}"
HEALTHCHECK_URL="{{HEALTHCHECK_URL}}"

LOCK_FILE="/tmp/squid-tentacle-upgrade-$TARGET_VERSION.lock"
STATUS_DIR="/var/lib/squid-tentacle"
STATUS_FILE="$STATUS_DIR/last-upgrade.json"

# ── Status file helper — atomic write via temp+rename ─────────────────────────
# Server reads this file via the tentacle's Capabilities RPC on next health
# check. Must be atomic so a partial write during a service restart doesn't
# produce invalid JSON. Parent dir pre-created by install-tentacle.sh with
# owner=squid-tentacle; we `sudo mkdir -p` here too as a safety net for
# pre-Phase-1 installs that don't have it.
write_status() {
  local status="$1"
  local detail="${2:-}"
  local now
  now=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
  sudo mkdir -p "$STATUS_DIR" 2>/dev/null || true
  local tmp
  tmp=$(mktemp "$STATUS_DIR/.last-upgrade.XXXXXX.json" 2>/dev/null || mktemp)
  cat > "$tmp" <<STATUS_JSON
{
  "targetVersion": "$TARGET_VERSION",
  "status": "$status",
  "updatedAt": "$now",
  "detail": "$detail"
}
STATUS_JSON
  sudo mv "$tmp" "$STATUS_FILE" 2>/dev/null || mv "$tmp" "$STATUS_FILE" 2>/dev/null || true
  sudo chown "$SERVICE_USER:$SERVICE_USER" "$STATUS_FILE" 2>/dev/null || true
}

# ── systemd version probe (v239+ for --kill-who / --scope --collect) ──────────
SYSTEMD_VERSION=$(systemctl --version 2>/dev/null | head -1 | awk '{print $2}' | grep -oE '^[0-9]+' || echo 0)
if [ "${SYSTEMD_VERSION:-0}" -lt 239 ]; then
  echo "::error:: systemd v239+ required for transient scope support (this host has v$SYSTEMD_VERSION)."
  echo "Upgrade your distribution to Ubuntu 18.04+/Debian 10+/RHEL 8+, or pin SQUID_TARGET_LINUX_TENTACLE_VERSION to hold this host on the current version."
  exit 12
fi

# ── Idempotency guard (audit H-12 + N-8) ──────────────────────────────────────
# Per-version flock, /tmp so both the tentacle service-user and the post-exec
# root scope can access. Lock released automatically when our fd closes
# (SIGKILL-safe, no orphan lock on crash).
touch "$LOCK_FILE"
exec {LOCK_FD}>"$LOCK_FILE"

if ! flock -n "$LOCK_FD"; then
  echo "Upgrade to $TARGET_VERSION already in progress (flock held on $LOCK_FILE) — this delivery is a no-op."
  exit 0
fi

echo "=== Squid Tentacle upgrade ==="
echo "Target version : $TARGET_VERSION"
echo "Architecture   : $RID"
echo "Install dir    : $INSTALL_DIR"
echo "Service        : $SERVICE_NAME"
echo "systemd version: $SYSTEMD_VERSION"

# ── Stage directory + consolidated cleanup trap ──────────────────────────────
STAGE=$(mktemp -d -t squid-tentacle-upgrade-XXXXXX)

cleanup() {
    local ec=$?
    [ -n "${STAGE:-}" ] && [ -d "$STAGE" ] && rm -rf "$STAGE"
    exit $ec
}
trap cleanup EXIT

# ══════════════════════════════════════════════════════════════════════════════
# PHASE A: Pre-scope work — runs in tentacle cgroup, Halibut sees all logs.
# ══════════════════════════════════════════════════════════════════════════════

# Only do the heavy pre-flight + download once, in the PRE-scope phase.
# The post-scope continuation skips this entire phase via the sentinel env var.
if [ -z "${SQUID_UPGRADE_SCOPED:-}" ]; then

  write_status "IN_PROGRESS" "Pre-flight checks and download starting"

  # ── Pre-flight: disk space ──────────────────────────────────────────────────
  require_free_mb() {
    local path="$1" min_mb="$2" label="$3"
    local avail_kb
    avail_kb=$(df -P -k "$path" 2>/dev/null | awk 'NR==2 {print $4}')
    if [ -z "$avail_kb" ]; then
      echo "::warning:: Could not determine free space on $label ($path) — skipping disk pre-check"
      return 0
    fi
    local avail_mb=$((avail_kb / 1024))
    if [ "$avail_mb" -lt "$min_mb" ]; then
      echo "::error:: Insufficient disk on $label ($path): $avail_mb MB free, need $min_mb MB"
      write_status "FAILED" "Insufficient disk on $label: $avail_mb MB free, need $min_mb MB"
      exit 5
    fi
  }
  require_free_mb "$STAGE" 500 "/tmp staging"
  require_free_mb "$(dirname "$INSTALL_DIR")" 500 "install directory"

  # ── Pre-flight: URL reachable ───────────────────────────────────────────────
  if ! curl -fsSI --max-time 10 "$DOWNLOAD_URL" >/dev/null 2>&1; then
    echo "::error:: Target tarball not reachable: $DOWNLOAD_URL"
    echo "Verify: (1) the release tag '$TARGET_VERSION' is published on GitHub Releases; (2) outbound network is open from this agent."
    write_status "FAILED" "Target tarball not reachable: $DOWNLOAD_URL"
    exit 6
  fi

  # ── Download ────────────────────────────────────────────────────────────────
  ARCHIVE="$STAGE/tentacle.tar.gz"
  echo "Downloading $DOWNLOAD_URL ..."
  curl -fsSL --retry 3 --retry-delay 2 --max-time 120 "$DOWNLOAD_URL" -o "$ARCHIVE" || {
    echo "::error:: Download failed from $DOWNLOAD_URL"
    write_status "FAILED" "Download failed"
    exit 2
  }

  # ── Opportunistic SHA256 fetch ──────────────────────────────────────────────
  if [ -z "$EXPECTED_SHA256" ] && command -v curl >/dev/null 2>&1; then
    SHA_URL="${DOWNLOAD_URL}.sha256"
    FETCHED_SHA=$(curl -fsSL --max-time 10 "$SHA_URL" 2>/dev/null | awk '{print $1}' | tr -d '[:space:]' || true)
    if [ -n "$FETCHED_SHA" ] && echo "$FETCHED_SHA" | grep -qE '^[0-9a-fA-F]{64}$'; then
      EXPECTED_SHA256="$FETCHED_SHA"
      echo "Fetched expected SHA256 from $SHA_URL"
    else
      echo "::info:: No valid .sha256 companion file at $SHA_URL — skipping integrity check"
    fi
  fi

  # ── SHA256 verification ─────────────────────────────────────────────────────
  if [ -n "$EXPECTED_SHA256" ]; then
    ACTUAL_SHA256=$(sha256sum "$ARCHIVE" | awk '{print $1}')
    if [ "$ACTUAL_SHA256" != "$EXPECTED_SHA256" ]; then
      echo "::error:: SHA256 mismatch. Expected $EXPECTED_SHA256, got $ACTUAL_SHA256"
      write_status "FAILED" "SHA256 mismatch"
      exit 7
    fi
    echo "SHA256 verified: $ACTUAL_SHA256"
  fi

  # ── Extract + verify new binary (still in pre-scope phase) ──────────────────
  EXTRACT="$STAGE/extract"
  mkdir -p "$EXTRACT"
  tar xzf "$ARCHIVE" -C "$EXTRACT"

  NEW_BIN="$EXTRACT/Squid.Tentacle"
  [ -f "$NEW_BIN" ] || { echo "::error:: Extracted archive missing Squid.Tentacle binary"; write_status "FAILED" "Missing binary after extraction"; exit 3; }
  chmod +x "$NEW_BIN"

  if command -v ldd >/dev/null 2>&1; then
    if ldd "$NEW_BIN" 2>&1 | grep -i "not found\|version.*not found" | head -3 | grep -q . ; then
      echo "::error:: New binary has unresolved library dependencies (likely glibc mismatch):"
      ldd "$NEW_BIN" 2>&1 | grep -i "not found\|version.*not found"
      write_status "FAILED" "Binary failed libc/glibc compat check"
      exit 8
    fi
  fi

  if ! "$NEW_BIN" --version >/dev/null 2>&1 && ! "$NEW_BIN" version >/dev/null 2>&1; then
    echo "::warning:: New binary did not respond to --version probe. Continuing anyway."
  fi

  # ── SCOPE DETACH — re-exec self outside the tentacle cgroup ────────────────
  # From this point on we're safe to touch the service because we live in a
  # separate systemd scope. The Halibut connection WILL die when we restart
  # the tentacle; that's expected. Status file is how the server learns the
  # final outcome (see Octopus's ExitCode-on-disk pattern).
  #
  # Preserve the script body on disk because Halibut deletes its temp copy
  # of this script as soon as the parent bash exits.
  SCOPED_SCRIPT="/tmp/squid-upgrade-scoped-$$-$(date +%s).sh"
  cp "$0" "$SCOPED_SCRIPT"
  chmod +x "$SCOPED_SCRIPT"

  echo "Detaching to systemd scope for service restart (logs continue in /var/log/squid-tentacle-upgrade.log)..."
  exec sudo systemd-run --scope --collect --quiet \
    --unit="squid-upgrade-$TARGET_VERSION-$$" \
    --setenv=SQUID_UPGRADE_SCOPED=1 \
    --setenv=TARGET_VERSION="$TARGET_VERSION" \
    --setenv=INSTALL_DIR="$INSTALL_DIR" \
    --setenv=SERVICE_NAME="$SERVICE_NAME" \
    --setenv=SERVICE_USER="$SERVICE_USER" \
    --setenv=HEALTHCHECK_URL="$HEALTHCHECK_URL" \
    --setenv=STAGE="$STAGE" \
    --setenv=EXTRACT="$EXTRACT" \
    --setenv=STATUS_FILE="$STATUS_FILE" \
    --setenv=STATUS_DIR="$STATUS_DIR" \
    --setenv=LOCK_FILE="$LOCK_FILE" \
    bash "$SCOPED_SCRIPT" || {
      echo "::error:: Failed to spawn scope for upgrade handoff."
      write_status "FAILED" "systemd-run --scope failed to launch"
      exit 13
    }

  # Unreachable after exec — keeps grep-guided refactor honest.
fi

# ══════════════════════════════════════════════════════════════════════════════
# PHASE B: Post-scope — runs in transient scope, OUTSIDE tentacle cgroup.
#          Safe to restart the service now. Halibut is no longer connected;
#          status file is the sole outcome channel.
# ══════════════════════════════════════════════════════════════════════════════

# Redirect all scope output to a log file for post-mortem debugging (operator
# can `cat /var/log/squid-tentacle-upgrade.log` if status says FAILED).
LOG_FILE="/var/log/squid-tentacle-upgrade.log"
sudo touch "$LOG_FILE" 2>/dev/null || true
sudo chmod 644 "$LOG_FILE" 2>/dev/null || true
exec > >(sudo tee -a "$LOG_FILE") 2>&1

echo "=== In scope: continuing upgrade to $TARGET_VERSION at $(date -u +%FT%TZ) ==="

BAK_DIR="${INSTALL_DIR}.bak"

# ── Atomic swap (agent still running old binary throughout) ────────────────────
# Both mv calls get explicit failure handling — `set -e` aborting between
# the first and second mv would leave INSTALL_DIR missing (agent binaryless).
if [ -d "$BAK_DIR" ]; then sudo rm -rf "$BAK_DIR"; fi

INSTALL_DIR_BACKED_UP=0
if [ -d "$INSTALL_DIR" ]; then
  if ! sudo mv "$INSTALL_DIR" "$BAK_DIR"; then
    echo "::error:: Failed to back up current install ($INSTALL_DIR → $BAK_DIR). State unchanged; no damage."
    write_status "FAILED" "Failed to back up current install (pre-swap) — state unchanged"
    exit 10
  fi
  INSTALL_DIR_BACKED_UP=1
fi

if ! sudo mv "$EXTRACT" "$INSTALL_DIR"; then
  echo "::error:: Failed to move new install into place — attempting emergency restore"
  RESTORE_OK=0
  if [ "$INSTALL_DIR_BACKED_UP" = "1" ] && [ -d "$BAK_DIR" ]; then
    if sudo mv "$BAK_DIR" "$INSTALL_DIR"; then
      RESTORE_OK=1
    else
      echo "::error:: CRITICAL: emergency restore ALSO failed — agent binaryless; manual intervention required"
    fi
  fi
  if [ "$INSTALL_DIR_BACKED_UP" = "1" ] && [ "$RESTORE_OK" = "0" ]; then
    write_status "ROLLBACK_CRITICAL_FAILED" "Install move failed AND emergency restore failed — agent is binaryless"
    exit 9
  fi
  write_status "FAILED" "Install move failed (emergency restore ok or nothing to restore)"
  exit 11
fi

sudo chmod +x "$INSTALL_DIR/Squid.Tentacle"
[ -f "$INSTALL_DIR/Squid.Calamari" ] && sudo chmod +x "$INSTALL_DIR/Squid.Calamari"
sudo ln -sf "$INSTALL_DIR/Squid.Tentacle" "$INSTALL_DIR/squid-tentacle"
sudo ln -sf "$INSTALL_DIR/squid-tentacle" /usr/local/bin/squid-tentacle 2>/dev/null || true

if id "$SERVICE_USER" >/dev/null 2>&1; then
  sudo chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR" 2>/dev/null || true
fi

write_status "SWAPPED" "New binary in place; about to restart service"

# ── Restart service (safe: we are in our own scope cgroup) ────────────────────
echo "Restarting service $SERVICE_NAME ..."
HEALTH_OK=0
START_OK=1
timeout 90 sudo systemctl restart "$SERVICE_NAME" || {
  echo "::error:: systemctl restart exceeded 90s or returned non-zero — taking rollback path."
  START_OK=0
}

# ── Health check loop ────────────────────────────────────────────────────────
if [ "$START_OK" = "1" ]; then
  for i in $(seq 1 90); do
    if sudo systemctl is-active --quiet "$SERVICE_NAME"; then
      if command -v curl >/dev/null 2>&1 && curl -fsS --max-time 5 "$HEALTHCHECK_URL" >/dev/null 2>&1; then
        HEALTH_OK=1
        break
      fi
    fi
    sleep 1
  done
fi

# ── Post-restart version sanity ──────────────────────────────────────────────
if [ "$HEALTH_OK" = "1" ]; then
  RUNNING_VERSION=""
  if [ -x "$INSTALL_DIR/squid-tentacle" ]; then
    RUNNING_VERSION=$("$INSTALL_DIR/squid-tentacle" --version 2>/dev/null | head -1 | awk '{print $NF}' || true)
  fi
  if [ -n "$RUNNING_VERSION" ] && [ "$RUNNING_VERSION" != "$TARGET_VERSION" ]; then
    echo "::warning:: Service is healthy but binary reports version '$RUNNING_VERSION' (expected '$TARGET_VERSION')."
    echo "Treating as failure to avoid silent partial upgrades — rolling back."
    HEALTH_OK=0
  fi
fi

if [ "$HEALTH_OK" = "1" ]; then
  echo "✓ Upgrade to $TARGET_VERSION successful"
  write_status "SUCCESS" "Agent restarted, healthz OK, --version matches target"
  sudo rm -rf "$BAK_DIR"
  exit 0
fi

# ── Rollback ─────────────────────────────────────────────────────────────────
echo "::error:: Upgrade failed: $SERVICE_NAME did not become healthy after restart"
echo "Rolling back to previous version ..."
write_status "ROLLING_BACK" "Health check failed after restart"

timeout 30 sudo systemctl stop "$SERVICE_NAME" 2>/dev/null || true
sudo rm -rf "$INSTALL_DIR"
sudo mv "$BAK_DIR" "$INSTALL_DIR"
timeout 30 sudo systemctl start "$SERVICE_NAME"

ROLLBACK_OK=0
for i in $(seq 1 30); do
  if sudo systemctl is-active --quiet "$SERVICE_NAME"; then
    ROLLBACK_OK=1
    break
  fi
  sleep 1
done

if [ "$ROLLBACK_OK" = "1" ]; then
  echo "Rollback to previous version succeeded; agent is healthy on the old binary."
  write_status "ROLLED_BACK" "New binary failed health check; previous version restored and healthy"
  exit 4
fi

echo "::error:: CRITICAL: rollback also failed. Agent is in an unknown state."
write_status "ROLLBACK_CRITICAL_FAILED" "Agent in unknown state; manual intervention required"
exit 9
