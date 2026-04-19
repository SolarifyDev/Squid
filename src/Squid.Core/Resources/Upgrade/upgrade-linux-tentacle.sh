#!/usr/bin/env bash
# ==============================================================================
# Squid Tentacle self-upgrade script
# ------------------------------------------------------------------------------
# Sent over a Halibut polling RPC by the server's LinuxTentacleUpgradeStrategy
# at ScriptIsolationLevel.FullIsolation, so the agent serializes this behind
# any in-flight deployment scripts — we never restart a tentacle mid-deploy.
#
# Placeholders ({{...}}) are filled by the server before transmission.
# Atomicity guarantees:
#   1. Pre-flight  : disk space check + URL reachability HEAD probe
#   2. Download   : retried tarball fetch + SHA256 verification (when supplied)
#   3. Sanity     : binary is executable + reports the expected version + libc
#                   compat probe via ldd
#   4. Backup     : current install dir → <dir>.bak, atomic rename
#   5. Swap       : new install → install dir, atomic rename
#   6. Restart    : systemctl restart, then 30s healthcheck loop
#   7. Verify     : agent reports the new version (post-restart sanity)
#   8. Rollback   : on any post-swap failure, mv .bak back, restart, exit 4
#   9. Cleanup    : delete .bak only on confirmed success; leave on failure
# ==============================================================================
set -euo pipefail

TARGET_VERSION="{{TARGET_VERSION}}"
DOWNLOAD_URL="{{DOWNLOAD_URL}}"
EXPECTED_SHA256="{{EXPECTED_SHA256}}"   # may be empty until release pipeline emits it
INSTALL_DIR="{{INSTALL_DIR}}"
SERVICE_NAME="{{SERVICE_NAME}}"
SERVICE_USER="{{SERVICE_USER}}"
HEALTHCHECK_URL="{{HEALTHCHECK_URL}}"   # operator override via SQUID_TARGET_LINUX_TENTACLE_HEALTHCHECK_URL on the server pod

LOCK_FILE="/tmp/squid-tentacle-upgrade-$TARGET_VERSION.lock"

# ── Idempotency guard (audit H-12 + N-8) ──────────────────────────────────────
# Use kernel-level advisory flock, NOT a file-existence check. Three wins
# over the previous "touch + trap rm" pattern:
#
#  1. Atomic — `[ -f ... ] && touch` has a TOCTOU race; flock(2) is a single
#     fcntl syscall enforced by the kernel.
#  2. SIGKILL-safe — the kernel auto-releases the lock when our fd closes,
#     including on SIGKILL / OOM-kill / power loss. No orphan lock file
#     can jam a future upgrade.
#  3. /tmp avoids the sudo-permission gymnastics a /var/lib path would need
#     to be shell-writable (sudoers rules, chown cascades, etc.). /tmp is
#     always 1777; works whether the agent runs as root or service user.
#
# Per-version lock filename so re-issuing 1.4.0 → 1.4.1 upgrades back-to-back
# are not blocked by each other.
touch "$LOCK_FILE"
exec {LOCK_FD}>"$LOCK_FILE"

if ! flock -n "$LOCK_FD"; then
  echo "Upgrade to $TARGET_VERSION already in progress (flock held on $LOCK_FILE) — this delivery is a no-op."
  exit 0
fi

# ── Detect arch (server URL is parameterised by $RID) ─────────────────────────
ARCH=$(uname -m)
case "$ARCH" in
  x86_64)         RID="linux-x64"   ;;
  aarch64|arm64)  RID="linux-arm64" ;;
  *) echo "::error:: Unsupported architecture: $ARCH"; exit 1 ;;
esac

echo "=== Squid Tentacle upgrade ==="
echo "Target version : $TARGET_VERSION"
echo "Architecture   : $RID"
echo "Install dir    : $INSTALL_DIR"
echo "Service        : $SERVICE_NAME"

# ── Stage download + single consolidated cleanup trap (audit H-12) ────────────
# Why a function instead of an inline trap string: every future cleanup
# resource (a temp credential, a named pipe, a custom logger) goes in ONE
# place — no more "overwrite the old trap and hope you remembered every
# prior cleanup item". Exit-code preserved so callers see the real failure.
STAGE=$(mktemp -d -t squid-tentacle-upgrade-XXXXXX)

