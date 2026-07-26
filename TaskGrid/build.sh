#!/bin/bash
set -e

PLUGIN_DIR="Jellyfin.Plugin.TaskGrid"
OUTPUT_DIR="output"
VERSION="1.0.0.0"
ZIP_NAME="task-grid-$VERSION.zip"

echo "=== Building Task Grid Plugin ==="

rm -rf "$PLUGIN_DIR/bin" "$PLUGIN_DIR/obj" "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

echo "Compiling plugin..."
cd "$PLUGIN_DIR"
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet restore
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet publish -c Release -f net9.0 -o publish_out /p:Version="$VERSION" /p:AssemblyVersion="$VERSION"

echo "Copying files..."
cd ..
cp "$PLUGIN_DIR/publish_out/Jellyfin.Plugin.TaskGrid.dll" "$OUTPUT_DIR/"
cp "TaskGrid.png" "$OUTPUT_DIR/"

cat > "$OUTPUT_DIR/meta.json" <<JSON
{
  "guid": "a56a1707-aaeb-4ed5-bd95-1543ff817b9e",
  "name": "Task Grid",
  "overview": "Shows Jellyfin scheduled tasks on a weekly hour grid.",
  "description": "Task Grid displays Jellyfin scheduled tasks on a Monday-first weekly grid with hour columns, color coding, heavy-task overlap warnings, refresh support, and red warnings for tasks most recently aborted by server shutdown.",
  "owner": "nothing2obvi",
  "category": "General",
  "version": "$VERSION",
  "targetAbi": "10.11.0.0",
  "timestamp": "2026-07-26T00:00:00Z"
}
JSON

echo "Creating ZIP archive..."
cd "$OUTPUT_DIR"
zip -r "$ZIP_NAME" *.dll *.png meta.json
cd ..

echo "Built $OUTPUT_DIR/$ZIP_NAME"
