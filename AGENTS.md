# AGENTS.md

Instructions for AI coding agents working on Stratoscope. Humans: see [CONTRIBUTING.md](CONTRIBUTING.md), which this file follows.

## Read first

Stratoscope is an **instrument**: it shows what an engine reports and says when it doesn't know. Read "What Stratoscope shows, and what it won't" in [CONTRIBUTING.md](CONTRIBUTING.md) before writing code. A change that breaks one of those rules will be declined however well it is written.

## Rules for any change

- **Name the source of every new value you display**: the endpoint, field or log line it comes from. If you can't name one, don't display it.
- **When a value is missing, show "–" or nothing.** Don't estimate it, scale it from another metric, or fall back to 0.
- **Never launch, wake, signal or restart an engine**, and never write to its files.
- **No network calls** other than to the configured engine.

## Rules for the code

- Layout: `Sources/StratoscopeCore` is engine- and platform-neutral and must not import AppKit or any other UI framework. `Sources/Stratoscope` is the macOS app; another platform gets its own target. One folder per engine under `Sources/StratoscopeCore/Engines/`.
- Build and test: `swift build`, `./scripts/test.sh`. Try the UI without an engine: `STRATOSCOPE_ENGINE=demo swift run Stratoscope`.
- The Windows app is C# (.NET 10) in `Sources/Windows`; everything Windows-specific stays under that folder, nothing new in the repo root. Build and test: `dotnet test Sources/Windows/Stratoscope.Windows.sln -c Release`. The rules above apply to it too; what both apps must show is in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#shared-behaviour), the rest in [docs/WINDOWS.md](docs/WINDOWS.md).
- Windows: use a maintained library before writing your own (JSON is System.Text.Json, no NuGet package in `Core` or `App`), and do not allocate per poll or per frame.
- New engine data goes into `EngineSnapshot` as an **optional** field. `nil` means "unknown" and hides that part of the menu.
- Add a test for the logic you add, preferably a pure function separated from networking.
- Comments and UI text are English.

## Rules for the pull request

- One change per PR, from a branch off the current `main`.
- State which engine and version you verified against, and say plainly what was checked against a real engine and what only against a recorded payload or the demo.
- Don't claim more than the change does.
