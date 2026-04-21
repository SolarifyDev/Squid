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
# ARCHITECTURE (Phase 1 + 2):
#   Phase A (in tentacle cgroup, Halibut sees logs):
#     • Common pre-flight (arch, systemd version, flock idempotency, status).
#     • INSTALL_METHODS block (server-injected): ordered apt -> yum ->
#       tarball-marker dispatch (Phase 2). The first method whose detection
#       branch matches sets INSTALL_OK=1.
#     • If INSTALL_METHOD=tarball, the existing tarball download/verify/extract
#       block runs (separate from the marker so its ~80 lines stay in the
#       template, not in C#).
#     • Re-exec into a transient `systemd-run --scope` so the next phase
#       survives the upcoming `systemctl restart squid-tentacle`.
#
#   Phase B (in scope cgroup, separate from tentacle):
#     • Conditional swap: tarball method requires `mv .bak / mv staging`;
#       apt/yum methods are no-ops here (the package manager already wrote
#       /opt/squid-tentacle/).
#     • `systemctl restart squid-tentacle`.
#     • Health poll + version verify.
#     • Status file at /var/lib/squid-tentacle/last-upgrade.json (Octopus
#       parity: server reads on next health check via Capabilities RPC).
#
# Status progression:
#   IN_PROGRESS → SWAPPED → SUCCESS
#                          → ROLLED_BACK (tarball: .bak restored; apt/yum: see ROLLBACK_NEEDED)
#                          → ROLLBACK_NEEDED (apt/yum: operator must downgrade manually)
#                          → ROLLBACK_CRITICAL_FAILED (rollback itself failed)
#
# Exit codes (Halibut-visible, from PRE-scope phase):
#   0   — dispatched to scope OR no-op (already on target version)
#   1   — unsupported architecture
#   2   — download failure (tarball method only)
#   3   — missing binary in extracted archive
#   5   — insufficient disk space
#   6   — target tarball URL not reachable (tarball method only)
#   7   — SHA256 mismatch
#   8   — libc/glibc compat probe found unresolved deps in new binary
#   12  — systemd too old for `systemd-run --scope` (needs v239+)
#   13  — failed to spawn the scope
#   14  — no install method succeeded (apt+yum+tarball all skipped or failed)
# ==============================================================================
set -euo pipefail

# ── Arch detection MUST run before DOWNLOAD_URL is assigned ───────────────────
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

STATUS_DIR="/var/lib/squid-tentacle"
STATUS_FILE="$STATUS_DIR/last-upgrade.json"
# Lock file lives in the service-user-owned state dir (NOT /tmp) so a stale
# root-owned lock left by a previous debug session / failed scope run can't
# block squid-tentacle's Phase A from starting a new upgrade. /tmp's sticky
# bit means the service user can't unlink a root-owned file there without
# sudo — a state we'd then need another sudoers rule to recover from.
# STATUS_DIR is always squid-tentacle-owned (set up by install-tentacle.sh),
# and Phase B's touch of an existing squid-tentacle-owned file doesn't
# change ownership, so the lock stays squid-tentacle-writable forever.
LOCK_FILE="$STATUS_DIR/upgrade-$TARGET_VERSION.lock"

# ── Status file helper — atomic write via temp+rename ─────────────────────────
# Deterministic tempfile path so the sudoers `mv` rule can match precisely:
# always /tmp/squid-upgrade-status.XXXXXX (world-writable, no sudo to write),
# then `sudo mv` it into STATUS_DIR (root-owned, needs sudo). Previous
# implementation tried `mktemp $STATUS_DIR/.last-upgrade.XXX` first and fell
# back to bare `mktemp` (= /tmp/tmp.XXX) on failure — making the eventual
# path ambiguous and breaking the sudoers pattern match when the fallback
# fired. See the install-tentacle.sh sudoers rules for the matching pattern.
write_status() {
  local status="$1"
  local detail="${2:-}"
  local now
  now=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
  sudo mkdir -p "$STATUS_DIR" 2>/dev/null || true
  local tmp
  tmp=$(mktemp /tmp/squid-upgrade-status.XXXXXX)
  cat > "$tmp" <<STATUS_JSON
{
  "targetVersion": "$TARGET_VERSION",
  "installMethod": "${INSTALL_METHOD:-unknown}",
  "status": "$status",
  "updatedAt": "$now",
  "detail": "$detail"
}
STATUS_JSON
  # Both 2>/dev/null || true on each step — status-file write failures must
  # never abort the upgrade itself. If sudoers is misconfigured the upgrade
  # still succeeds; only the "last upgrade" JSON won't update. Server falls
  # back to the next Capabilities health check to infer outcome from version.
  sudo mv "$tmp" "$STATUS_FILE" 2>/dev/null || { rm -f "$tmp"; return; }
  sudo chown "$SERVICE_USER:$SERVICE_USER" "$STATUS_FILE" 2>/dev/null || true
}

