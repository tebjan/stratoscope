# Security Policy

## Reporting a vulnerability

Please report security issues **privately** via GitHub: **Security → Advisories → Report a vulnerability**. For non-sensitive concerns a regular issue is fine. Reports are acknowledged within a few days.

## Security posture

Stratoscope is a **read-only monitor**:

- **No `sudo`, no privilege escalation.** It runs as your user.
- **Nothing leaves your Mac.** No telemetry, no analytics, no update check. The only network traffic is HTTP to the engine you configured, by default on `127.0.0.1`.
- **Read-only.** It reads the engine's metrics endpoint and, for Strata, the engine's log file. It never writes to the engine, its files or its process.
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

## Supported versions

The latest release and `main` receive fixes.
