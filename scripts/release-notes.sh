#!/bin/sh
# Prints the release notes for one version: its section from CHANGELOG.md plus the install lines.
#   ./scripts/release-notes.sh 0.1.0
set -e
cd "$(dirname "$0")/.."
V="$1"
[ -n "$V" ] || { echo "usage: release-notes.sh <version>" >&2; exit 1; }

awk -v v="## v$V" 'index($0, v) == 1 { on = 1; next } /^## / { on = 0 } on' CHANGELOG.md
cat <<NOTES

**macOS:** open \`Stratoscope-$V-macos.dmg\` and drag Stratoscope to Applications. It is not notarized yet: on the first start open System Settings → Privacy & Security and click **Open Anyway**.

**Windows 10 or 11:** open \`Stratoscope-$V-windows-setup.exe\`. It is not code-signed yet: if Windows warns, choose "More info", then "Run anyway".

Also for Windows: \`Stratoscope-$V-windows.zip\` (unpack, run \`install.cmd\`) and \`Stratoscope-$V-windows-framework-dependent.zip\` (small, needs the .NET 10 Desktop Runtime).
NOTES
