#!/bin/sh
# Prints the release notes for one version: its section from CHANGELOG.md plus the first-launch hint.
#   ./scripts/release-notes.sh 0.1.0
set -e
cd "$(dirname "$0")/.."
V="$1"
[ -n "$V" ] || { echo "usage: release-notes.sh <version>" >&2; exit 1; }

awk -v v="## v$V" 'index($0, v) == 1 { on = 1; next } /^## / { on = 0 } on' CHANGELOG.md
cat <<NOTES

**Install:** open the DMG and drag Stratoscope to Applications.

The app is not notarized yet, so macOS warns on the first start. Open **System Settings → Privacy & Security**, scroll down and click **Open Anyway**. Or run \`xattr -dr com.apple.quarantine /Applications/Stratoscope.app\` once.

Check the download: \`shasum -a 256 -c Stratoscope-$V.dmg.sha256\`
NOTES
