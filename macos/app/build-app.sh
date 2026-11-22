#!/bin/bash
# Builds Limen.app (universal, macOS 13+, ad-hoc signed) from the Swift
# sources and the NativeAOT Core dylibs.
#
#   macos/app/build-app.sh <arm64-aot-dir> <x64-aot-dir> <out-dir>
#
# The two AOT dirs are `dotnet publish macos/bridge/Aot -r osx-arm64 / osx-x64`
# outputs; every .dylib in them is joined with lipo and shipped in Frameworks.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
ARM="$1"; X64="$2"; OUT="$3"
VERSION="${LIMEN_VERSION:-${EDGELINGS_VERSION:-0.1.0}}"
APP="$OUT/Limen.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Frameworks" "$APP/Contents/Resources/Characters" "$APP/Contents/Resources/Fonts" "$APP/Contents/Resources/Marks" "$OUT/obj"

SOURCES=("$HERE"/Sources/*.swift "$REPO"/macos/platform/*.swift "$REPO"/macos/engine/*.swift "$REPO"/macos/golden/SvgCanvas.swift)
for arch in arm64 x86_64; do
  swiftc -O -target "$arch-apple-macos13.0" "${SOURCES[@]}" -o "$OUT/obj/Limen-$arch"
done
lipo -create "$OUT/obj/Limen-arm64" "$OUT/obj/Limen-x86_64" -output "$APP/Contents/MacOS/Limen"

for lib in "$ARM"/*.dylib; do
  name="$(basename "$lib")"
  if [ -f "$X64/$name" ]; then
    lipo -create "$lib" "$X64/$name" -output "$APP/Contents/Frameworks/$name"
  else
    cp "$lib" "$APP/Contents/Frameworks/$name"
  fi
done

ENGINES="$REPO/windows/src/AgentIsland.WinUI/Drawer/Characters"
cp "$ENGINES/characters.mjs" "$ENGINES/classic.mjs" "$APP/Contents/Resources/Characters/"
FONTS="$REPO/windows/src/AgentIsland.WinUI/Assets/Fonts"
cp "$FONTS"/*.ttf "$FONTS"/*.txt "$APP/Contents/Resources/Fonts/"
cp "$REPO"/windows/src/AgentIsland.WinUI/Assets/mark-*.png "$APP/Contents/Resources/Marks/"

ICON_PATH="${LIMEN_ICON:-${EDGELINGS_ICON:-}}"
if [ -n "$ICON_PATH" ] && [ -f "$REPO/$ICON_PATH" ]; then cp "$REPO/$ICON_PATH" "$APP/Contents/Resources/AppIcon.icns"; elif [ -n "$ICON_PATH" ] && [ -f "$ICON_PATH" ]; then cp "$ICON_PATH" "$APP/Contents/Resources/AppIcon.icns"; fi

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key><string>dev.limen.Limen</string>
  <key>CFBundleName</key><string>Limen</string>
  <key>CFBundleDisplayName</key><string>Limen</string>
  <key>CFBundleExecutable</key><string>Limen</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Limen</string>
</dict>
</plist>
PLIST

for lib in "$APP"/Contents/Frameworks/*.dylib; do codesign --force -s - "$lib"; done
# The entitlements let JavaScriptCore use its JIT (without them the engines run in its interpreter, about 3.5x slower).
codesign --force --deep -s - --entitlements "$HERE/Limen.entitlements" "$APP"
lipo -info "$APP/Contents/MacOS/Limen"
echo "built $APP"
