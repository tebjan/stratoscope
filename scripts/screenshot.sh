#!/bin/sh
# Regenerates the README screenshots from demo data (no engine needed). The PNGs are opaque (no alpha channel).
set -e
cd "$(dirname "$0")/.."
./scripts/build-app.sh >/dev/null 2>&1   # the app bundle knows its version; `swift run` does not
mkdir -p docs/img
shoot() { # name, extra env...
  out="docs/img/$1.png"; shift
  env STRATOSCOPE_ENGINE=demo STRATOSCOPE_COMPACT_AT=60000 STRATOSCOPE_SNAPSHOT="$out" "$@" build/Stratoscope.app/Contents/MacOS/Stratoscope &
  pid=$!; i=0
  while kill -0 $pid 2>/dev/null && [ $i -lt 50 ]; do sleep 0.2; i=$((i+1)); done
  kill $pid 2>/dev/null || true
}
shoot menu-light
shoot menu-dark STRATOSCOPE_DARK=1
shoot menu-decode-light STRATOSCOPE_SCENARIO=decode
ls -l docs/img
