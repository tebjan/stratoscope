#!/bin/sh
# Builds build/Stratoscope-<version>.dmg (drag-to-Applications disk image) and a .sha256 next to it.
#   VERSION=0.2.0 ./scripts/make-dmg.sh      (default version: see build-app.sh)
set -e
cd "$(dirname "$0")/.."

./scripts/build-app.sh
VERSION="${VERSION:-0.1.0}"

STAGE="build/dmg"
rm -rf "$STAGE" && mkdir -p "$STAGE"
cp -R build/Stratoscope.app "$STAGE/"
ln -s /Applications "$STAGE/Applications"

DMG="build/Stratoscope-$VERSION.dmg"
rm -f "$DMG"
hdiutil create -volname "Stratoscope $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG" >/dev/null
(cd build && shasum -a 256 "Stratoscope-$VERSION.dmg" > "Stratoscope-$VERSION.dmg.sha256")
echo "Built $DMG"