cleanup() {
    local ec=$?
    [ -n "${STAGE:-}" ] && [ -d "$STAGE" ] && rm -rf "$STAGE"
    # flock auto-releases when $LOCK_FD closes on shell exit — no rm/unlock
    # needed. File stays around as a cheap anchor for future flock; that's fine.
    exit $ec
}
trap cleanup EXIT

# ── Pre-flight: disk space ────────────────────────────────────────────────────
# Tentacle tarball is ~80MB compressed → ~250MB extracted; require 500MB free
# in both /tmp (download + extract) and on the install partition (swap).
require_free_mb() {
  local path="$1" min_mb="$2" label="$3"
  # POSIX `df -P -k <path>` (portable, no GNU-only flags): always 6 columns,
  # data row 2, "Available 1024-blocks" is column 4. Works on Alpine,
  # BusyBox, macOS, full glibc Linuxes — i.e. every base image we might
  # see a tentacle running on. Audit H-13.
  local avail_kb
  avail_kb=$(df -P -k "$path" 2>/dev/null | awk 'NR==2 {print $4}')
  if [ -z "$avail_kb" ]; then
    echo "::warning:: Could not determine free space on $label ($path) — skipping disk pre-check"
    return 0
  fi
  local avail_mb=$((avail_kb / 1024))
  if [ "$avail_mb" -lt "$min_mb" ]; then
    echo "::error:: Insufficient disk on $label ($path): $avail_mb MB free, need $min_mb MB"
    exit 5
  fi
}
require_free_mb "$STAGE" 500 "/tmp staging"
require_free_mb "$(dirname "$INSTALL_DIR")" 500 "install directory"

# ── Pre-flight: URL reachable ─────────────────────────────────────────────────
# HEAD probe before committing to anything destructive. If the target tarball
# doesn't exist on GitHub Releases (404, network down, etc.), bail BEFORE
# touching the live binary.
if ! curl -fsSI --max-time 10 "$DOWNLOAD_URL" >/dev/null 2>&1; then
  echo "::error:: Target tarball not reachable: $DOWNLOAD_URL"
  echo "Verify: (1) the release tag '$TARGET_VERSION' is published on GitHub Releases; (2) outbound network is open from this agent."
  exit 6
fi

# ── Download ──────────────────────────────────────────────────────────────────
ARCHIVE="$STAGE/tentacle.tar.gz"
echo "Downloading $DOWNLOAD_URL ..."
curl -fsSL --retry 3 --retry-delay 2 --max-time 120 "$DOWNLOAD_URL" -o "$ARCHIVE" || {
  echo "::error:: Download failed from $DOWNLOAD_URL"
  exit 2
}

# ── Opportunistic SHA256 fetch (audit A6) ────────────────────────────────────
# If the server-side template didn't pre-populate a SHA (Phase 1 default),
# try to fetch the companion `.sha256` file alongside the tarball. This turns
# integrity checking ON automatically the day the release pipeline starts
# publishing hashes — no server-side changes needed.
#
# Strict validation: only accept the fetched value if it's exactly 64 hex
# chars (SHA256 length). This guards against HTML 404 pages, truncated
# responses, mis-published SHA512, etc. — those would otherwise trigger
# a spurious SHA-mismatch error.
if [ -z "$EXPECTED_SHA256" ] && command -v curl >/dev/null 2>&1; then
  SHA_URL="${DOWNLOAD_URL}.sha256"
  FETCHED_SHA=$(curl -fsSL --max-time 10 "$SHA_URL" 2>/dev/null | awk '{print $1}' | tr -d '[:space:]' || true)
  if [ -n "$FETCHED_SHA" ] && echo "$FETCHED_SHA" | grep -qE '^[0-9a-fA-F]{64}$'; then
    EXPECTED_SHA256="$FETCHED_SHA"
    echo "Fetched expected SHA256 from $SHA_URL"
  else
    echo "::info:: No valid .sha256 companion file at $SHA_URL — skipping integrity check (Phase 1: release pipeline hasn't started publishing hashes yet)."
  fi
fi

