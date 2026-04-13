#!/bin/bash
set -euo pipefail

# Squid Tentacle Linux Installer
# Usage: curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | bash
# Or:    bash install-tentacle.sh [--version 1.0.0] [--install-dir /opt/squid-tentacle]

BINARY_NAME="squid-tentacle"
VERSION="${TENTACLE_VERSION:-latest}"
INSTALL_DIR="${INSTALL_DIR:-/opt/squid-tentacle}"
DOWNLOAD_BASE="${DOWNLOAD_BASE:-https://github.com/SolarifyDev/Squid/releases}"

# Parse args
while [[ $# -gt 0 ]]; do
    case $1 in
        --version) VERSION="$2"; shift 2 ;;
        --install-dir) INSTALL_DIR="$2"; shift 2 ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

# Detect architecture
ARCH=$(uname -m)
case $ARCH in
    x86_64)  RID="linux-x64" ;;
    aarch64) RID="linux-arm64" ;;
    arm64)   RID="linux-arm64" ;;
    *) echo "Error: Unsupported architecture: $ARCH"; exit 1 ;;
esac

# Require root for default install dir
if [ "$INSTALL_DIR" = "/opt/squid-tentacle" ] && [ "$(id -u)" -ne 0 ]; then
    echo "Error: Root privileges required. Run with:"
    echo "  curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | sudo bash"
    echo ""
    echo "Or install to a user directory:"
    echo "  curl ... | INSTALL_DIR=\$HOME/.squid-tentacle bash"
    exit 1
fi

echo "=== Squid Tentacle Installer ==="
echo "Version:  ${VERSION}"
echo "Arch:     ${RID}"
echo "Install:  ${INSTALL_DIR}"
echo ""

# Download
if [ "$VERSION" = "latest" ]; then
    URL="${DOWNLOAD_BASE}/latest/download/${BINARY_NAME}-${RID}.tar.gz"
else
    URL="${DOWNLOAD_BASE}/download/v${VERSION}/${BINARY_NAME}-${VERSION}-${RID}.tar.gz"
fi

echo "Downloading from ${URL}..."
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

if ! curl -fsSL --retry 3 "$URL" -o "$TMP_DIR/tentacle.tar.gz"; then
    echo "Error: Download failed. Check the version or your network connection."
    echo "  Available releases: ${DOWNLOAD_BASE}"
    exit 1
fi

# Extract and install
mkdir -p "$INSTALL_DIR"
tar xzf "$TMP_DIR/tentacle.tar.gz" -C "$INSTALL_DIR"

# Make binary executable
chmod +x "$INSTALL_DIR/Squid.Tentacle"

# Create symlink with well-known name
ln -sf "$INSTALL_DIR/Squid.Tentacle" "$INSTALL_DIR/${BINARY_NAME}"

if [ -d /usr/local/bin ]; then
    ln -sf "$INSTALL_DIR/${BINARY_NAME}" /usr/local/bin/${BINARY_NAME}
    echo "Installed: /usr/local/bin/${BINARY_NAME}"
else
    echo "Add to PATH: export PATH=\"${INSTALL_DIR}:\$PATH\""
fi

# Verify
if command -v ${BINARY_NAME} &>/dev/null; then
    echo ""
    ${BINARY_NAME} --help 2>/dev/null | head -1 || true
fi

echo ""
echo "=== Installation Complete ==="
echo ""
echo "Next steps:"
echo "  squid-tentacle register \\"
echo "    --server https://your-squid-server:7078 \\"
echo "    --api-key API-XXXX \\"
echo "    --role web-server \\"
echo "    --environment Production \\"
echo "    --flavor LinuxTentacle"
echo "  sudo squid-tentacle service install"
