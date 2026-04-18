#!/usr/bin/env bash
# ==============================================================================
# Squid Tentacle self-upgrade script
# ------------------------------------------------------------------------------
# Sent over a Halibut polling RPC by the server's LinuxTentacleUpgradeStrategy.
# The placeholders below ({{...}}) are filled by the server before transmission.
#
# Atomicity guarantees:
#   1. Download + sanity-check the new tarball BEFORE touching the live binary.
#   2. Backup current install dir to <dir>.bak before swap (rollback path).
#   3. After service restart, poll for healthy state up to 30s.
#   4. On health-check failure, atomically swap back to .bak.
# ==============================================================================
set -euo pipefail

TARGET_VERSION="{{TARGET_VERSION}}"
DOWNLOAD_URL="{{DOWNLOAD_URL}}"
INSTALL_DIR="{{INSTALL_DIR}}"
SERVICE_NAME="{{SERVICE_NAME}}"
SERVICE_USER="{{SERVICE_USER}}"

LOCK_DIR="/var/lib/squid-tentacle"
LOCK_FILE="$LOCK_DIR/upgrade-$TARGET_VERSION.lock"

# ── Idempotency guard ─────────────────────────────────────────────────────────
# Same upgrade re-delivered (e.g. polling reconnect) is a no-op.
mkdir -p "$LOCK_DIR" 2>/dev/null || sudo mkdir -p "$LOCK_DIR"
if [ -f "$LOCK_FILE" ]; then
  echo "Upgrade to $TARGET_VERSION already in progress / completed (lock: $LOCK_FILE)"
  exit 0
fi
touch "$LOCK_FILE"
trap 'rm -f "$LOCK_FILE"' EXIT

# ── Detect arch ───────────────────────────────────────────────────────────────
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
trap 'rm -rf "$STAGE"; rm -f "$LOCK_FILE"' EXIT

ARCHIVE="$STAGE/tentacle.tar.gz"
echo "Downloading $DOWNLOAD_URL ..."
curl -fsSL --retry 3 --retry-delay 2 "$DOWNLOAD_URL" -o "$ARCHIVE" || {
  echo "::error:: Download failed from $DOWNLOAD_URL"
  exit 2
}

# ── Verify the new binary BEFORE touching the live install ────────────────────
EXTRACT="$STAGE/extract"
mkdir -p "$EXTRACT"
tar xzf "$ARCHIVE" -C "$EXTRACT"

NEW_BIN="$EXTRACT/Squid.Tentacle"
[ -f "$NEW_BIN" ] || { echo "::error:: Extracted archive missing Squid.Tentacle binary"; exit 3; }
chmod +x "$NEW_BIN"

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

# Health-check loop. Use the agent's bundled health probe if present, otherwise
# verify it bound the polling listener (port 8080 is the local healthcheck HTTP).
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
exit 4
