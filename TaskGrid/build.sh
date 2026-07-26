#!/bin/bash
set -e

PLUGIN_DIR="Jellyfin.Plugin.TaskGrid"
OUTPUT_DIR="output"
LEGACY_10_11_VERSION="1.1.4.0"
V12_RC2_VERSION="1.2.4.0"
V12_RC3_VERSION="1.3.4.0"
TARGET="${1:-all}"
DEFAULT_JELLYFIN_SOURCE_ROOT="/Users/joncasas/GitHub/jellyfin"

echo "=== Building Task Grid Plugin ==="

rm -rf "$PLUGIN_DIR/bin" "$PLUGIN_DIR/obj" "$PLUGIN_DIR/publish_out" "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

build_target() {
  local target="$1"
  local target_abi
  local framework
  local zip_name
  local plugin_version
  local package_version=""
  local source_root="${JELLYFIN_SOURCE_ROOT:-}"

  case "$target" in
    10.11|10.11.0|10.11.0.0)
      target_abi="10.11.0.0"
      framework="net9.0"
      plugin_version="$LEGACY_10_11_VERSION"
      zip_name="task-grid-$plugin_version.zip"
      ;;
    12|12.0|12.0.0|12.0.0.0|jellyfin12|v12|12-rc3|v12-rc3|rc3)
      target_abi="12.0.0.0"
      framework="net10.0"
      plugin_version="$V12_RC3_VERSION"
      package_version="${JELLYFIN_PACKAGE_VERSION:-12.0.0-rc3}"
      zip_name="task-grid-$plugin_version-jellyfin12-rc3.zip"
      ;;
    12-rc2|v12-rc2|rc2)
      target_abi="12.0.0.0"
      framework="net10.0"
      plugin_version="$V12_RC2_VERSION"
      package_version="${JELLYFIN_PACKAGE_VERSION:-12.0.0-rc2}"
      zip_name="task-grid-$plugin_version-jellyfin12-rc2.zip"
      ;;
    *)
      echo "Unknown target '$target'. Use 10.11, 12, or all."
      exit 1
      ;;
  esac

  echo ""
  echo "=== Target ABI: $target_abi ($framework) ==="
  if [ "$target_abi" = "12.0.0.0" ] && [ -n "$source_root" ]; then
    echo "Using Jellyfin source root: $source_root"
  elif [ "$target_abi" = "12.0.0.0" ]; then
    echo "Using Jellyfin package references version $package_version."
  fi

  rm -rf "$PLUGIN_DIR/publish_out"

  echo "Compiling plugin..."
  cd "$PLUGIN_DIR"
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet restore \
    /p:JellyfinTargetAbi="$target_abi" \
    /p:JellyfinSourceRoot="$source_root" \
    /p:JellyfinPackageVersion="$package_version"
  DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 dotnet publish -c Release -f "$framework" -o publish_out \
    /p:JellyfinTargetAbi="$target_abi" \
    /p:JellyfinSourceRoot="$source_root" \
    /p:JellyfinPackageVersion="$package_version" \
    /p:Version="$plugin_version" \
    /p:AssemblyVersion="$plugin_version"

  echo "Copying files..."
  cd ..
  rm -f "$OUTPUT_DIR"/*.dll "$OUTPUT_DIR"/*.png "$OUTPUT_DIR"/meta.json "$OUTPUT_DIR/$zip_name"
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
  "version": "$plugin_version",
  "targetAbi": "$target_abi",
  "timestamp": "2026-07-26T00:00:00Z"
}
JSON

  echo "Creating ZIP archive..."
  cd "$OUTPUT_DIR"
  zip -r "$zip_name" *.dll *.png meta.json
  cd ..

  echo "Built $OUTPUT_DIR/$zip_name"
}

case "$TARGET" in
  all)
    build_target "10.11"
    build_target "12-rc2"
    build_target "12-rc3"
    ;;
  *)
    build_target "$TARGET"
    ;;
esac
