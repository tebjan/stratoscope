# Custom fixtures

The custom 27B server (its metrics use the `q27:` prefix; `frontend/server.py`, `worker.py`). Its `/metrics` is Prometheus text sorted by name. Shared by the C# tests and any Swift port. Read-only GET requests only.

| File | What it is |
|---|---|
| `custom-idle.health.json` | Real `GET /health`, recorded 2026-11-12T02:49Z through the router (an `/upstream/<model>/health` path), engine ready. It names no engine. |
| `custom-idle.metrics.txt` | Real `GET /metrics` from the same engine and time, idle after two short requests (4 sequences, 3409 free KV pages, 1 prefix checkpoint). |
| `custom-busy.metrics.txt` | Written by hand after `worker.py` `metrics()`: three requests running, two waiting, admission held for KV, `generation_tokens_total` 5120. Field names and types follow the source and the recording; the values are made up. |
| `custom-ui-state-busy.json` | `GET /ui/state`, hand-written to the shape the engine's source defines (`worker.py` `Scheduler.stats()`, `stats.py` `tick()`, `server.py` `GET /ui/state`). It was derived from that source and was not captured from a live request; the real answer also carries logs and history. Five `seqs`: r1 running prefill, r2 running decode, r3 and r5 waiting, r4 running prefill with zero prefill time. |

`GET /props` and `GET /slots` answer 404 on this server; that is how detection tells it from llama-server.

Phase rule the tests assert: with only `/metrics` (or when `/ui/state` is missing, refused or not JSON) the custom engine shows "running" (no reading or writing, no speed). With `/ui/state` the phase of each `running` seq is the engine's own (`prefill` is reading, `decode` is writing); `waiting` is queued; `parked` and `admitted` count as running without a phase. There is no expected file: the tests assert the values.

| `custom-ui-state-idle.json` | Live capture of `GET /ui/state` from 2026-11-12 (endpoint `/ui/state` of the engine, idle after two short requests, RTX 5090 Laptop GPU). The real shape is `info`, `started`, `history`, `log`, and `tick` (`seqs`, `vram`, `cache`, `sample`, `sched`). |

`custom-ui-state-busy.json` uses the same `tick` wrapper as the capture (the first hand-written version had `seqs` at the top level, which the real engine does not send). `/ui/state` is read on every poll: the card, VRAM parts, KV cache, engine settings and finished requests (`log` events of kind `done`) are only there.
