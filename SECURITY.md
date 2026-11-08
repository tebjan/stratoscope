# Security Policy

## Reporting a vulnerability

Please report security issues **privately** via GitHub: **Security → Advisories → Report a vulnerability**. For non-sensitive concerns a regular issue is fine. Reports are acknowledged within a few days.

## Security posture

Stratoscope is a **read-only monitor**. The Windows app follows the same rules, including the client config reading (from `%USERPROFILE%\.config\kilo` and `\opencode`, or `%XDG_CONFIG_HOME%`), and installs per user, without administrator rights:

- **No `sudo`, no privilege escalation.** It runs as your user.
- **Nothing leaves your Mac.** No telemetry, no analytics, no update check. The only network traffic is HTTP to the engine you configured, by default on `127.0.0.1`.
- **Read-only.** It reads the engine's metrics endpoint and, for Strata, the engine's log file. To place the summarise marker it also reads the configs of the clients Kilo and OpenCode, and from them only the server address and the context and output sizes, never a key (turn it off with `client_configs`: `none`). It never writes to the engine, its files or its process, and never writes to a client's config.
- **No private system APIs.** Only public APIs of the platform.
- **No remote code.** There is no auto-updater. You update by downloading the new version from Releases.

## Releases

Releases are built by GitHub Actions from a version tag (`.github/workflows/release.yml`), so the DMG on the Releases page comes from the public source. Each release has a `.sha256` file:

```bash
shasum -a 256 -c Stratoscope-<version>.dmg.sha256
```

On macOS the app is signed ad hoc and not notarized yet, which is why it asks for "Open Anyway" on the first start. You can also build it yourself:

```bash
./scripts/build-app.sh
```

The Windows zip comes from the same workflow, is not code-signed yet, and has a `.sha256` file too: `(Get-FileHash Stratoscope-<version>-windows.zip).Hash` must match it.

## Supported versions

The latest release and `main` receive fixes.
