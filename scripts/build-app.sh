#!/bin/sh
# Builds build/Stratoscope.app (a menu bar app: no Dock icon). Needs the Xcode Command Line Tools.
#   ./scripts/build-app.sh            build
#   ./scripts/build-app.sh --install  build and copy to /Applications
set -e
cd "$(dirname "$0")/.."

VERSION="${VERSION:-0.3.0}"
COMMIT="$(git rev-parse --short HEAD 2>/dev/null || true)"
APP="build/Stratoscope.app"

swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/Stratoscope"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/Stratoscope"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>io.github.yosephyne.stratoscope</string>
  <key>CFBundleName</key><string>Stratoscope</string>
  <key>CFBundleExecutable</key><string>Stratoscope</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>StratoscopeCommit</key><string>$COMMIT</string>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
  <key>LSUIElement</key><true/>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict></plist>
PLIST
# Ad-hoc signature: enough to run on your own Mac.
codesign --force --sign - "$APP" >/dev/null 2>&1 || true

echo "Built $APP ($VERSION${COMMIT:+, $COMMIT})"
if [ "$1" = "--install" ]; then
  rm -rf /Applications/Stratoscope.app && cp -R "$APP" /Applications/Stratoscope.app
  echo "Installed to /Applications/Stratoscope.app"
fi
