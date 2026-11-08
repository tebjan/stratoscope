#!/bin/sh
# Runs the unit tests. With full Xcode installed, plain `swift test` is enough.
# With only the Command Line Tools, the Swift Testing plugin has to be pointed out explicitly.
set -e
cd "$(dirname "$0")/.."
CLT=/Library/Developer/CommandLineTools
if [ "$(xcode-select -p)" = "$CLT/" ] || [ "$(xcode-select -p)" = "$CLT" ]; then
  exec swift test \
    -Xswiftc -external-plugin-path -Xswiftc "$CLT/usr/lib/swift/host/plugins/testing#$CLT/usr/bin/swift-plugin-server" \
    -Xswiftc -F -Xswiftc "$CLT/Library/Developer/Frameworks" \
    -Xlinker -F -Xlinker "$CLT/Library/Developer/Frameworks" \
    -Xlinker -rpath -Xlinker "$CLT/Library/Developer/Frameworks" "$@"
fi
exec swift test "$@"