# ── systemd version probe (v239+ for --scope --collect) ──────────────────────
SYSTEMD_VERSION=$(systemctl --version 2>/dev/null | head -1 | awk '{print $2}' | grep -oE '^[0-9]+' || echo 0)
if [ "${SYSTEMD_VERSION:-0}" -lt 239 ]; then
  echo "::error:: systemd v239+ required for transient scope support (this host has v$SYSTEMD_VERSION)."
  exit 12
fi

# ── Idempotency guard ────────────────────────────────────────────────────────
# Ensure lock dir exists (fresh installs that pre-date the post-Phase-2-Part-2
# install-tentacle.sh may not have created it yet). sudoers allows this via
# `sudo mkdir -p /var/lib/squid-tentacle` — if the rule isn't there we
# swallow the failure and the subsequent `touch` surfaces a clearer error.
sudo mkdir -p "$STATUS_DIR" 2>/dev/null || true

# Open read-only for flock. flock works on read-only fds, so this succeeds
# even if a stray root-owned lock file blocks our write. The touch-if-missing
# creates the file the FIRST time (while the parent dir is squid-tentacle-
# owned, the file inherits our ownership).
[ -f "$LOCK_FILE" ] || touch "$LOCK_FILE"
exec {LOCK_FD}< "$LOCK_FILE"

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

# ── Stage directory + cleanup trap ──────────────────────────────────────────
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

