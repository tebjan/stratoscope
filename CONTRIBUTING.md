# Contributing to Stratoscope

Thanks for helping. The most useful contribution is **an adapter for another local inference engine**. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): an adapter is usually one file plus one test.

## Quick start

The app runs on macOS 13+ today and needs the Xcode Command Line Tools to build. Ports to other platforms are welcome, see [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#platforms). The Windows app (C#, .NET 10 SDK) builds from `Sources/Windows` with `dotnet test Sources/Windows/Stratoscope.Windows.sln`: see [docs/WINDOWS.md](docs/WINDOWS.md#build-and-test).

```bash
swift build                              # build everything
STRATOSCOPE_ENGINE=demo swift run Stratoscope   # try the UI with fake data, no engine needed
./scripts/test.sh                        # unit tests (works with Command Line Tools only)
./scripts/screenshot.sh                  # re-render the README images
```

Nothing leaves your Mac: no telemetry, no analytics, no network calls except to the engine on `127.0.0.1` (or the address you configure). PRs that phone home won't be merged.

## What Stratoscope shows, and what it won't

Stratoscope is an instrument. It shows what an engine reports and says so when it doesn't know.

1. **Only reported values.** Every number on screen comes from the engine's own metrics. The one exception is the summarise marker on the context bar, which no engine reports: it comes from your own `compact_at` setting or from the `limit` numbers in the config of a client that talks to the engine (read-only, never keys), and is left out when neither says. A value the engine doesn't report is shown as "–" or left out, never as `0` and never guessed from something else.
2. **Estimates are marked.** Where a number has to be derived (for example Strata does not report the cache reuse of a request that is still running), the UI shows it as approximate (`≈`) and the code says why.
3. **Engines are asked through their public interface only**, and only while they run. Stratoscope never starts, wakes, signals or restarts an engine, and never writes to its files.
4. **It describes; it doesn't act.** No automatic actions on your engine or your machine.
5. **Engine code stays out of the UI.** If the menu needs something new, add an optional field to `EngineSnapshot` that is useful for every engine.
6. **Say what was tested.** A PR states the engine, its version and the Mac or Windows PC it was verified on, and says plainly what was checked against a real engine and what only against a recorded payload or the demo.

If your change runs against one of these, open an issue first. There may be another way to get the same answer.

## Changing how the menu looks

Run `./scripts/screenshot.sh` and commit the new `docs/img/*.png` in the same PR. The images come from the demo data, so nothing has to be captured by hand, and unchanged code produces identical files. Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#screenshots).

## Adding an engine

1. Open an issue ("Engine request") with the engine's metrics endpoint and a sample payload.
2. Follow [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#adding-an-engine).
3. Add a test that feeds a recorded payload through your translation, like `StrataSnapshotTests`.

## Pull requests

- One change per PR, from a branch off the current `main`.
- Open an issue first for anything beyond a fix: a new panel, data source or engine.
- Add or update a test for the logic you change. Put logic in `StratoscopeCore` (no AppKit) so it can be tested without a screen.
- Comments and UI text are English.
- C#: use a maintained library before writing your own, and keep `Sources/Windows` self-contained (no new files in the repo root).
- Update [CHANGELOG.md](CHANGELOG.md) under "Unreleased".
