#!/bin/bash
set -euo pipefail

# Squid Tentacle Linux Installer
# Usage: curl -fsSL https://install.squidcd.com/tentacle | bash
# Or:    bash install-tentacle.sh [--version 1.0.0] [--install-dir /opt/squid-tentacle]

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
    *) echo "Unsupported architecture: $ARCH"; exit 1 ;;
esac

echo "=== Squid Tentacle Installer ==="
echo "Version:  ${VERSION}"
echo "Arch:     ${RID}"
echo "Install:  ${INSTALL_DIR}"
echo ""

# Download
if [ "$VERSION" = "latest" ]; then
    URL="${DOWNLOAD_BASE}/latest/download/squid-tentacle-${RID}.tar.gz"
else
    URL="${DOWNLOAD_BASE}/download/v${VERSION}/squid-tentacle-${VERSION}-${RID}.tar.gz"
fi

echo "Downloading from ${URL}..."
mkdir -p "$INSTALL_DIR"
curl -fsSL "$URL" | tar xz -C "$INSTALL_DIR"

# Create symlink
if [ -w /usr/local/bin ]; then
    ln -sf "$INSTALL_DIR/Squid.Tentacle" /usr/local/bin/squid-tentacle
    echo "Symlinked: /usr/local/bin/squid-tentacle → ${INSTALL_DIR}/Squid.Tentacle"
else
    echo "Note: Run 'sudo ln -sf ${INSTALL_DIR}/Squid.Tentacle /usr/local/bin/squid-tentacle' to add to PATH"
fi

echo ""
echo "=== Installation Complete ==="
echo ""
echo "Quick start (polling mode):"
echo "  squid-tentacle new-certificate"
echo "  squid-tentacle register \\"
echo "    --server https://your-squid-server:7078 \\"
echo "    --api-key API-XXXX \\"
echo "    --role web-server \\"
echo "    --environment Production \\"
echo "    --comms-url https://your-squid-server:10943 \\"
echo "    --flavor LinuxTentacle"
echo "  sudo squid-tentacle service install"
echo ""
echo "Quick start (Docker):"
echo "  docker run -d \\"
echo "    -e Tentacle__ServerUrl=https://your-squid-server:7078 \\"
echo "    -e Tentacle__ServerCommsUrl=https://your-squid-server:10943 \\"
echo "    -e Tentacle__BearerToken=your-token \\"
echo "    -e Tentacle__Roles=web-server \\"
echo "    -e Tentacle__Environments=Production \\"
echo "    squidcd/squid-tentacle-linux:latest"
