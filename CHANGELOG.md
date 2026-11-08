# Changelog

## Unreleased

## v0.1.2 · 2026-11-08

- Status words and labels start with a capital letter ("Ready", "Reading", "Hit rate", …).

## v0.1.1 · 2026-11-08

- Context: room left below the limit, and one bar per finished request showing the context size after it, for the last 12 requests.
- The marker for where your client summarises is now optional. Set `compact_at` to your client's threshold to see it; without a value no marker is drawn. The explanatory text under the bar is gone.
- Menu labels are written out ("Prefill", "Decode") instead of single letters.
- Screenshots come from fixed demo data and are reproducible with `./scripts/screenshot.sh`.

## v0.1.0 · 2026-11-08

First version.

- Menu bar item with live speed (`247 t/s`) and a progress bar while a prompt is read, colored by prompt-cache quality.
- Menu: current request, context bar with the room left, an optional marker where your client summarises, and the context size after each of the last 12 requests, speed of the last 12 requests, RAM/GPU/CPU, prompt cache and parked conversations, totals.
- Engine-neutral snapshot model and an adapter protocol. Adapters: **Strata**, **Demo**.
- Configuration through environment variables or `~/.config/stratoscope.json`.
- Download as a DMG from GitHub Releases. Pushing a `v*` tag builds a universal (Apple Silicon and Intel) DMG with a checksum and publishes it.
- README screenshots are rendered from demo data (`./scripts/screenshot.sh`).
