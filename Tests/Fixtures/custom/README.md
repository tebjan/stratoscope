# Custom fixtures

The custom 27B server (its metrics use the `q27:` prefix; `frontend/server.py`, `worker.py`). Its `/metrics` is Prometheus text sorted by name. Shared by the C# tests and any Swift port. Read-only GET requests only.

| File | What it is |
|---|---|
| `custom-idle.health.json` | Real `GET /health`, recorded 2026-11-12T02:49Z through the router (an `/upstream/<model>/health` path), engine ready. It names no engine. |
| `custom-idle.metrics.txt` | Real `GET /metrics` from the same engine and time, idle after two short requests (4 sequences, 3409 free KV pages, 1 prefix checkpoint). |
| `custom-busy.metrics.txt` | Written by hand after `worker.py` `metrics()`: three requests running, two waiting, admission held for KV, `generation_tokens_total` 5120. Field names and types follow the source and the recording; the values are made up. |

`GET /props` and `GET /slots` answer 404 on this server; that is how detection tells it from llama-server.

Phase rule the tests assert on the busy file: the custom engine shows "running" only (no speed), whenever requests are running, until it reports per-request phases (see the to-do in ops `tasks/todo.md`, section "Custom engine: per-request phase for Stratoscope"). Neither reading nor writing is shown. There is no expected file: the tests assert the values.