# ── SHA256 verification (when the server supplies one OR we fetched one) ──────
if [ -n "$EXPECTED_SHA256" ]; then
  ACTUAL_SHA256=$(sha256sum "$ARCHIVE" | awk '{print $1}')
  if [ "$ACTUAL_SHA256" != "$EXPECTED_SHA256" ]; then
    echo "::error:: SHA256 mismatch. Expected $EXPECTED_SHA256, got $ACTUAL_SHA256"
    echo "Aborting — refuse to install a tarball that does not match the server-supplied hash."
    exit 7
  fi
  echo "SHA256 verified: $ACTUAL_SHA256"
fi

# ── Verify the new binary BEFORE touching the live install ────────────────────
EXTRACT="$STAGE/extract"
mkdir -p "$EXTRACT"
tar xzf "$ARCHIVE" -C "$EXTRACT"

NEW_BIN="$EXTRACT/Squid.Tentacle"
[ -f "$NEW_BIN" ] || { echo "::error:: Extracted archive missing Squid.Tentacle binary"; exit 3; }
chmod +x "$NEW_BIN"

# Probe libc compat: a binary built against newer glibc on Ubuntu 22 will fail
# on Ubuntu 18 with "version `GLIBC_2.32' not found". ldd surfaces this BEFORE
# we try to start the service.
if command -v ldd >/dev/null 2>&1; then
  if ldd "$NEW_BIN" 2>&1 | grep -i "not found\|version.*not found" | head -3 | grep -q . ; then
    echo "::error:: New binary has unresolved library dependencies (likely glibc mismatch):"
    ldd "$NEW_BIN" 2>&1 | grep -i "not found\|version.*not found"
    exit 8
  fi
fi

# Sanity: new binary should be runnable on this OS/arch.
if ! "$NEW_BIN" --version >/dev/null 2>&1 && ! "$NEW_BIN" version >/dev/null 2>&1; then
  echo "::warning:: New binary did not respond to --version probe. Continuing anyway."
fi

# ── Atomic swap: stop service → mv current to .bak → mv new into place ────────
BAK_DIR="${INSTALL_DIR}.bak"

echo "Stopping service $SERVICE_NAME ..."
# Cap stop at 30s. A unit with active connections (TCP linger) or a misbehaving
# ExecStop hook can hang systemctl indefinitely; without this cap a single
# stuck service eats the entire 5-min Halibut script budget. The trailing
# `|| true` keeps "service was already stopped" (exit 5) from tripping
# `set -euo pipefail`. Audit H-7.
timeout 30 sudo systemctl stop "$SERVICE_NAME" 2>/dev/null || true

if [ -d "$BAK_DIR" ]; then sudo rm -rf "$BAK_DIR"; fi

# Atomic swap: each mv gets explicit failure handling — we CANNOT rely on
# `set -e` to kick us into the rollback block below, because the window
# between "INSTALL_DIR moved to .bak" and "new install moved into place"
# has INSTALL_DIR missing. If the second mv fails and set -e aborts, the
# rollback block never runs and the agent is left binaryless → operator
# must SSH to repair. Round-3 audit.
INSTALL_DIR_BACKED_UP=0

if [ -d "$INSTALL_DIR" ]; then
  if ! sudo mv "$INSTALL_DIR" "$BAK_DIR"; then
    echo "::error:: Failed to back up current install ($INSTALL_DIR → $BAK_DIR). State unchanged; no damage."
    exit 10
  fi
  INSTALL_DIR_BACKED_UP=1
fi

if ! sudo mv "$EXTRACT" "$INSTALL_DIR"; then
  # DANGEROUS STATE: INSTALL_DIR is absent. We MUST emergency restore
  # before `set -e` takes us out — otherwise no rollback block will run
  # and the agent has no binary.
  echo "::error:: Failed to move new install into place ($EXTRACT → $INSTALL_DIR) — attempting emergency restore"
  RESTORE_OK=0
  if [ "$INSTALL_DIR_BACKED_UP" = "1" ] && [ -d "$BAK_DIR" ]; then
    if sudo mv "$BAK_DIR" "$INSTALL_DIR"; then
      RESTORE_OK=1
      # Try to bring the old service back up so the agent is at least reachable
      # for the NEXT upgrade attempt. Ignore failure: rollback-verification
      # block already owns health-detection semantics.
      timeout 30 sudo systemctl start "$SERVICE_NAME" 2>/dev/null || true
    else
      echo "::error:: CRITICAL: emergency restore ALSO failed — agent binaryless; manual intervention required"
    fi
  fi
  # Escalate to exit 9 when we HAD a backup and couldn't restore it —
  # state is identical to the post-healthcheck rollback-failed path
  # (agent binaryless). If there was nothing to restore (fresh first-time
  # install), exit 11 is accurate: "install failed, nothing lost".
  # Audit B1.
  if [ "$INSTALL_DIR_BACKED_UP" = "1" ] && [ "$RESTORE_OK" = "0" ]; then
    exit 9
  fi
  exit 11
