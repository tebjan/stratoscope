#!/bin/sh
# Re-renders all README screenshots from the demo data. No engine, no screen capture, no manual steps:
# the menu is drawn straight into an opaque 2x PNG (STRATOSCOPE_SNAPSHOT). The demo pins its clock
# (DemoProvider.referenceDate), so unchanged code gives byte-identical images.
#
# Run it after every change to the menu's looks, then commit docs/img together with the change.
# To add a screenshot, add one `shoot` line below and reference the file in README.md.
set -e
cd "$(dirname "$0")/.."
./scripts/build-app.sh >/dev/null   # the app bundle knows its version; `swift run` does not
mkdir -p docs/img

shoot() { # name, then extra environment variables
  name="$1"; shift
  out="docs/img/$name.png"
  rm -f "$out"
  env STRATOSCOPE_ENGINE=demo STRATOSCOPE_SNAPSHOT="$out" "$@" build/Stratoscope.app/Contents/MacOS/Stratoscope &
  pid=$!; i=0
  while kill -0 $pid 2>/dev/null && [ $i -lt 50 ]; do sleep 0.2; i=$((i+1)); done
  kill $pid 2>/dev/null || true
  [ -s "$out" ] || { echo "No image written: $out" >&2; exit 1; }
  [ "$(sips -g hasAlpha "$out" | awk '/hasAlpha/ {print $2}')" = "no" ] || { echo "$out has an alpha channel" >&2; exit 1; }
}

#     name                 scenario / appearance
shoot menu-light
shoot menu-dark            STRATOSCOPE_DARK=1
shoot menu-decode-light    STRATOSCOPE_SCENARIO=decode

ls -l docs/img
