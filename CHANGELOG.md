# Changelog

## Unreleased

- Basalt engines (verified against Basalt 0.3.2): state, prefill progress and speed, decode speed, context, finished requests with their cache verdict, engine details (version, KV format, draft window, expert cache), RAM, CPU, and the video card's load, temperature, power and memory, each only where Basalt reports it. The kind of engine is asked of the engine itself (`/health`), so one router can serve Strata and Basalt. New setting value `engine: basalt` reads Basalt only. The Swift core recognises a Basalt document and shows state, speeds, context, requests, RAM, GPU load and CPU.

## v0.2.0 · 2026-11-09

- Windows 10 and 11 (x64): a readout in the taskbar and the same panel, released as `Stratoscope-<version>-windows.zip`. See [docs/WINDOWS.md](docs/WINDOWS.md).

## v0.1.4 · 2026-11-08

- Parked conversations whose details can't be read (no log) show "In use" instead of "Free".
- New setting `client_model`: the model id your client config uses, for setups where it differs from the name the engine reports.
- Client configs are also looked up in `$XDG_CONFIG_HOME`.
- README: a table of other setups and what to set for each.

## v0.1.3 · 2026-11-08

- The summarise marker on the context bar now finds its value itself: it reads the context and output size of the running model from the Kilo and OpenCode configs that point at the same server, and uses context minus output. Where nothing can be read, or two configs disagree, `compact_at` is the fallback, and without it there is no marker. `client_configs: none` turns the reading off.

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
