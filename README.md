<div align="center">

# Stratoscope

A speedometer for your local LLM.

[![Release](https://img.shields.io/github/v/release/yosephyne/stratoscope?color=2b9348)](https://github.com/yosephyne/stratoscope/releases/latest)
![macOS 13+](https://img.shields.io/badge/macOS-13%2B-000000?logo=apple&logoColor=white)
![Windows 10+](https://img.shields.io/badge/Windows-10%2B-0078d4)
[![License MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Inference engine: Strata](https://img.shields.io/badge/inference%20engine-Strata-5c9efa)
![No telemetry](https://img.shields.io/badge/telemetry-none-2b9348)

<br>

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/img/menu-dark.png">
  <img src="docs/img/menu-light.png" alt="The Stratoscope menu while a long prompt is being read" width="400">
</picture>

</div>

<br>

Stratoscope shows what your local LLM inference engine is doing right now, in the macOS menu bar or the Windows taskbar.

- The speed, like `247 t/s`, and a progress bar while a prompt is read. The bar's color shows how much came from the cache.
- The panel: the current request, the context and how its size changed over the last 12 requests, the speed of the last 12 requests, RAM / GPU / CPU, the cache slots, and totals.
**Cache colors**

| Color | Meaning |
|---|---|
| Green | 90 % or more of the prompt came from the cache |
| Yellow | Less than 90 %, but at least 8,192 tokens were reused |
| Orange | Less than 8,192 tokens reused. Expect a wait |
| Gray | Prompt under 4,096 tokens. Too small to matter |

<table>
  <tr>
    <td align="center"><img src="docs/img/menu-light.png" width="300"><br><sub>Reading the prompt</sub></td>
    <td align="center"><img src="docs/img/menu-decode-light.png" width="300"><br><sub>Writing the answer</sub></td>
  </tr>
</table>

## Install

### macOS

1. Download the latest `Stratoscope-<version>.dmg` from [Releases](https://github.com/yosephyne/stratoscope/releases/latest).
2. Open it and drag Stratoscope to Applications.
3. Start it. On the first launch macOS warns you, because the app isn't notarized yet. Open System Settings → Privacy & Security, scroll down and click **Open Anyway**.

To start it at login: System Settings → General → Login Items → add Stratoscope.

Built for macOS 13 or newer, Apple Silicon and Intel. Tested so far on Apple Silicon with macOS 27.

### Windows

1. Download `Stratoscope-<version>-windows.zip` from [Releases](https://github.com/yosephyne/stratoscope/releases/latest) and unpack it.
2. Run `install.cmd`. It installs for your user, no administrator rights, and starts with Windows.

Windows 10 (1809) or newer, 64-bit. The build is not code-signed, so SmartScreen may warn once. Instead of a menu bar item you get a readout in the taskbar, with the same panel. Settings are in `%LOCALAPPDATA%\Stratoscope\config.json`. Differences from the Mac app: [docs/WINDOWS.md](docs/WINDOWS.md).

<div align="center">
<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/img/windows-panel-dark.png">
  <img src="docs/img/windows-panel-light.png" alt="The Stratoscope panel on Windows while a prompt is being read" width="360">
</picture>
</div>

### Other platforms

| Platform | Status |
|---|---|
| macOS | Works |
| Windows | Works |
| Linux | Not yet |

The inference engine adapters and all the numbers are platform-neutral. Only the menu bar part is macOS code, so another platform needs its own small front end: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#platforms).

## Supported inference engines

| Inference engine | Status |
|---|---|
| Strata | Works |
| Demo (fake data, to try the menu without an inference engine) | Works: `STRATOSCOPE_ENGINE=demo` |
| llama.cpp, MLX, Uzu, BaseRT | Not yet |

Each inference engine has a small adapter that turns its metrics into one common format. The menu only knows that format. If your inference engine exposes metrics over HTTP, writing an adapter is usually one file. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md), or open an "Engine request" issue.

### Strata

Stratoscope reads `http://127.0.0.1:8095/metrics` by default. For the list of parked conversations it also reads Strata's log file. If you run a llama-swap router in front of Strata, set `router` and Stratoscope takes the model name and loading state from there.

**The line on the context bar.** Where a client summarises the chat is the client's decision, and Strata doesn't report it. Stratoscope reads it from the config of the clients that talk to your Strata: Kilo (`~/.config/kilo/kilo.jsonc`) and OpenCode (`~/.config/opencode/opencode.json`). It only counts a config that points at the same server address and names the model that is running, and it takes the model's context size minus its output size. Only those numbers and the server address are read, never your keys. If no config matches, or two disagree, the line uses your `compact_at`, and without that there is no line. Set `compact_at` for any other client. Set `client_configs` to `none` to turn the reading off.

Nothing is sent anywhere except requests to your Strata server. It never writes to Strata or its files. When Strata doesn't report a value, the menu shows `–` instead of guessing, and an estimated number gets a `≈`.

## Other setups

Strata listens on port 8095 unless it is started with `--port`, so the default works for a plain Strata. Everything else is a setting:

| Your setup | What you see | What to do |
|---|---|---|
| Strata only, no router | Everything works. The model name is the one Strata reports. There is no "Loading" state, because only a router knows it | Nothing |
| Strata on another port | No menu bar item at all | Set `url`. To check the address: `curl <url>/metrics` should answer with JSON |
| Strata behind a router | Model name and loading state come from the router | Set `router` |
| Strata's log is somewhere else | The parked conversations show "In use" without details, and there is no "Open log" entry | Set `log` |
| A client other than Kilo or OpenCode | No line on the context bar | Set `compact_at` to the token count where that client summarises |
| Kilo or OpenCode, but their model id differs from what Strata reports | No line on the context bar | Set `client_model` to the id used in the client config, or `compact_at` |
| Client configs in `$XDG_CONFIG_HOME` | Found there | Nothing |
| An engine other than Strata | Nothing (the item stays hidden) | Not supported yet, see [Supported inference engines](#supported-inference-engines) |

## Settings

Environment variables win over `~/.config/stratoscope.json`, which wins over the defaults. If you start the app from the Finder on macOS, use the JSON file, because the Finder doesn't pass your shell variables on. On Windows the file is `%LOCALAPPDATA%\Stratoscope\config.json`, see [docs/WINDOWS.md](docs/WINDOWS.md#settings).

| Variable | JSON key | Does |
|---|---|---|
| `STRATOSCOPE_ENGINE` | `engine` | Which inference engine to read: `strata` (default) or `demo` |
| `STRATOSCOPE_URL` | `url` | Inference engine address. Strata default: `http://127.0.0.1:8095` |
| `STRATOSCOPE_ROUTER_URL` | `router` | Router in front of the inference engine. `none` turns it off |
| `STRATOSCOPE_LOG` | `log` | Inference engine log file. Strata default: newest `*.log` in `~/.local/share/strata/install` |
| `STRATOSCOPE_WEBUI_URL` | `webui` | Adds an "Open chat UI" entry to the menu |
| `STRATOSCOPE_COMPACT_AT` | `compact_at` | Optional. Token count where your client summarises the chat, drawn as a line on the context bar. Used when Stratoscope can't find the value itself (see below) |
| `STRATOSCOPE_CLIENT_MODEL` | `client_model` | The model id your client config uses for this engine, if it differs from the name the engine reports |
| `STRATOSCOPE_CLIENT_CONFIGS` | `client_configs` | `none` stops Stratoscope from reading client configs |

Example `~/.config/stratoscope.json`:

```json
{ "url": "http://127.0.0.1:8095", "webui": "http://127.0.0.1:3000", "compact_at": 60000 }
```

## Build from source

On macOS you need the Xcode Command Line Tools (`xcode-select --install`).

```bash
git clone https://github.com/yosephyne/stratoscope.git
cd stratoscope
./scripts/build-app.sh --install    # builds and copies to /Applications
```

While working on it:

```bash
swift build                                     # build
./scripts/test.sh                               # tests
STRATOSCOPE_ENGINE=demo swift run Stratoscope   # run with fake data
./scripts/screenshot.sh                         # redo the images above (after any change to how the menu looks)
./scripts/make-dmg.sh                           # build the DMG that goes on Releases
```

On Windows you need the .NET SDK 10: `dotnet test Sources/Windows/Stratoscope.Windows.sln` and `.\scripts\windows\build.ps1`.

Pull requests are welcome, adapters most of all. Read [CONTRIBUTING.md](CONTRIBUTING.md) first, it's short.

## License

[MIT](LICENSE)