if [ -z "${SQUID_UPGRADE_SCOPED:-}" ]; then

  # State the dispatch fills in. INSTALL_OK is the truth source for "did
  # SOME method succeed". INSTALL_METHOD records which one for the scope
  # phase to branch on. OLD_VERSION_* are recorded by apt/yum methods so
  # the operator-visible status detail can mention what to downgrade to.
  INSTALL_OK=0
  INSTALL_METHOD=""
  OLD_VERSION_APT=""
  OLD_VERSION_RPM=""

  write_status "IN_PROGRESS" "Selecting upgrade method"

  # ── Pre-flight: disk space (applies to ALL methods) ──────────────────────
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

  # ── Method dispatch (apt → yum → tarball-marker, server-injected) ────────
  # Each snippet's contract: short-circuit when INSTALL_OK=1 (a higher-
  # priority method already succeeded), otherwise probe the host and either
  # install + set INSTALL_OK=1 or fall through silently.
  {{INSTALL_METHODS}}

  # ── Tarball install (only when no package-manager method matched) ────────
  # Kept inline (not in the INSTALL_METHODS placeholder block) because the
  # curl / SHA / extract logic is ~80 lines we don't want in C# string-builders.
  # The TarballUpgradeMethod marker above flipped INSTALL_METHOD=tarball
  # without setting INSTALL_OK — this block is what actually does the work.
  if [ "$INSTALL_METHOD" = "tarball" ] && [ "$INSTALL_OK" != "1" ]; then
    echo "[upgrade-method:tarball] Pre-flight: probing $DOWNLOAD_URL"

    # HEAD probe with retries. GitHub Releases from certain regions
    # (notably CN behind slow cross-border routes) routinely exceeds 10s
    # for a single handshake. --retry 2 + --retry-all-errors covers TCP
    # reset, DNS hiccup, and transient 5xx without false-positive exit 6.
    # --connect-timeout is separate from --max-time: connect caps the TLS
    # handshake specifically, max-time caps the whole operation.
    # Operator can override the URL base via
    # SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL for air-gap / mirror
    # scenarios — if they're hitting this line from China, pointing the
    # env var at a CN mirror fixes it permanently.
    if ! curl -fsSI --connect-timeout 15 --max-time 30 \
               --retry 2 --retry-delay 5 --retry-all-errors \
               "$DOWNLOAD_URL" >/dev/null 2>&1; then
      echo "::error:: Target tarball not reachable after 2 retries: $DOWNLOAD_URL"
      echo "::error:: Likely causes:"
      echo "  1. GitHub Releases is slow from this region (China → try again, or configure APT repo to use squid.solarifyai.com)"
      echo "  2. Tarball isn't published yet (recent tag — wait 1-2 min for build-publish-linux-tentacle.yml to finish)"
      echo "  3. Firewall blocks github.com (check outbound HTTPS egress)"
      echo "  4. For an air-gapped mirror, set SQUID_TARGET_LINUX_TENTACLE_DOWNLOAD_BASE_URL on the server pod"
      write_status "FAILED" "Target tarball not reachable after retries: $DOWNLOAD_URL"
      exit 6
    fi

    ARCHIVE="$STAGE/tentacle.tar.gz"
    echo "[upgrade-method:tarball] Downloading $DOWNLOAD_URL ..."
    # --max-time 300 (was 120): cross-border ~65MB download can hit 2-3 min
    # from CN at peak hours. 5 min is a generous budget that still fails
    # loudly for true network blackholes.
    curl -fsSL --connect-timeout 15 --retry 3 --retry-delay 5 --retry-all-errors \
         --max-time 300 "$DOWNLOAD_URL" -o "$ARCHIVE" || {
      echo "::error:: Download failed from $DOWNLOAD_URL"
      write_status "FAILED" "Download failed"
      exit 2
    }

    # Opportunistic SHA256 fetch — turns integrity-checking on automatically
    # the day the release pipeline starts publishing per-version .sha256.
    if [ -z "$EXPECTED_SHA256" ] && command -v curl >/dev/null 2>&1; then
      SHA_URL="${DOWNLOAD_URL}.sha256"
      FETCHED_SHA=$(curl -fsSL --max-time 10 "$SHA_URL" 2>/dev/null | awk '{print $1}' | tr -d '[:space:]' || true)
      if [ -n "$FETCHED_SHA" ] && echo "$FETCHED_SHA" | grep -qE '^[0-9a-fA-F]{64}$'; then
        EXPECTED_SHA256="$FETCHED_SHA"
        echo "[upgrade-method:tarball] Fetched expected SHA256 from $SHA_URL"
      else
        echo "::info:: No valid .sha256 companion at $SHA_URL — skipping integrity check"
      fi
    fi

    if [ -n "$EXPECTED_SHA256" ]; then
      ACTUAL_SHA256=$(sha256sum "$ARCHIVE" | awk '{print $1}')
      if [ "$ACTUAL_SHA256" != "$EXPECTED_SHA256" ]; then
        echo "::error:: SHA256 mismatch. Expected $EXPECTED_SHA256, got $ACTUAL_SHA256"
        write_status "FAILED" "SHA256 mismatch"
        exit 7
      fi
      echo "[upgrade-method:tarball] SHA256 verified: $ACTUAL_SHA256"
    fi

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

    # Version probe — use the `version` subcommand (added in the same
    # Phase 2 Part 2 follow-up commit that added VersionCommand.cs).
    # DO NOT use `--version` here: the CLI's CommandResolver treats any
    # arg starting with `-` as a config flag and falls through to
    # RunCommand, which starts the agent and never exits — bash would
    # hang forever waiting for the process. `version` is a proper
    # subcommand that prints + exits 0.
    #
    # --max-time via `timeout` guards against OLD tentacle binaries that
    # predate VersionCommand: for those, `version` is an unknown verb,
    # resolver returns exit 1, our fallback warning fires.
    PROBED_VERSION=$(timeout 5 "$NEW_BIN" version 2>/dev/null | head -1 || true)
    if [ -n "$PROBED_VERSION" ]; then
      echo "[upgrade-method:tarball] Probed version: $PROBED_VERSION"
      if [ "$PROBED_VERSION" != "$TARGET_VERSION" ]; then
        echo "::warning:: New binary reports $PROBED_VERSION but target was $TARGET_VERSION. Continuing anyway; Phase B version-verify catches mismatch after swap."
      fi
    else
      echo "::warning:: New binary did not respond to 'version' subcommand. Continuing anyway."
    fi

    INSTALL_OK=1
    echo "[upgrade-method:tarball] Tarball downloaded + verified at $EXTRACT"
  fi

  # ── Bail if no method worked ────────────────────────────────────────────────
  if [ "$INSTALL_OK" != "1" ]; then
    echo "::error:: No upgrade method succeeded. Tried: apt, yum, tarball."
    write_status "FAILED" "No upgrade method succeeded — apt/yum repos not configured AND tarball download failed"
    exit 14
  fi

  echo "[upgrade] Method selected: $INSTALL_METHOD"

  # ── SCOPE DETACH ─────────────────────────────────────────────────────────
  # Re-exec self in a transient systemd scope so the upcoming `systemctl
  # restart squid-tentacle` doesn't kill us along with the tentacle service.
  # Halibut connection dies here (expected); the next health check reads
  # /var/lib/squid-tentacle/last-upgrade.json for the final outcome.
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
    --setenv=EXTRACT="${EXTRACT:-}" \
    --setenv=STATUS_FILE="$STATUS_FILE" \
    --setenv=STATUS_DIR="$STATUS_DIR" \
    --setenv=LOCK_FILE="$LOCK_FILE" \
    --setenv=INSTALL_METHOD="$INSTALL_METHOD" \
    --setenv=OLD_VERSION_APT="$OLD_VERSION_APT" \
    --setenv=OLD_VERSION_RPM="$OLD_VERSION_RPM" \
    bash "$SCOPED_SCRIPT" || {
      echo "::error:: Failed to spawn scope for upgrade handoff."
      write_status "FAILED" "systemd-run --scope failed to launch"
      exit 13
    }
