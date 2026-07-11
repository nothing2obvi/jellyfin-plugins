#!/bin/bash
set -e

PLUGIN_DIR="Jellyfin.Plugin.JellyTag"
OUTPUT_DIR="output"
VERSION="1.52.18.0"
LEGACY_10_11_VERSION="1.51.22.0"
TARGET="${1:-12}"

case "$TARGET" in
  10.11|10.11.0|10.11.0.0)
    TARGET_ABI="10.11.0.0"
    FRAMEWORK="net9.0"
    PACKAGE_VERSION="$LEGACY_10_11_VERSION"
    ZIP_NAME="jellytag-plus-$PACKAGE_VERSION.zip"
    ;;
  12|12.0|12.0.0|12.0.0.0|jellyfin12)
    TARGET_ABI="12.0.0.0"
    FRAMEWORK="net10.0"
    PACKAGE_VERSION="$VERSION"
    ZIP_NAME="jellytag-plus-$PACKAGE_VERSION-jellyfin12.zip"
    ;;
  *)
    echo "Unknown target '$TARGET'. Use 10.11 or 12."
    exit 1
    ;;
esac

echo "=== Building JellyTag-Plus Plugin ==="
echo "Target ABI: $TARGET_ABI ($FRAMEWORK)"

if [ "$TARGET_ABI" = "12.0.0.0" ] && [ -z "${JELLYFIN_SOURCE_ROOT:-}" ]; then
  JELLYFIN_PACKAGE_VERSION="${JELLYFIN_PACKAGE_VERSION:-12.0.0-rc2}"
  echo "Using Jellyfin package references version $JELLYFIN_PACKAGE_VERSION."
fi

rm -rf "$PLUGIN_DIR/publish_out"
mkdir -p "$OUTPUT_DIR"
rm -f "$OUTPUT_DIR"/*.dll "$OUTPUT_DIR"/*.png "$OUTPUT_DIR"/meta.json "$OUTPUT_DIR/$ZIP_NAME"

echo "Compiling plugin..."
cd "$PLUGIN_DIR"
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet restore \
  /p:JellyfinTargetAbi="$TARGET_ABI" \
  /p:JellyfinSourceRoot="${JELLYFIN_SOURCE_ROOT:-}" \
  /p:JellyfinPackageVersion="${JELLYFIN_PACKAGE_VERSION:-}"
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet publish -c Release -f "$FRAMEWORK" -o publish_out \
  /p:JellyfinTargetAbi="$TARGET_ABI" \
  /p:JellyfinSourceRoot="${JELLYFIN_SOURCE_ROOT:-}" \
  /p:JellyfinPackageVersion="${JELLYFIN_PACKAGE_VERSION:-}" \
  /p:Version="$PACKAGE_VERSION" \
  /p:AssemblyVersion="$PACKAGE_VERSION"

echo "Copying files..."
cd ..
cp "$PLUGIN_DIR/publish_out/Jellyfin.Plugin.JellyTagPlus.dll" "$OUTPUT_DIR/"
cp "JellyTag-Plus.png" "$OUTPUT_DIR/"

for dll in ExCSS.dll ShimSkiaSharp.dll Svg.Custom.dll Svg.Model.dll Svg.Skia.dll; do
    [ -f "$PLUGIN_DIR/publish_out/$dll" ] && cp "$PLUGIN_DIR/publish_out/$dll" "$OUTPUT_DIR/"
done

cat > "$OUTPUT_DIR/meta.json" <<'JSON'
{
  "guid": "a3db8d87-9a5a-4f35-94b5-7df409f7dc01",
  "name": "JellyTag-Plus",
  "overview": "Overlays quality badges (resolution, HDR, codec, audio, language, collections) on media posters and thumbnails.",
  "description": "JellyTag-Plus automatically overlays quality badges on your media posters and thumbnails. Supports per-library badge type controls, resolution, HDR, video codec, audio, language flags, VOST indicator, and regex-matched collection badges.",
  "owner": "nothing2obvi",
  "category": "General",
  "version": "__VERSION__",
  "targetAbi": "__TARGET_ABI__",
  "timestamp": "2026-07-10T00:00:00Z"
}
JSON
sed -i.bak "s/__VERSION__/$PACKAGE_VERSION/g; s/__TARGET_ABI__/$TARGET_ABI/g" "$OUTPUT_DIR/meta.json"
rm -f "$OUTPUT_DIR/meta.json.bak"

echo "Creating ZIP archive..."
cd "$OUTPUT_DIR"
zip -r "$ZIP_NAME" *.dll *.png meta.json
cd ..

echo "Built $OUTPUT_DIR/$ZIP_NAME"
