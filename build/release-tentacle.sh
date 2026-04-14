#!/bin/bash
set -euo pipefail

VERSION="${1:?Usage: release-tentacle.sh <version>}"
DIST_DIR="dist"

echo "=== Releasing Squid Tentacle v${VERSION} ==="

# Verify archives exist
for RID in linux-x64 linux-arm64; do
    VERSIONED="${DIST_DIR}/squid-tentacle-${VERSION}-${RID}.tar.gz"
    LATEST="${DIST_DIR}/squid-tentacle-${RID}.tar.gz"

    if [ ! -f "$VERSIONED" ]; then
        echo "Error: ${VERSIONED} not found. Run publish-tentacle.sh first."
        exit 1
    fi

    if [ ! -f "$LATEST" ]; then
        echo "Error: ${LATEST} not found. Run publish-tentacle.sh first."
        exit 1
    fi
done

# Create or update GitHub release
echo "Creating GitHub release v${VERSION}..."
gh release create "v${VERSION}" \
    "${DIST_DIR}/squid-tentacle-${VERSION}-linux-x64.tar.gz" \
    "${DIST_DIR}/squid-tentacle-${VERSION}-linux-arm64.tar.gz" \
    "${DIST_DIR}/squid-tentacle-linux-x64.tar.gz" \
    "${DIST_DIR}/squid-tentacle-linux-arm64.tar.gz" \
    --title "Squid Tentacle v${VERSION}" \
    --notes "Linux Tentacle binaries (self-contained, no .NET runtime required)" \
    --latest \
    2>/dev/null \
|| gh release upload "v${VERSION}" \
    "${DIST_DIR}/squid-tentacle-${VERSION}-linux-x64.tar.gz" \
    "${DIST_DIR}/squid-tentacle-${VERSION}-linux-arm64.tar.gz" \
    "${DIST_DIR}/squid-tentacle-linux-x64.tar.gz" \
    "${DIST_DIR}/squid-tentacle-linux-arm64.tar.gz" \
    --clobber

echo ""
echo "=== Release v${VERSION} Complete ==="
echo "https://github.com/SolarifyDev/Squid/releases/tag/v${VERSION}"
