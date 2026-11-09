# Architecture

Stratoscope is built so that **the menu knows nothing about any engine**. Engines are adapters.

```
 engine server ──HTTP/log──▶ EngineProvider ──▶ EngineSnapshot ──▶ Panels (AppKit) ──▶ menu bar
 (Strata, …)                 one per engine     engine-neutral      draws what it gets
```

| Folder | Contents | Imports AppKit? |
|---|---|---|
| `Sources/StratoscopeCore/Snapshot.swift` | The engine-neutral data model | no |
| `Sources/StratoscopeCore/EngineProvider.swift` | The adapter protocol and the registry | no |
| `Sources/StratoscopeCore/Engines/<Name>/` | One folder per engine | no |
| `Sources/StratoscopeCore/Config.swift`, `Format.swift`, `CacheHit.swift` | Settings, number formatting, cache verdict | no |
| `Sources/Stratoscope/` | The macOS app: status item, menu, drawing, screenshot renderer | yes |

Everything that can be tested without a screen lives in `StratoscopeCore` and is covered by `Tests/`.

## Platforms

`StratoscopeCore` knows nothing about any UI. It only imports Foundation, so everything an engine adapter does (fetching, parsing, estimating, formatting) is shared by all platforms. The UI is the only platform-specific part: today `Sources/Stratoscope` is the macOS app, built with AppKit. Windows is a second implementation in C# (`Sources/Windows`, `Core` and `App`), kept in step by [Shared behaviour](#shared-behaviour) and the recorded payloads in `Tests/Fixtures`: see [WINDOWS.md](WINDOWS.md).

To add a platform:

1. Add a new target next to `Sources/Stratoscope` (for example `Sources/StratoscopeWindows`) that depends on `StratoscopeCore`.
2. Poll an `EngineProvider` every two seconds and draw the `EngineSnapshot` it returns: a menu bar or taskbar item with the speed, and a panel with the sections. Hide the item when the provider returns `nil`.
3. Keep platform code out of `StratoscopeCore`. If the core needs something platform-specific, put it behind a small protocol.
4. On platforms outside Apple's, `URLSession` lives in `FoundationNetworking`, and paths like `~/.config/stratoscope.json` may need a per-platform location in `Config.swift`.

Open an issue first so the design can be agreed before the code.

## The snapshot

`EngineSnapshot` holds what the menu can show: status (loading, idle, prefill, thinking, decode), the running request, finished requests, context usage, and three **optional** sections: `system` (RAM, GPU, CPU), `cache` (prompt cache and parked conversations) and `totals`. A section that is `nil` is not drawn. An engine that has no cache stats simply gets a shorter menu.

## Where the context numbers come from

| Shown | Source | If missing |
|---|---|---|
| `X of Y` and the bar | `X`: prompt plus generated tokens of the running request, or of the last request when idle. `Y`: the engine's context limit (`ContextUsage.max`) | no limit: just `X`, no bar fill |
| Marker | `ContextUsage.compactAt`, see below | no marker |
| `N free` | limit minus `X`, only when both are known (`ContextUsage.free`) | not shown |
| Bars under the context bar (one per finished request, last 12) | prompt plus output tokens of each finished request (`RequestStats.contextTokens`) | not shown |

The marker on the bar is `ContextUsage.compactAt`: the token count where the *client* summarises or trims the conversation. It is `nil` unless a source knows it, and then nothing is drawn. The adapter decides the source. An engine that reports it passes it through. Strata's `/metrics` has no such field, so the Strata adapter takes, in this order:

1. The client configs read by `ClientConfigReader` (`ClientLimits.swift`): Kilo's `kilo.jsonc` and OpenCode's `opencode.json`. A config counts when its `baseURL` has the same host and port as the router, `url` or proxy, and it names the running model. The value is the model's `limit.context` minus `limit.output`, which is the rule observed for Kilo. `client_model` names the model id used in the client config when it differs from the engine's name. Two matching configs that disagree give `nil`.
2. The user's `compact_at` setting, as the fallback for everything that cannot be read (other clients, other models).
3. Nothing: no marker.

