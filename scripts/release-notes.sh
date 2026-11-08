#!/bin/sh
# Prints the release notes for one version: its section from CHANGELOG.md plus the first-launch hint.
#   ./scripts/release-notes.sh 0.1.0
set -e
cd "$(dirname "$0")/.."
V="$1"
[ -n "$V" ] || { echo "usage: release-notes.sh <version>" >&2; exit 1; }

awk -v v="## v$V" 'index($0, v) == 1 { on = 1; next } /^## / { on = 0 } on' CHANGELOG.md
cat <<NOTES

**Install on macOS:** open the DMG and drag Stratoscope to Applications.

The app is not notarized yet, so macOS warns on the first start. Open **System Settings → Privacy & Security**, scroll down and click **Open Anyway**. Or run \`xattr -dr com.apple.quarantine /Applications/Stratoscope.app\` once.

Check the download: \`shasum -a 256 -c Stratoscope-$V.dmg.sha256\`

**Install on Windows 10 or 11:** unpack \`Stratoscope-$V-windows.zip\` and double-click \`install.cmd\`. It installs for your user only, no administrator rights. Nothing has to be installed first. \`Stratoscope-$V-windows-framework-dependent.zip\` is the small download for PCs that already have the .NET 10 Desktop Runtime.

The Windows build is not code-signed yet, so SmartScreen may warn on the first start. Check the download: \`(Get-FileHash Stratoscope-$V-windows.zip).Hash\` must equal (ignoring case) the hash in \`Stratoscope-$V-windows.zip.sha256\`.
NOTES
