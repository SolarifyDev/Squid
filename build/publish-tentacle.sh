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

    # Versioned archive (for specific version downloads)
    ARCHIVE="${OUTPUT_BASE}/squid-tentacle-${VERSION}-${RID}.tar.gz"
    echo "Creating archive: ${ARCHIVE}"
    tar czf "$ARCHIVE" -C "$OUTPUT_DIR" .

    # Unversioned archive (for "latest" downloads)
    LATEST_ARCHIVE="${OUTPUT_BASE}/squid-tentacle-${RID}.tar.gz"
    cp "$ARCHIVE" "$LATEST_ARCHIVE"

    echo "Created: ${ARCHIVE} ($(du -h "$ARCHIVE" | cut -f1))"
done

echo ""
echo "=== Done ==="
ls -lh "${OUTPUT_BASE}"/squid-tentacle-*.tar.gz

echo ""
echo "To create a GitHub release:"
echo "  gh release create v${VERSION} \\"
echo "    ${OUTPUT_BASE}/squid-tentacle-${VERSION}-linux-x64.tar.gz \\"
echo "    ${OUTPUT_BASE}/squid-tentacle-${VERSION}-linux-arm64.tar.gz \\"
echo "    ${OUTPUT_BASE}/squid-tentacle-linux-x64.tar.gz \\"
echo "    ${OUTPUT_BASE}/squid-tentacle-linux-arm64.tar.gz"
