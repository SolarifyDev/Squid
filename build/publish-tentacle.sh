#!/bin/bash
set -euo pipefail

VERSION="${1:-0.0.0-dev}"
OUTPUT_BASE="dist"

echo "=== Publishing Squid Tentacle v${VERSION} ==="

for RID in linux-x64 linux-arm64; do
    OUTPUT_DIR="${OUTPUT_BASE}/${RID}"
    echo ""
    echo "--- ${RID} ---"

    echo "Publishing Squid.Tentacle (self-contained)..."
    dotnet publish src/Squid.Tentacle/Squid.Tentacle.csproj \
        -c Release -r "$RID" --self-contained true \
        -p:PublishSingleFile=true \
        -p:Version="$VERSION" \
        -o "$OUTPUT_DIR"

    echo "Publishing Squid.Calamari (self-contained)..."
    dotnet publish src/Squid.Calamari/Squid.Calamari.csproj \
        -c Release -r "$RID" --self-contained true \
        -o "$OUTPUT_DIR"

    ARCHIVE="${OUTPUT_BASE}/squid-tentacle-${VERSION}-${RID}.tar.gz"
    echo "Creating archive: ${ARCHIVE}"
    tar czf "$ARCHIVE" -C "$OUTPUT_DIR" .

    echo "Created: ${ARCHIVE} ($(du -h "$ARCHIVE" | cut -f1))"
done

echo ""
echo "=== Done ==="
ls -lh "${OUTPUT_BASE}"/squid-tentacle-*.tar.gz
