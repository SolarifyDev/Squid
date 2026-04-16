#!/bin/bash
set -euo pipefail

# Squid Tentacle Linux Installer
# Usage: curl -fsSL https://raw.githubusercontent.com/SolarifyDev/Squid/main/deploy/scripts/install-tentacle.sh | sudo bash
# Or:    sudo bash install-tentacle.sh [--version 1.2.7] [--install-dir /opt/squid-tentacle]

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

# Install runtime dependencies (.NET 9 self-contained needs libicu + ca-certificates).
# Detect the host package manager and install silently if libicu is missing.
install_runtime_deps() {
    if ldconfig -p 2>/dev/null | grep -q "libicuuc"; then
        return 0
    fi

    echo "Installing runtime dependencies (libicu, ca-certificates)..."

    if command -v apt-get >/dev/null 2>&1; then
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq >/dev/null 2>&1 || true
        apt-get install -y -qq libicu-dev ca-certificates >/dev/null 2>&1
    elif command -v dnf >/dev/null 2>&1; then
        dnf install -y -q libicu ca-certificates >/dev/null
    elif command -v yum >/dev/null 2>&1; then
        yum install -y -q libicu ca-certificates >/dev/null
    elif command -v apk >/dev/null 2>&1; then
        apk add --no-cache --quiet icu-libs ca-certificates
    else
        echo "Warning: couldn't detect package manager — install libicu manually."
        return 1
    fi
}

install_runtime_deps || true

# Resolve download URL.
# GitHub Actions publishes two tar.gz per arch under each release tag:
#   squid-tentacle-${VERSION}-${RID}.tar.gz     (versioned)
#   squid-tentacle-${RID}.tar.gz                (unversioned "latest" copy)
#
# For "latest", use GitHub's /releases/latest/download redirect — always resolves.
# For a specific version, try the tag as-given first; if that 404s, fall back to "v"-prefixed.
TMP_DIR=$(mktemp -d)
trap 'rm -rf "$TMP_DIR"' EXIT

ARCHIVE_PATH="$TMP_DIR/tentacle.tar.gz"

download_ok() {
    curl -fsSL --retry 3 "$1" -o "$ARCHIVE_PATH" 2>/dev/null
}

if [ "$VERSION" = "latest" ]; then
    URL="${DOWNLOAD_BASE}/latest/download/${BINARY_NAME}-${RID}.tar.gz"
    echo "Downloading from ${URL}..."

    if ! download_ok "$URL"; then
        echo "Error: Failed to download latest release."
        echo "  Check: ${DOWNLOAD_BASE}/latest"
        exit 1
    fi
else
    # Try both tag formats: plain version (e.g. 1.2.7) and v-prefixed (e.g. v1.2.7).
    URL_PLAIN="${DOWNLOAD_BASE}/download/${VERSION}/${BINARY_NAME}-${VERSION}-${RID}.tar.gz"
    URL_V_PREFIXED="${DOWNLOAD_BASE}/download/v${VERSION}/${BINARY_NAME}-${VERSION}-${RID}.tar.gz"

    echo "Downloading from ${URL_PLAIN}..."

    if ! download_ok "$URL_PLAIN"; then
        echo "Tag '${VERSION}' not found, retrying with 'v${VERSION}'..."
        echo "Downloading from ${URL_V_PREFIXED}..."

        if ! download_ok "$URL_V_PREFIXED"; then
            echo "Error: Neither tag '${VERSION}' nor 'v${VERSION}' has a release asset for ${RID}."
            echo "  Available releases: ${DOWNLOAD_BASE}"
            exit 1
        fi
    fi
fi

# Extract (tar contents are flat — no wrapper dir)
mkdir -p "$INSTALL_DIR"
tar xzf "$ARCHIVE_PATH" -C "$INSTALL_DIR"

# Make binaries executable. Calamari is spawned by Tentacle at runtime, so it also needs +x.
chmod +x "$INSTALL_DIR/Squid.Tentacle"
[ -f "$INSTALL_DIR/Squid.Calamari" ] && chmod +x "$INSTALL_DIR/Squid.Calamari"

# Create well-known-name symlink inside install dir
ln -sf "$INSTALL_DIR/Squid.Tentacle" "$INSTALL_DIR/${BINARY_NAME}"

# Expose on PATH
if [ -d /usr/local/bin ]; then
    ln -sf "$INSTALL_DIR/${BINARY_NAME}" /usr/local/bin/${BINARY_NAME}
    echo "Installed: /usr/local/bin/${BINARY_NAME}"
else
    echo "Add to PATH: export PATH=\"${INSTALL_DIR}:\$PATH\""
fi

# Create a dedicated non-login system user that owns cert/config/workspace dirs.
# The systemd unit can then run with User=${SERVICE_USER} instead of root, so a
# compromised script payload can't pivot to root on the host. Skipped when
# --no-user is passed (for dev/test or when an operator wants to manage the
# identity themselves).
SERVICE_USER="${SERVICE_USER:-squid-tentacle}"
if [ "${CREATE_USER:-yes}" = "yes" ]; then
    if ! getent passwd "$SERVICE_USER" >/dev/null 2>&1; then
        if command -v useradd >/dev/null 2>&1; then
            useradd --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER" 2>/dev/null || true
            echo "Created system user: ${SERVICE_USER}"
        elif command -v adduser >/dev/null 2>&1; then
            adduser --system --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER" 2>/dev/null || true
            echo "Created system user: ${SERVICE_USER}"
        fi
    fi
fi

# Create the system-scope config directory for multi-instance support.
# Register + run will read/write here; restricting to owner-only protects the
# API key and server thumbprint that get persisted after a successful `register`.
CONFIG_DIR="/etc/squid-tentacle"
if [ ! -d "$CONFIG_DIR" ]; then
    mkdir -p "$CONFIG_DIR/instances"
    chmod 700 "$CONFIG_DIR"
    echo "Created config dir: ${CONFIG_DIR}"
fi

# Workspace for script staging. Created upfront so the service user owns it
# (otherwise the first script run does it under root and permissions drift).
WORKSPACE_DIR="${WORKSPACE_DIR:-/squid/work}"
mkdir -p "$WORKSPACE_DIR"

# Transfer ownership to the service user if one exists.
if getent passwd "$SERVICE_USER" >/dev/null 2>&1; then
    chown -R "$SERVICE_USER:$SERVICE_USER" "$CONFIG_DIR" "$WORKSPACE_DIR" "$INSTALL_DIR" 2>/dev/null || true
    echo "Ownership set to ${SERVICE_USER}: ${CONFIG_DIR}, ${WORKSPACE_DIR}, ${INSTALL_DIR}"
fi

# Verify binary is runnable (use the `help` subcommand — `--help` is routed to RunCommand in Program.cs).
if command -v ${BINARY_NAME} >/dev/null 2>&1; then
    if ${BINARY_NAME} help >/dev/null 2>&1; then
        echo "Verified: ${BINARY_NAME} executable"
    else
        echo "Warning: ${BINARY_NAME} installed but did not run successfully."
        echo "  Check glibc version (.NET 9 self-contained requires glibc 2.31+):"
        echo "    ldd --version | head -1"
    fi
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
