# Fixtures

Recorded `/metrics` payloads, shared by the tests of every Stratoscope implementation. The behaviour they pin down is
written once, in [docs/ARCHITECTURE.md](../../docs/ARCHITECTURE.md#shared-behaviour).

| File | What it is |
|---|---|
| `strata-0.1.56-idle.json`, `-prefill.json`, `-thinking.json`, `-decode.json` | Real `/metrics` documents of Strata engine 0.1.56 (CUDA) behind a llama-swap router v270, recorded read-only with GET requests, one per state. They hold model names, counters and hardware numbers only. |
| `strata-parallel-two-of-four.json`, `-decode.json`, `-decode-later.json`, `-mixed.json`, `-mixed-later.json`, `-reading.json` | `live` documents with requests running in batch slots, shaped after `slots_view` of Strata's `serve/server.py`: a decoding slot has `prompt_tokens`, `generated`, `elapsed_s`, `tok_s` (not used: see docs/WINDOWS.md) and no `held_tokens`; an idle slot has `held_tokens`; a reading slot has `prompt_read`. The first holds the values reported by a live engine with two of four slots decoding (the rest is filled in by hand), the others are written by hand. The `-later` files are the same requests two seconds on. They have no expected file: the tests assert totals and speeds. |
| `hand-made-missing-fields.json` | Written by hand: most optional fields are missing or `null`, as a different engine or an older Strata would send them. |
| `router-running-ready.json`, `-starting.json`, `-empty.json` | The router's `/running` answer with a ready model, a model that is starting, and no model. The `cmd` field of the real answer is left out. |
| `<name>.expected.json` | What a correct implementation derives from `<name>.json` (see below). |

## Expected values

`<name>.expected.json` is flat. A key that is `null` means "unknown": the implementation must show `–` or nothing.

| Key | Meaning |
|---|---|
| `status` | `idle`, `prefill`, `thinking` or `decode` |
| `title` | the menu bar or taskbar title: the speed that matters right now, padded to three digits with figure spaces (U+2007), or `ready` (the Windows app writes `Ready`: its lines start with a capital) |
| `queued` | requests waiting |
| `percent` | prefill progress in whole percent, while reading |
| `context_used`, `context_max` | context bar numbers |
| `recent` | number of finished requests |
| `last_request_hit` | cache verdict of the newest finished request: `hit`, `partial`, `miss`, `small` |
| `running_cache_hit` | cache verdict of the running request (approximate), while reading |
| `cache_slots`, `cache_parked` | the conversation cache numbers |
| `batch_slots` | number of entries in `live.slots` |
| `has_system`, `has_totals` | whether those sections are drawn |

The C# tests in `Tests/StratoscopeWindowsTests` read these files. The Swift tests do not use them yet.
