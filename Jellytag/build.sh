#!/bin/bash
set -e

PLUGIN_DIR="Jellyfin.Plugin.JellyTag"
OUTPUT_DIR="output"
ZIP_NAME="jellytag-plus-1.50.22.0.zip"

echo "=== Building JellyTag-Plus Plugin ==="

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Compiling plugin..."
cd "$PLUGIN_DIR"
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet restore
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet publish -c Release -o publish_out

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
  "version": "1.50.22.0",
  "targetAbi": "10.11.0.0",
  "timestamp": "2026-05-29T00:00:00Z"
}
JSON

echo "Creating ZIP archive..."
cd "$OUTPUT_DIR"
zip -r "$ZIP_NAME" *.dll *.png meta.json
cd ..

echo "Built $OUTPUT_DIR/$ZIP_NAME"