fi

# ══════════════════════════════════════════════════════════════════════════════
# PHASE B: Post-scope — runs in transient scope, OUTSIDE tentacle cgroup.
# ══════════════════════════════════════════════════════════════════════════════

LOG_FILE="/var/log/squid-tentacle-upgrade.log"
sudo touch "$LOG_FILE" 2>/dev/null || true
sudo chmod 644 "$LOG_FILE" 2>/dev/null || true
exec > >(sudo tee -a "$LOG_FILE") 2>&1

echo "=== In scope: continuing upgrade to $TARGET_VERSION (method=$INSTALL_METHOD) at $(date -u +%FT%TZ) ==="

BAK_DIR="${INSTALL_DIR}.bak"

# ── Conditional swap (tarball only — apt/yum already wrote files via dpkg/rpm)
if [ "$INSTALL_METHOD" = "tarball" ]; then
  if [ -d "$BAK_DIR" ]; then sudo rm -rf "$BAK_DIR"; fi

  INSTALL_DIR_BACKED_UP=0
  if [ -d "$INSTALL_DIR" ]; then
    if ! sudo mv "$INSTALL_DIR" "$BAK_DIR"; then
      echo "::error:: Failed to back up current install ($INSTALL_DIR → $BAK_DIR). State unchanged; no damage."
      write_status "FAILED" "Failed to back up current install (pre-swap)"
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
        echo "::error:: CRITICAL: emergency restore ALSO failed — agent binaryless"
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
else
  # apt/yum: dpkg/rpm transactionally replaced /opt/squid-tentacle/* during
  # the install step. No swap needed; the binary is on disk waiting for the
  # service restart below to load it.
  echo "[upgrade] No swap needed for INSTALL_METHOD=$INSTALL_METHOD (package manager owns file placement)"
fi

