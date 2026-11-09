#!/bin/sh
# Builds build/Stratoscope-<version>-macos.dmg (drag-to-Applications disk image).
#   VERSION=0.3.0 ./scripts/make-dmg.sh      (default version: see build-app.sh)
set -e
cd "$(dirname "$0")/.."

./scripts/build-app.sh
VERSION="${VERSION:-0.3.0}"

STAGE="build/dmg"
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp -R build/Stratoscope.app "$STAGE/"
ln -s /Applications "$STAGE/Applications"

DMG="build/Stratoscope-$VERSION-macos.dmg"
rm -f "$DMG"
hdiutil create -volname "Stratoscope $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
echo "Built $DMG"
