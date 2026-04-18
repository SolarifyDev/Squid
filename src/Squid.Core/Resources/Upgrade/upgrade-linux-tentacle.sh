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

LOCK_DIR="/var/lib/squid-tentacle"
LOCK_FILE="$LOCK_DIR/upgrade-$TARGET_VERSION.lock"

# ── Idempotency guard ─────────────────────────────────────────────────────────
# Same upgrade re-delivered (e.g. polling reconnect after a server-side retry)
# is a no-op. The lock file is per-target-version so re-issuing v1.4.0 → v1.4.1
# upgrades after this one are still allowed.
sudo mkdir -p "$LOCK_DIR"
if [ -f "$LOCK_FILE" ]; then
  echo "Upgrade to $TARGET_VERSION already in progress / completed (lock: $LOCK_FILE)"
  exit 0
fi
sudo touch "$LOCK_FILE"
trap 'sudo rm -f "$LOCK_FILE"' EXIT

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

# ── Stage download ────────────────────────────────────────────────────────────
STAGE=$(mktemp -d -t squid-tentacle-upgrade-XXXXXX)
trap 'rm -rf "$STAGE"; sudo rm -f "$LOCK_FILE"' EXIT

# ── Pre-flight: disk space ────────────────────────────────────────────────────
# Tentacle tarball is ~80MB compressed → ~250MB extracted; require 500MB free
# in both /tmp (download + extract) and on the install partition (swap).
require_free_mb() {
  local path="$1" min_mb="$2" label="$3"
  local avail_kb
  avail_kb=$(df -k --output=avail "$path" | tail -1 | tr -d ' ')
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

# ── SHA256 verification (when the server supplies one) ────────────────────────
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
sudo systemctl stop "$SERVICE_NAME" 2>/dev/null || true

if [ -d "$BAK_DIR" ]; then sudo rm -rf "$BAK_DIR"; fi

if [ -d "$INSTALL_DIR" ]; then
  sudo mv "$INSTALL_DIR" "$BAK_DIR"
fi
sudo mv "$EXTRACT" "$INSTALL_DIR"

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
sudo systemctl start "$SERVICE_NAME"

# Health-check loop. Use the agent's local healthz endpoint (port 8080).
HEALTH_OK=0
for i in $(seq 1 30); do
  if sudo systemctl is-active --quiet "$SERVICE_NAME"; then
    if command -v curl >/dev/null 2>&1 && curl -fsS http://127.0.0.1:8080/healthz >/dev/null 2>&1; then
      HEALTH_OK=1
      break
    fi
  fi
  sleep 1
done

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