write_status "SWAPPED" "New binary in place via $INSTALL_METHOD; about to restart service"

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
# Use `version` subcommand (not --version — that falls through to RunCommand
# per CommandResolver and would hang bash forever waiting on the agent to
# exit, which it never does). timeout 5s caps old-binary fallback: if the
# currently-installed binary predates VersionCommand, `version` is an unknown
# verb, CLI exits non-zero, and we skip the comparison (treat as healthy).
if [ "$HEALTH_OK" = "1" ]; then
  RUNNING_VERSION=""
  if [ -x "$INSTALL_DIR/squid-tentacle" ]; then
    RUNNING_VERSION=$(timeout 5 "$INSTALL_DIR/squid-tentacle" version 2>/dev/null | head -1 || true)
  fi
  if [ -n "$RUNNING_VERSION" ] && [ "$RUNNING_VERSION" != "$TARGET_VERSION" ]; then
    echo "::warning:: Service is healthy but binary reports version '$RUNNING_VERSION' (expected '$TARGET_VERSION')."
    echo "Treating as failure to avoid silent partial upgrades — rolling back."
    HEALTH_OK=0
  fi
fi

if [ "$HEALTH_OK" = "1" ]; then
  echo "✓ Upgrade to $TARGET_VERSION successful via $INSTALL_METHOD"
  write_status "SUCCESS" "Agent restarted, healthz OK, --version matches target (method=$INSTALL_METHOD)"
  if [ "$INSTALL_METHOD" = "tarball" ]; then
    sudo rm -rf "$BAK_DIR"
  fi
  exit 0
fi

# ── Rollback ─────────────────────────────────────────────────────────────────
echo "::error:: Upgrade failed: $SERVICE_NAME did not become healthy after restart"

if [ "$INSTALL_METHOD" = "tarball" ]; then
  echo "Rolling back via .bak directory (tarball method) ..."
  write_status "ROLLING_BACK" "Health check failed after restart — restoring previous version from $BAK_DIR"

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
fi

# ── apt/yum: no auto-rollback (Phase 2 Part 2 v1) ──────────────────────────
# Auto-downgrade isn't implemented in v1. The per-method hint below points
# at whichever rollback path is actually feasible:
#
#   apt: reprepro's APT repo only keeps the LATEST version (one-version-per-
#     package-per-arch model). Old versions aren't in `stable` anymore, so
#     `apt-get install --allow-downgrades squid-tentacle=OLD` fails.
#     Workaround: download the retained-forever .deb from GitHub Releases
#     + `dpkg -i`, then systemctl restart.
#
#   yum: createrepo_c's repomd indexes every .rpm we leave in the pool; our
#     publish workflow keeps the last 5 per arch. `dnf downgrade` resolves
#     cleanly as long as OLD_VERSION is within that window.
case "$INSTALL_METHOD" in
  apt)
    ARCH_DEB=$(dpkg --print-architecture 2>/dev/null || echo amd64)
    DOWNGRADE_CMD="curl -fsSLo /tmp/squid-tentacle-rollback.deb \"https://github.com/SolarifyDev/Squid/releases/download/${OLD_VERSION_APT:-<previous-version>}/squid-tentacle_${OLD_VERSION_APT:-<previous-version>}_${ARCH_DEB}.deb\" && sudo dpkg -i /tmp/squid-tentacle-rollback.deb && sudo systemctl restart $SERVICE_NAME"
    ;;
  yum)
    DOWNGRADE_CMD="sudo dnf downgrade -y squid-tentacle-${OLD_VERSION_RPM:-<previous-version>} && sudo systemctl restart $SERVICE_NAME"
    ;;
  *)
    DOWNGRADE_CMD="(unknown method '$INSTALL_METHOD' — manual intervention required)"
    ;;
esac

echo "::error:: $INSTALL_METHOD method — automatic rollback not implemented. To recover:"
echo "  $DOWNGRADE_CMD"
write_status "ROLLBACK_NEEDED" "$INSTALL_METHOD upgrade failed health check; manual rollback required: $DOWNGRADE_CMD"
exit 4