The reader only looks at the server address and the `limit` numbers of each model, and never uses, stores or logs a key. Another client is one more entry in `ClientConfigReader.sources` and, if it summarises by another rule, its own `compactAt`. Test it with a sample config, like `ClientLimitsTests`.

The bars below the context bar need no setting: a sudden drop is where a client started over, and the cache dots show what that cost.

## Screenshots

The images in `docs/img` are rendered from `DemoProvider`'s fixed data by `./scripts/screenshot.sh`, not captured by hand: the menu is painted into an opaque 2x PNG, and the demo's clock is pinned, so unchanged code gives byte-identical files. After any change to how the menu looks, run the script and commit the images with the change. To add a screenshot, add a `shoot` line to the script and reference the file in the README.

## Adding an engine

1. Create `Sources/StratoscopeCore/Engines/MyEngine/MyEngineProvider.swift` and conform to `EngineProvider`:

   ```swift
   public final class MyEngineProvider: EngineProvider {
       public static let id = "myengine"
       public init(config: Config) { … }
       public func poll(_ completion: @escaping (EngineSnapshot?) -> Void) {
           // fetch the engine's metrics, translate, then call completion once on the main queue.
           // Pass nil if the engine is not running: the menu bar item hides itself.
       }
   }
   ```

2. Register it in `EngineRegistry` (`EngineProvider.swift`): add the id to `available` and a case to `make`.
3. Add a test that feeds a recorded metrics payload through your translation, like `StrataSnapshotTests`.
4. Document the engine's defaults in the README.

Guidelines:

- **Report only what the engine knows.** Prefer `nil` over a guess. Where you must estimate (Strata does not report cache reuse of a running request), say so in a comment.
- **Never block.** `poll` runs every two seconds on the main queue. Use `JSONFetcher` (1.5 s timeout) and call `completion` when done.
- **Keep engine code out of the UI.** If the menu needs something new, add it to `EngineSnapshot` as an optional field and make it useful for every engine.
- Use `DemoProvider` as the smallest example.

## Shared behaviour

What every front end does the same way. The payloads in `Tests/Fixtures` with their `*.expected.json` files (see the README there) pin it down; the C# tests read them, the Swift tests do not yet.

- **Missing values:** a key that is missing or `null` is unknown (`–` or left out, never `0`); a number sent as text is not a number. A missing section (`hardware`, `conversation_cache`, `totals`) is not drawn. Shown on purpose: the title is padded to three digits and reads `0 t/s`, and the running request's cache reuse carries `≈`.
- **Numbers:** English formatting whatever the system language (`1,234,567`, `26.4`), halves round to even, durations `42 s`, `2 min 5 s`, `3 h 12 min`, sizes in GiB with one decimal.
- **Strata sources:** router mode asks `GET <router>/running`, then `GET <router>/upstream/<model>/metrics`; direct mode `GET <url>/metrics`. Nothing else. Status comes from `live.state` and `live.phase`; speeds from `live.prefill_tok_s_mean` (else the progress of `live.prompt_read` between polls) and `live.tok_s`; context from `live.prompt_tokens + live.generated` of `engine.max_context`; requests from `requests[]`; RAM, GPU, CPU from `hardware`; the prompt cache from `conversation_cache`; totals from `totals`.
- **States:** connecting (nothing shown), offline, no model (router answers, `running` empty; nothing is woken), loading (router state is not `ready`), then ready, reading, thinking, writing. An HTTP 429 from the router keeps the last data on screen, marked.
- **Settings:** environment variable, then the settings file, then the default. A router without `url` is asked alone, never `127.0.0.1:8095`.

## Not built yet

- More than one engine at a time (the registry picks one via `STRATOSCOPE_ENGINE`).
- Localisation. Strings are English literals in `Panels.swift` and `AppDelegate.swift`.