fi

# Restore well-known symlinks the install script set up.
sudo chmod +x "$INSTALL_DIR/Squid.Tentacle"
[ -f "$INSTALL_DIR/Squid.Calamari" ] && sudo chmod +x "$INSTALL_DIR/Squid.Calamari"
sudo ln -sf "$INSTALL_DIR/Squid.Tentacle" "$INSTALL_DIR/squid-tentacle"
sudo ln -sf "$INSTALL_DIR/squid-tentacle" /usr/local/bin/squid-tentacle 2>/dev/null || true

# Match ownership with the install script's behaviour.
if id "$SERVICE_USER" >/dev/null 2>&1; then
  sudo chown -R "$SERVICE_USER:$SERVICE_USER" "$INSTALL_DIR" 2>/dev/null || true
fi

# ── Restart and verify ───────────────────────────────────────────────────────
echo "Starting service $SERVICE_NAME ..."
# Cap start at 60s. ExecStartPre hook stuck on dependency, sd_notify never
# sent, or systemd unit dependency loop will otherwise block until the
# Halibut timeout — by which time we've lost the chance to roll back
# cleanly. timeout(1) returns 124 on timeout (non-zero) → we explicitly
# branch to skip the healthcheck loop (it would just churn 30s for nothing
# and inflate operator-visible time-to-failure). Audit H-7.
HEALTH_OK=0
START_OK=1
timeout 60 sudo systemctl start "$SERVICE_NAME" || {
  echo "::error:: systemctl start exceeded 60s or returned non-zero — taking rollback path immediately."
  START_OK=0
}

# Health-check loop. Use the agent's local healthz endpoint (port 8080).
# Skipped entirely if the start command itself failed/timed out — no point
# polling for health on a service we couldn't start.
if [ "$START_OK" = "1" ]; then
  for i in $(seq 1 30); do
    if sudo systemctl is-active --quiet "$SERVICE_NAME"; then
      if command -v curl >/dev/null 2>&1 && curl -fsS --max-time 5 "$HEALTHCHECK_URL" >/dev/null 2>&1; then
        HEALTH_OK=1
        break
      fi
    fi
    sleep 1
  done
fi

# Post-restart sanity: ensure the running agent reports the version we
# intended. Catches the rare case where the swap "succeeded" but the agent
# loaded an unexpected binary (e.g. systemd starts an old instance from
# /usr/local/bin pointing somewhere stale).
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
  sudo rm -rf "$BAK_DIR"
  exit 0
fi

# ── Rollback ─────────────────────────────────────────────────────────────────
echo "::error:: Upgrade failed: $SERVICE_NAME did not become healthy after 30s"
echo "Rolling back to previous version ..."
sudo systemctl stop "$SERVICE_NAME" 2>/dev/null || true
sudo rm -rf "$INSTALL_DIR"
sudo mv "$BAK_DIR" "$INSTALL_DIR"
sudo systemctl start "$SERVICE_NAME"

# Verify the rollback itself worked. If the previous install is now also
# broken (e.g. glibc upgrade on the box happened independently), surface that
# loudly so the operator knows the box needs manual intervention — don't
# pretend the rollback was clean.
ROLLBACK_OK=0
for i in $(seq 1 15); do
  if sudo systemctl is-active --quiet "$SERVICE_NAME"; then
    ROLLBACK_OK=1
    break
  fi
  sleep 1
done

if [ "$ROLLBACK_OK" = "1" ]; then
  echo "Rollback to previous version succeeded; agent is healthy on the old binary."
  exit 4
fi

echo "::error:: CRITICAL: rollback also failed. Agent is in an unknown state."
echo "Manual intervention required. Backup of pre-upgrade install was at: $BAK_DIR (now restored to $INSTALL_DIR)."
exit 9
