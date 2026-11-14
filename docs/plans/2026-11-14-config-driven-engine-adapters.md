# Config-driven engine adapters (plan)

Status: **draft for discussion, nothing built.** Branch `windows-code-quality`, app version stays 0.3.4 (a definition file has its own schema number, it never bumps the app). Written 2026-11-14.

Evidence labels used throughout: **[src]** read from the engine's source at a named tag, **[docs]** official documentation, **[probe]** live read-only GET on this machine on 2026-11-14 (llama-swap v270 in front of the custom Autobahn engine), **[fixture]** a recording in `Tests/Fixtures`, **[proposal]** my design choice, not a fact. Raw captures: `side-session-notes/.../probes/`.

## Summary

1. One definition file per engine (JSON, schema `stratoscope.engine/1`) replaces the per-engine adapter classes. A generic engine reads the file: which URLs to ask, how to recognise the engine, how each response field maps to a display field, and which derived values to compute. **[proposal]**
2. Metrics come in four classes: live counter, end-of-request counter, gauge, absent. The sample store keeps a quality flag per sample (measured, reconstructed, estimated), so end-of-request engines such as llama.cpp still give a curve: estimated while the request runs, replaced by the exact average when it ends. **[proposal]**
3. Each platform keeps one small interpreter (about 600 lines each) and both pass the same fixtures. No code per engine. **[proposal]**
4. Two surprises from the probes: our Autobahn engine already exposes standard vLLM metric names next to its `q27:` ones, and llama-swap has a per-request activity feed that works for any engine behind it.

## 1. Assessment of existing standards and engines

Versions read on 2026-11-14 from raw source at the tag: vLLM v0.32.0, SGLang v0.5.23, llama.cpp v1.0.0, llama-swap v272 (source) and v270 (live probe), Ollama v0.40.16, mlx-lm v0.32.2, mlx-vlm v0.7.12, vllm-mlx v0.6.0, oMLX v0.8.0rc1; LM Studio from its current docs (closed source). Full tables with line numbers and permalinks: `research-A-*.md` and `research-B-*.md` in the notes folder. Classes: **L** live, **E** end-of-request, **G** gauge, **W** windowed gauge, **A** absent.

### 1.1 Standards and who follows them

| Standard | Status | What it gives a monitor | Followed by |
|---|---|---|---|
| vLLM metric names (`vllm:*`, Prometheus) | de facto, vLLM v0.32.0 **[src]** | running, waiting, KV usage, token counters, TTFT, ITL, spec decode, prefix cache | vLLM; **our Autobahn engine** (probe: 31 `vllm:*` metric families, same values as its `q27:` counters); SGLang only partly (same names for TTFT, ITL, TPOT, e2e, prompt and generation tokens, different names and timing for running, queue, KV); vllm-mlx uses its own `vllm_mlx_*` names |
| OpenTelemetry GenAI semantic conventions | **Development** (no stable release), moved to `semantic-conventions-genai` **[src, main e6658f1]** | request duration, TTFT, time per output token as histograms; token usage counters; no queue, KV or live throughput | none of the engines here emit `gen_ai.server.*` metrics; vLLM emits OTel traces with older `gen_ai.usage.*` span attributes |
| OpenAI `usage` object | de facto API **[src: openai-openapi f437d0e]** | `prompt_tokens`, `completion_tokens`, cached and reasoning details; Responses API uses `input_tokens`/`output_tokens` | all OpenAI-compatible servers; always end-of-request (final stream chunk). vLLM adds `continuous_usage_stats` (per chunk), not standard |
| Prometheus text 0.0.4 / OpenMetrics | stable | the wire format of most metrics here | vLLM, SGLang, llama.cpp, llama-swap (host only), Autobahn, vllm-mlx |

Consequence **[proposal]**: the definition vocabulary uses the **vLLM names as the neutral names** (`running`, `waiting`, `kv_usage`, `prompt_tokens`, `generation_tokens`, `ttft`, `itl`) because most engines map onto them with a rename, and OTel names are kept as an export alias only. Stratoscope reads, never produces, telemetry, so OTel is a naming reference and not a format to implement.

### 1.2 Engines

Evidence: **[src]** source at tag, **[docs]**, **[probe]**, **[fix]** recorded fixture in `Tests/Fixtures`.

| Engine (version) | Endpoints that matter | Live speed source | End-of-request | Gauges | Absent | Evidence |
|---|---|---|---|---|---|---|
| **vLLM** v0.32.0 | `/metrics`, `/health` (empty 200/503), `/load` (needs `--enable-server-load-tracking`), `/version`, `/v1/models`, dev-only `/server_info`, `/is_sleeping` (need `VLLM_SERVER_DEV_MODE`) | decode **L**: `vllm:generation_tokens_total` per step; `vllm:iteration_tokens_total` histogram per step; `time_to_first_token_seconds`, `inter_token_latency_seconds` L | `request_*` histograms, `e2e_request_latency_seconds`, `request_success_total`; **`prompt_tokens_total` jumps at first output, not per chunk** (so prefill is bursty) | `num_requests_running`, `num_requests_waiting`, `kv_cache_usage_perc` (0 to 1; old `gpu_cache_usage_perc` name gone in v0.32.0), prefix cache counters, spec decode counters (per step), `num_preemptions_total` | live prefill rate (proxy via `iteration_tokens_total` minus generation: accuracy UNVERIFIED); `avg_*_throughput` gauges removed | **[src]** `vllm/v1/metrics/{loggers,stats}.py`, scheduler.py |
| **SGLang** v0.5.23 | `/metrics` (needs `--enable-metrics`), `/health`, `/health_generate` (runs a 1-token generation when idle: not safe to poll), `/get_model_info`, `/get_server_info`, `/v1/models`, `/v1/loads` (JSON per DP rank) | `sglang:realtime_tokens_total{mode=prefill_compute\|prefill_cache\|decode}` **L**; `sglang:gen_throughput` gauge ticks every 40 decode iterations | `sglang:prompt_tokens_total`, `generation_tokens_total`, histograms (**E**: opposite of vLLM for generation) | `num_running_reqs`, `num_queue_reqs`, `token_usage`, `cache_hit_rate`, spec accept rate | `/get_load`, `/get_loads` not found | **[src]** `metrics_collector.py`, `http_server.py`, `v1_loads.py` |
| **llama.cpp** server v1.0.0 | `/health` (503 while loading), `/props` (`total_slots`, `build_info`, `is_sleeping`), `/slots` (default on), `/metrics` (needs `--metrics`), `/v1/models`; router mode: `/models` with `status.value` unloaded/loading/loaded/sleeping, `?model=` on GET | decode **L only through `/slots` `next_token[0].n_decoded`**; prefill **L**: `prompt_tokens_total`, `prompt_seconds_total`, `n_decode_total` per decode batch, `/slots` `n_prompt_tokens_processed` | `tokens_predicted_total`, `tokens_predicted_seconds_total`, spec decode counters (**only on slot reset**); `predicted_tokens_seconds` (filled at request end); `timings` in the final chunk (per chunk with `timings_per_token`); `usage` | `requests_processing`, `requests_deferred`, `/slots` `is_processing`; **`prompt_tokens_seconds` and `predicted_tokens_seconds` use one bucket reset on every scrape** | KV usage (`kv_cache_usage_ratio`, `kv_cache_tokens` not in v1.0.0), per-slot tokens/s | **[src]** `server-context.cpp`, `server-task.cpp`, `server-common.h`; **[fix]** `llamacpp-*` of build `b1-d493f84` |
| **Ollama** v0.40.16 | `/api/version`, `/api/tags`, `/api/ps` (`models[]`: `size_vram`, `expires_at`, `context_length`), `/api/show`; OpenAI routes | none | `/api/generate` and `/api/chat` final chunk: `total_duration`, `load_duration`, `prompt_eval_count`, `prompt_eval_duration`, `eval_count`, `eval_duration` (ns); OpenAI `usage` | `/api/ps` (listed = loaded) | `/metrics` (open PR 18508, not merged), queue, slots, live rates | **[src]** `server/routes.go`, `api/types.go` |
| **LM Studio** (docs) | `/api/v1/models` (`loaded_instances[]`), `/api/v1/chat` (SSE), older `/api/v0/*`, OpenAI routes, CLI `lms ps --json`, `lms server status --json` | none for a monitor (SSE `prompt_processing.progress` goes only to the requester) | `stats`: `tokens_per_second`, `time_to_first_token_seconds`, `input_tokens`, `total_output_tokens` | `loaded_instances` | Prometheus, documented health route, queue, KV | **[docs]** lmstudio.ai/docs/developer/rest |
| **mlx-lm** v0.32.2 | `/health`, `/v1/models`, OpenAI routes | none | `usage` tokens only | `/health` | everything else | **[src]** `mlx_lm/server.py` |
| **mlx-vlm** v0.7.12 | `/health` (rich: `loaded_model`, `effective_context_limit`), JSON `/metrics`, `/v1/cache/stats` | `in_flight` **L** | per request: `prefill_tok_s`, `decode_tok_s`, `ttft_s`, token counts, `avg_decode_tok_s` | `in_flight`, `loaded_context_size` | Prometheus | **[src]** `mlx_vlm/server/app.py`, `generation.py` |
| **vllm-mlx** v0.6.0 | `/metrics` (Prometheus, opt-in `--enable-metrics`), `/v1/status` (`num_running`, `num_waiting`, `generation_tps`, `prompt_tps`), `/health` | `generation_tps`/`prompt_tps` (liveness UNVERIFIED) | token counters, TTFT histogram at completion (timing UNVERIFIED) | `vllm_mlx_scheduler_running_requests`, `waiting_requests`, cache gauges | full metric list UNVERIFIED | **[src]** `vllm_mlx/server.py`, `metrics.py` |
| **oMLX** v0.8.0rc1 | `/health`, `/api/status` (`active_requests`, `waiting_requests`, `avg_prefill_tps`, `avg_generation_tps`; needs API key), `/v1/models/status` | none (averages) | `usage.prompt_tokens_per_second`, `generation_tokens_per_second` in responses | active, waiting | Prometheus | **[src]** `omlx/server.py` |
| **llama-swap** v272 / v270 | see 5.3 | none own | `/api/metrics/activity` per request | `/running`, `/v1/models` status, `/api/events` | token metrics in `/metrics` | **[src]** + **[probe]** |
| **Strata** (local clone, fixtures 0.1.56) | `/health`, `/metrics` (**JSON despite the name**: `engine`, `live{state,phase,slots[],tok_s,prefill_tok_s_mean,prompt_read,generated,queued,waiting}`, `requests[]`, `conversation_cache`, `totals`, `hardware`), `/slots` | `live.slots[].generated` and `.prompt_read` **L** (per token and per chunk); `live.tok_s` | `requests[]`, `totals` (added when a request ends, `serve/server.py`) | `hardware.*`, `conversation_cache` | cumulative live counter | **[fix]** `strata-0.1.56-*.json`; **[src]** `research-repos/Strata/serve/server.py` (`/metrics` L7108, `/health` L7331, `/slots` L7384, `slots_view` L2312) |
| **Basalt** 0.3.2 | `/health` (`service:"basalt"`, `loaded`, `percent`), `/metrics` JSON (`engine`, `live{state,slots,tok_s,prefill_tok_s_mean,queue,load}`, `requests[]`, `hardware`, `best`), `/slots` (per slot `state`, `tok_s`, `generated`, `prefill_done`, `reused`, `ttft_s`, `drafts_offered`, `drafts_accepted`) | `/slots` `tok_s`, `prefill_done` **L** | `requests[]` | `hardware` | cumulative counter | **[fix]** `basalt-0.3.2-*` (recorded read-only from a live engine) |
| **Autobahn / custom** (rc `serving-rc4-20261114-0745`, Python `BaseHTTP`) | `/health` (`{"status":"ok"}`), `/metrics` (46 KB Prometheus: `q27:*` and `vllm:*`), `/v1/models`, `/ui/state` (563 KB JSON) | **L**: `q27:prefill_tokens_total` (per chunk), `q27:generation_tokens_total` (equal to `vllm:generation_tokens_total` in the probe) | `q27:prompt_tokens_total` = `vllm:prompt_tokens_total` (24,024,018 in the probe: once per request, includes cached), `vllm:request_*` histograms, `vllm:request_success_total{finished_reason}` | `vllm:num_requests_running`, `num_requests_waiting`, `kv_cache_usage_perc`, `prefix_cache_*`, `spec_decode_*`, `num_preemptions_total` | `/slots`, `/props` (404) | **[probe]** 2026-11-14; ops doc "standard vLLM metrics on /metrics" (rc3) |

Findings that change the design:
1. Autobahn already speaks vLLM names. Its definition can be the **vLLM definition plus a `q27:` overlay** for the live prefill counter, instead of a code adapter with a 179-line DTO.
2. vLLM's own prefill counter is bursty and SGLang's token counters are end-of-request, so "same names" does not mean "same class". The definition therefore carries the class per field. **Never assume the class from the name.**
3. llama.cpp is the hard case: live prefill from counters, live decode only from `/slots`, decode totals and speed gauges at request end. The existing adapter was right to prefer `/slots`; the end-of-request counters become the settlement of section 3.
4. Four engines (Ollama, LM Studio, mlx-lm, and mlx-vlm for live speed) offer no live speed to a passive monitor. They get state-only definitions plus, behind llama-swap, the router activity feed.

### 1.3 Safety facts for probing

| Fact | Evidence |
|---|---|
| llama-swap `/upstream/<model>/...` loads an unloaded model for any path not matching `upstream.ignorePaths` (default covers only static file types) | **[src v272]** `internal/server/api.go`, `internal/config/upstream.go` |
| llama.cpp `/health`, `/props`, `/models`, `/metrics` do not wake a sleeping model; `/slots` is not documented as safe | **[src v1.0.0]** README, `server-context.cpp`; current adapter already skips `/slots` for a sleeping server |
| SGLang `/health_generate` runs a generation | **[src]** `http_server.py` |
| All my probes hit only a model whose router state was `ready` | **[probe]** `/running` first |


## 2. What Stratoscope has today

### 2.1 Inventory (verified in the repo, branch `windows-code-quality`)

| Piece | Windows (C#, `Sources/Windows/Core`) | macOS (Swift, `Sources/StratoscopeCore`) |
|---|---|---|
| Adapter interface | `IEngineProvider.PollAsync`, `LastError`, `ReadCountersAsync` (`EngineProvider.cs`) | `EngineProvider.poll(completion)` only, no counter read |
| Router flow | `RoutedProvider.cs` (185 lines): `/running`, wait while loading, read `/upstream/<model>/`, 429 keeps last data | folded into `StrataProvider.swift` (268 lines); uses the `proxy` address from `/running` instead of `/upstream` |
| Engine detection | `AutoEngineProvider.cs`: `/health` `service`, then `/props`, then `/metrics` with `q27:` | same idea inside `StrataProvider` |
| Strata | `StrataProvider.cs` 226 + `StrataCacheLog.cs` 121 + DTOs | `StrataProvider.swift` + `StrataCacheLog.swift` |
| Basalt | `BasaltProvider.cs` 202 + `BasaltDto.cs` 117 | `BasaltReader.swift` 205 |
| llama.cpp | `LlamaCppProvider.cs` 176 + `LlamaCppDto.cs` 113 | `LlamaCppReader.swift` 305 |
| Custom (Autobahn) | `CustomProvider.cs` 258 + `CustomDto.cs` 179 (reads `/ui/state` JSON and `/metrics` `q27:`) | `CustomReader.swift` 386 |
| Prometheus text | `PrometheusText.cs` (34 lines, prefix filter, no types, no label query) | `PrometheusText.swift` (25 lines) |
| Per-slot speed | `SlotRateTracker.cs` | `SlotRateTracker.swift` |
| History and scale | `SpeedHistory.cs` (120 s window, 0.5 s samples, 20 s warm-up, one-pole filter, per-curve `SpeedScale`) | `SpeedHistory.swift`, `SpeedScale.swift` |
| Light counter read | `EngineCounters`, `ServerMonitor.SampleAsync` every 0.5 s | `addCounters` exists in `SpeedHistory`, but no provider hook calls it |
| Server detection | `ServerDetection.cs` (client configs, usual ports, private nets incl. 100.64/10) | `ServerDetection.swift` |
| Several servers, tabs | `ServerMonitor`, `Servers`, one readout per server | no (WINDOWS.md: "Several servers: no") |
| Readout bars, panel | `RequestBars`, `ReadoutPainter`, `PanelPainter` (taskbar) | `Drawing.swift`, `Panels.swift` (menu bar), bars via `RequestBars` |
| Tests | 23 C# test files, 35 tests; shared `Tests/Fixtures` with `*.expected.json` | 8 Swift test files; Strata fixtures not read yet |

Lines of adapter code that a definition could replace: about 1,900 in C# (Core/Engines without `RoutedProvider`, `SlotRateTracker`, `StrataCacheLog`) and about 1,200 in Swift. **[proposal]** estimate, from the line counts above.

### 2.2 What macOS lacks against Windows (from code and docs)

| Gap | Evidence |
|---|---|
| Several servers and tabs | WINDOWS.md "Differences from the Mac app" |
| Router limit (HTTP 429) handling, last data kept with a note | same table |
| Light counter read every 0.5 s (`ReadCountersAsync`), so the 120 s curve at 0.5 s resolution is fed by 2 s polls only | no `readCounters` in any Swift provider; `addCounters` unused |
| `/upstream/<model>` routing (Mac uses the `proxy` address) | same table |
| Per-bar tooltips, sum bar plus per-request bars in the panel | same table |
| An `AutoEngineProvider` equivalent as a separate unit | Swift has one provider that dispatches inside |
| Swift is "mirrored (unbuilt)" for the newest changes (per-slot speeds, counters, warm-up) | commit messages `85b307f`, `c8a5f03` |

### 2.3 Linux

No Linux build exists: README says "Linux: Not yet". `Package.swift` declares `.macOS(.v13)` only. `StratoscopeCore` imports only Foundation, so it can compile on Linux with `FoundationNetworking`; the UI would be a new front end (ARCHITECTURE.md "To add a platform"). CI runs on macOS and Windows runners only. **[proposal]** The interpreter should be Linux-clean from day one (no AppKit, no Windows API), and a Linux front end stays out of this plan.

### 2.4 Generic engine versus definition data **[proposal]**

| Becomes the generic engine (code, once per platform) | Becomes definition data (per engine) |
|---|---|
| `IEngineProvider`, `ServerMonitor`, polling loops, `EngineSnapshot` model | Endpoint list, which are cheap enough for the 0.5 s counter read |
| Router layer from `RoutedProvider` (`/running`, loading, 429, upstream path) | Router type: llama-swap definition (paths, state names) |
| Detector (replaces `AutoEngineProvider`) | Fingerprint rules per engine |
| Prometheus parser with types and label selectors | Metric names and label selectors |
| JSON path reader | JSON paths into `/slots`, `/props`, `/metrics` JSON, `/api/ps` |
| Derived-value operators (rate, sum, ratio, coalesce) | Which operators on which inputs |
| `SlotRateTracker` as operator `slotRate` | Which slot field is the counter |
| Status rules engine (idle, prefill, decode) | The rule list per engine |
| `SpeedHistory`, sample store, `SpeedScale`, `RequestBars` | Metric class per field (live, end-of-request, gauge) |
| Client config reader, cache verdict, formatting | Brand, links, capability flags (`wakesWhenAsked`, needs flag) |

Stays native, behind a named hook: `StrataCacheLog` (parses the engine's log file, not an HTTP response). Everything else of Strata, Basalt, llama.cpp and Custom is a mapping. **[proposal]**

## 3. Real-time versus end-of-request

### 3.1 Metric classes

| Class | Meaning | Example |
|---|---|---|
| **L** live counter | cumulative, advances while a request runs (per chunk or per step) | `q27:prefill_tokens_total`, llama.cpp `prompt_tokens_total`, `/slots` `n_decoded` |
| **E** end-of-request counter | cumulative, jumps when a request ends | llama.cpp `tokens_predicted_total`, Strata `totals.*`, Ollama response fields |
| **G** gauge | value now, no history | `requests_processing`, `kv_cache_usage_perc`, `/api/ps` |
| **W** windowed gauge | a rate the engine computed over a window it owns | llama.cpp `predicted_tokens_seconds` (resets per scrape) |
| **A** absent | the engine says nothing | Ollama queue depth |

Owner's note answered: llama.cpp's `tokens_predicted_total` and `tokens_predicted_seconds_total` do advance only when the slot is reset at the end of a request, and `predicted_tokens_seconds` is filled only at request end and reset by every scrape **[src: llama.cpp v1.0.0, `server-context.cpp` `metrics_on_prediction` via `slot.callback_on_reset`, `server-task.cpp` `to_metrics`]**. `prompt_tokens_total`, `prompt_seconds_total` and `n_decode_total` advance per decode batch, so prefill is live **[src, same]**. So they are usable, but not as a live decode curve: they are exact totals that arrive late. The curve design below uses them for exactly that.

### 3.2 Options weighed

| Option | Gives | Cost | Verdict |
|---|---|---|---|
| A. Backfill: when a counter jumps, spread its tokens over the request's measured duration, draw with a display delay | exact average rate, correct total | the curve lags by the request length; a 90 s request shows nothing for 90 s | use for the settled part only |
| B. Estimate live: while `requests_processing > 0`, draw the last known rate | immediate feeling of activity | invented numbers; conflicts with AGENTS.md "don't estimate" | allowed only if drawn as estimate, see 3.4 |
| C. Mark estimated segments | honest | needs a quality flag in storage and a second drawing style | required by A and B |
| D. Use a live side source instead (llama.cpp `/slots` `n_decoded`) | true live decode curve | one more GET, polling at 1 to 4 Hz | **primary for llama.cpp**, already what the adapter does |
| E. Router activity feed (llama-swap `/api/metrics/activity`) | per request: input, output, cache tokens, tokens per second, duration, for any engine behind llama-swap | end-of-request only; needs the router | **settlement source** for engines with no better counters |

**Recommendation [proposal]: C + A + D/E.** Per engine the best live source is used (D, or counters of class L). Where there is none, an estimate (B) is drawn dashed while requests run (gauge says running), and when the end-of-request counter or the activity record arrives, the interval is overwritten with the reconstructed average (A) and drawn solid. No display delay; the past is revised instead. The graph already recomputes the smoothed curve from raw samples on every paint (`SpeedHistory.ReadSmoothed`), so revising raw samples needs no new smoothing logic.

### 3.3 Sample storage **[proposal]**

One ring buffer per curve (prefill, decode, total), 140 s deep (120 s window plus 20 s warm-up, as today). A sample is:

| Field | Meaning |
|---|---|
| `t` | receive time on this machine (monotonic), never the engine's clock (remote machines, skew) |
| `v` | tokens per second, NaN = unknown |
| `q` | quality: **M** measured (delta of a live counter or a live slot count), **R** reconstructed (exact average of a finished request spread over its interval), **E** estimated (last known rate while a request runs), **U** unknown |
| `src` | short source id (`q27:generation_tokens_total`, `slots:n_decoded`, `activity:387`) for the tooltip |

Rules:
1. M and R samples feed the adaptive scale and the legend averages; E samples feed neither (an estimate must not move the axis or the number).
2. `Revise(t0, t1, v, R)` overwrites E samples inside an interval; M samples are never overwritten (a live measurement wins over an average).
3. Interval placement for an end-of-request jump: the busy interval is bounded by the gauge (first poll with running above 0, to the poll that saw the jump). When the engine also reports cumulative seconds (llama.cpp `prompt_seconds_total`, `tokens_predicted_seconds_total`), the decode part sits in the last `Δpredicted_seconds` before the end and the prefill part before it. With a router activity record the interval is exact (`duration_ms`, `prompt_per_second`, `tokens_per_second`).
4. Parallel requests: batch seconds and counters are totals over all requests, so the rule stays valid; the result is a total rate, which is what the headline rule wants.

### 3.4 Drawing and filtering **[proposal]**

| Element | M | R | E | U |
|---|---|---|---|---|
| History curve | solid | solid (appears when settled) | dashed, 50 % opacity | gap |
| Legend average (120 s) | counted | counted | not counted | not counted |
| Adaptive scale | feeds | feeds | does not feed | no |
| Readout bar for a request | normal fill | n/a | hatched track, no number | empty track (today's rule) |
| Headline number | shown | n/a | not shown, `–` | `–` |

Smoothing: the one-pole filter runs over M, R and E together (the dashed part continues the line), unknown stays a gap. Filter state is rebuilt from raw samples on every paint as today, so a revision costs nothing extra. The 20 s warm-up already prevents edge jumps.

How many parallel requests fit **[proposal]**: add one capacity strip under the live block from gauges the engine reports: running of slots (`requests_processing` / `total_slots`, or `vllm:num_requests_running`), waiting, and KV usage where present (`vllm:kv_cache_usage_perc`). No estimate: absent gauge, absent strip. A derived "per request" speed (total decode divided by running) is shown only with a "per request, derived" label.

**Rule change needed (owner decision):** CONTRIBUTING and AGENTS.md say never estimate. Dashed estimates break that wording even though they are labelled. The plan keeps estimates behind a setting, off by default for engines that have a live source, on for E-only engines. See open question 1.

### 3.5 Per engine **[probe/src/docs as in section 1]**

| Engine | Decode curve source | Prefill curve source | Settlement |
|---|---|---|---|
| Autobahn (custom) | L: `q27:generation_tokens_total` (equal to `vllm:generation_tokens_total` in the probe) | L: `q27:prefill_tokens_total` | none needed |
| vLLM, SGLang | L per iteration (to confirm in section 1) | L | none needed |
| llama.cpp | D: `/slots` `n_decoded` per slot, `SlotRateTracker` | L: `prompt_tokens_total` (per batch) or `/slots` `n_prompt_tokens_processed` | E: `tokens_predicted_total` delta checks the live estimate |
| Strata | `live.slots[].generated` per slot | `live.slots[].prompt_read` | `totals` (E) |
| Basalt | `/slots` `tok_s`, `prefill_done` | same | none |
| Ollama | none (A) | none | none; state only from `/api/ps` |
| LM Studio | none (A) | none | none; model state only |
| mlx-lm | none | none | none |
| mlx-vlm / vllm-mlx / oMLX | `in_flight` / running gauges live; speeds at request end | same | E: per-request records, averages |
| any engine behind llama-swap | n/a | n/a | E: `/api/metrics/activity` per-request records |

## 4. The definition object and configurator **[proposal]**

### 4.1 Reuse and libraries (mandatory)

Evidence: `research-C-libraries.md` (GitHub API and NuGet registry, 2026-11-14). Stratoscope rule: "use a maintained library before writing your own; no NuGet package in `Core` or `App`".

| Need | Candidates (license, last activity) | Choice | Reason |
|---|---|---|---|
| Prometheus text parser | .NET: prometheus-net 8.2.1 (MIT, 2024) and Prometheus.Client 6.3.0 (MIT, 2026-11) are exporters; Promitor.Parsers 0.2.0 (2021) and Vizhion.Prometh 1.2.1 (2022) stale. Swift: swift-prometheus 2.4.1 (Apache-2.0) is client only. Reference parsers: Go `prometheus/common/expfmt` v0.74.0, Python `prometheus_client` 0.26.0 (both Apache-2.0, 2026-11) | **No maintained parser exists in C# or Swift.** Extend the existing `PrometheusText` to the text format 0.0.4 spec (types, label selectors, histogram `_sum/_count`) | thin glue on the existing 34-line parser; conformance inputs from expfmt `testdata/` and client_python tests; Windows, macOS, Linux (no platform code) |
| JSONPath | .NET: JsonPath.Net 3.1.0 (MIT, 2026-11-13, RFC 9535 per README). Swift: mackoj/swift-jsonpath 0.1.0 (MIT, published the same day, claims RFC 9535 and 715 compliance cases, unverified), SwiftPath 1.0.0 (MIT, no `..`, no functions), Sextant 0.4.41 (MIT, Jayway dialect, Linux ticked). JSONPath Compliance Test Suite `cts.json` (BSD-2, 2026-10-13) | **Define the path language as a strict subset of RFC 9535** (name, index, `[*]`, one equality filter `[?@.k=='v']`), implemented over `System.Text.Json` and Foundation `JSONSerialization`; run the CTS cases inside the subset on both sides | no Swift library has a track record yet, and the .NET one would break the no-NuGet rule in `Core` for a need that today is dotted names and `[*]`. Upgrade to JsonPath.Net and a Swift library later if an engine needs `..` or functions. Owner decision, question 3 |
| Derived-value expressions | CEL: Cel.NET 2.3.1, cel-net 0.3.3 (C# only, no Swift port found). JsonLogic: JsonLogic 6.2.0 (MIT, 2026-11) plus json-logic-swift (MIT, last push 2024-05-29, aging). JMESPath: JmesPath.Net 1.1.0 and jmespath.swift 1.2.0 (Apache-2.0, both maintained, but a query language, not arithmetic). jq Swift wrapper (native C). NCalc, DynamicExpresso (C# only) | **Fixed operator vocabulary** in the definition (section 4.5): about 14 operators, no text expressions | no pair of ports is confirmed to behave identically; a closed vocabulary is verifiable with shared fixtures and costs about a page per platform |
| JSON parsing | `System.Text.Json` (already), Foundation `JSONSerialization` (already) | existing | already in both apps |
| Definition validation | JsonSchema.Net 9.7.0 (MIT, drafts 6+); swift-json-schema 0.15.0 needs Swift 6.1, above the repo's 5.9; JSONSchema.swift stale | **Validate in CI with one tool**; at runtime each app does a structural check that turns problems into warnings (unknown operator gives a null field and a log line) | avoids a schema library on the Swift side; a runtime failure never takes the monitor down |
| Same behaviour in C# and Swift | two interpreters plus shared fixtures; wasmtime-dotnet 50.0.1 (Apache-2.0 with LLVM exception, 2026-11-06) plus WasmKit 0.4.2 (MIT, 0.x); Rust or Go core with C ABI; Swift on Windows | **Two small interpreters plus shared conformance fixtures** | the repo already works this way (twin files, `Tests/Fixtures`); WASM adds a native runtime to a 0.3 MB framework-dependent install and a 0.x Swift runtime for about 600 lines of logic |
| Format | JSON, YAML, TOML | **JSON** | both platforms parse it with no dependency; YAML and TOML need packages. No comments: a `"note"` key carries them |
| Precedents copied | Datadog OpenMetrics `metrics: {name: {name, type}}` and `rename_labels`; Home Assistant REST `json_attributes_path` with JSONPath; Grafana Infinity "root selector plus columns" for arrays | shapes only, no code | what operators already know; the `slots` mapping is "root selector plus fields" |
| Router and host feeds | llama-swap `/api/metrics/activity`, `/api/events`, `/running`, `/v1/models` | existing endpoints | no relay or poller of our own (section 5) |

### 4.2 Schema and versioning

One file per engine or router: `<id>.engine.json`. Top level:

| Key | Meaning |
|---|---|
| `schema` | `"stratoscope.engine/1"`. Integer major. A file with a higher major than the app knows is skipped with a log line; an unknown key inside a known major is ignored (additive changes need no bump). Independent of the app version, which stays 0.3.4 |
| `id`, `brand`, `kind` | `engine` or `router`; `brand` is the panel name |
| `revision` | integer of the file itself, shown in the log, no semantic meaning |
| `extends` | id of another definition: shallow merge by key (autobahn extends vllm) |
| `detect` | fingerprint, 4.4 |
| `sources` | the GET endpoints, 4.3 |
| `fields` | display mapping, 4.5 |
| `slots`, `recent` | array mappings (per slot, finished requests) |
| `status` | ordered rules deriving idle, prefill, thinking, decode, loading |
| `curves` | which fields feed prefill and decode curves and their class (section 3) |
| `limits` | `maxRatePerSecond` and similar sanity bounds |
| `tests` | names of fixture files that must pass, so a definition carries its own evidence |

### 4.3 Sources

```json
"sources": {
  "props":   { "get": "/props",   "format": "json",       "every": "poll" },
  "slots":   { "get": "/slots",   "format": "json",       "every": "poll", "skipWhen": "props:$.is_sleeping == true", "wakes": "unknown" },
  "metrics": { "get": "/metrics", "format": "prometheus", "every": "counters", "needs": "--metrics" }
}
```

`every`: `poll` (2 s full read), `counters` (0.5 s light read, today's `ReadCountersAsync`), `once`, `on-detect`. `wakes`: `no`, `yes`, `unknown`; a source that may wake an engine is never asked while the router says the model is not `ready` (today's rule, now data). `heavy: true` marks `/ui/state`-sized payloads so remote views slow them down (5.1). `404` or `503` on an optional source gives null fields, not an error.

### 4.4 Detection

Evaluated on the union of probe responses, each URL asked at most once per address and cached (as `AutoEngineProvider` caches today); the probe list is the union of all `detect` GETs, about eight: `/health`, `/props`, `/metrics`, `/v1/models`, `/version`, `/api/version`, `/api/ps`, `/slots`.

```json
"detect": {
  "require": [ { "source": "/props", "path": "$.build_info", "exists": true } ],
  "any":     [ { "source": "/metrics", "metricPrefix": "llamacpp:" } ],
  "forbid":  [ { "source": "/health", "path": "$.service", "equals": "basalt" } ],
  "weight": 10
}
```

All `require` match, at least one `any` if present, no `forbid`: candidate. Highest `weight` wins, ties go to the more specific (longer `require`), nothing matches gives "unknown engine", read as a plain OpenAI server: state from `/v1/models`, no speeds. Indicative fingerprints (to be proved by fixtures, each from section 1): vLLM `vllm:` metrics prefix and `/version`; SGLang `sglang:` prefix; llama.cpp `/props.build_info`; Ollama `/api/version` plus `/api/ps` JSON with `models`; LM Studio `/api/v1/models` with `loaded_instances`; mlx-vlm `/health.loaded_model`; oMLX `/api/status`; Basalt `/health.service == "basalt"`; Strata `/metrics.engine`; Autobahn `q27:` prefix (overlay beats plain vLLM by weight). A user can pin an engine with `"engine": "<id>"` to skip detection.

### 4.5 Fields and derived values

A **reference** names a source and a path: `props:$.total_slots`, `metrics:vllm:num_requests_running`, `metrics:vllm:request_success_total{finished_reason="stop"}`. Prometheus selectors allow name plus `=` and `!=` label matchers only (no regex, so the C# and Swift regex engines never differ). A number sent as text is not a number; a missing key is null (repo rule, ARCHITECTURE "Missing values").

**Operators** (JSON objects, evaluated the same everywhere; any null input gives null, never 0):

| Op | Meaning |
|---|---|
| `ref` | a reference (shorthand: the string itself) |
| `sum`, `max`, `min`, `count` | over all series matching a selector, or over an array |
| `add`, `sub`, `mul`, `div` | double arithmetic; `div` by 0 gives null |
| `rate` | `(now - before) / dt` over a counter between the two newest reads at least `minSpan` apart; a falling counter gives a gap (restart) |
| `delta` | counter difference since the previous read |
| `slotRate` | per-slot rate over the 4 s window of today's `SlotRateTracker` |
| `histMean` | `_sum / _count` of a histogram (or its delta over the window) |
| `scale` | unit conversion (`ms`, `ns`, `s`, `percent`, `ratio`) |
| `coalesce` | first non-null (version aliases: `kv_cache_usage_perc`, `gpu_cache_usage_perc`) |
| `if`, `eq`, `gt` | conditions for status rules |

Numeric rules, the same in both: IEEE 754 double, rounding only at display (halves to even, as today), time from the local receive clock.

Targets (the display fields) are the rows of the table in 6.2, with the same names as `EngineSnapshot` (`live.decodeTokensPerSecond`, `slots[].state`, `context.max`, `totals.outputTokens`, `capacity.kvUsage`, ...). A field the definition does not mention is null and its part of the panel stays hidden, as today.

Example (llama.cpp, abridged **[proposal]**):

```json
{ "schema": "stratoscope.engine/1", "id": "llamacpp", "kind": "engine", "brand": "llama.cpp", "revision": 1,
  "fields": {
    "context.max": "props:$.default_generation_settings.n_ctx",
    "queue.waiting": "metrics:llamacpp:requests_deferred",
    "totals.promptComputed": "metrics:llamacpp:prompt_tokens_total",
    "totals.outputTokens":  "metrics:llamacpp:tokens_predicted_total"
  },
  "slots": { "each": "slots:$[*]", "fields": {
      "id": "$.id", "running": "$.is_processing",
      "generated": "$.next_token[0].n_decoded",
      "promptRead": { "op": "add", "a": "$.n_prompt_tokens_processed", "b": "$.n_prompt_tokens_cache" },
      "decodeRate": { "op": "slotRate", "of": "$.next_token[0].n_decoded", "when": "$.is_processing" } } },
  "status": [ { "if": "no slot running", "then": "idle" },
              { "if": "some slot running with generated == 0", "then": "prefill" },
              { "else": "decode" } ],
  "curves": {
    "prefill": { "class": "live",            "from": { "op": "rate", "of": "metrics:llamacpp:prompt_tokens_total" } },
    "decode":  { "class": "live",            "from": { "op": "sum", "of": "slots.decodeRate" } },
    "settle":  { "class": "end-of-request",  "tokens": "metrics:llamacpp:tokens_predicted_total",
                 "seconds": "metrics:llamacpp:tokens_predicted_seconds_total" } } }
```

(The prose rules of `status` are written as small condition objects in the real schema.) The Autobahn file is `{"extends":"vllm", "id":"autobahn", ...}` plus `curves.prefill` from `q27:prefill_tokens_total` and `detect` weight over vLLM.

### 4.6 Where definitions live and how a user adds one

| Place | Content | Wins over |
|---|---|---|
| Built in: shared folder `Sources/EngineDefinitions/*.engine.json`, embedded in the Windows build (EmbeddedResource) and in the Swift package (resources) | strata, basalt, llamacpp, autobahn, vllm, sglang, ollama, lmstudio, mlx-vlm, vllm-mlx, omlx, llama-swap | nothing |
| User folder: `%LOCALAPPDATA%\Stratoscope\engines\`, `~/Library/Application Support/Stratoscope/engines/`, `$XDG_CONFIG_HOME/stratoscope/engines/` | any file; same `id` replaces the built-in, `extends` makes a thin overlay | built in |
| Server entry in the settings file: `{ "name": "...", "url": "...", "engine": "<id>\|auto", "definition": "<path>" }` | pins one definition for one server | folder |

No network fetch of definitions (AGENTS.md "no network calls other than the configured engine"). Definitions ship with the app; a user file is the update path for a new engine version.

### 4.7 Fully custom path

Three levels, same schema, no second mechanism **[proposal]**:
1. **Overlay**: `extends` a built-in and change fields (a vLLM fork with renamed series).
2. **Standalone**: a file with its own sources and fields for an unknown engine (nothing inherited). The 4.5 vocabulary covers any Prometheus or JSON endpoint.
3. **Native hook** (code, per platform, rare): a definition may name `"enrich": ["strataCacheLog"]`, a built-in registered function for things that are not HTTP (the Strata cache log). New hooks need a code change; none is planned beyond the existing one.

What does not exist: a scripting language in definitions. Reason: it would make behaviour differ between C# and Swift and open a code-execution path from a settings folder.

### 4.8 One file, identical behaviour in C# and Swift

| Component (about 600 lines each) | Test asset shared by both |
|---|---|
| Prometheus text reader with types and selectors | expfmt `testdata` and client_python cases, converted once to fixtures |
| Path reader (RFC 9535 subset) | the subset of `cts.json` |
| Operator evaluator | new `ops/*.json` cases (input, expected) |
| Source scheduler, detector, field binder | `Tests/Fixtures/<engine>-*.json` with a new `<name>.snapshot.json` expected EngineSnapshot, the same recordings the C# tests already read; the Swift tests start reading them too (closes the "Swift does not read Strata fixtures" gap) |
| Sample store and curves | `SpeedHistory` cases with M/R/E sequences |

Rule: a definition may be merged only with fixtures in `tests` that both test suites pass. The existing adapters stay in place until each engine's definition reproduces its `*.expected.json` byte for byte; then the adapter is deleted.

### 4.9 Migration order

1. Interpreter plus llama.cpp definition (hardest case, fixtures exist) beside the old adapter, compared on the same recordings.
2. Autobahn, Basalt, Strata (after the same comparison); then delete the old adapters and DTOs.
3. New engines from definitions only: vLLM, SGLang, vllm-mlx, mlx-vlm, oMLX; then state-only: Ollama, LM Studio, mlx-lm.
4. Swift parity: Swift reads the same fixtures and gains the counters hook, the router layer with 429 handling and multiple servers (section 2.2).
Time, my estimate: each step about 1 to 2 agent-orchestrated sessions of a few hours (owner's "6-hour sessions" scale); the build waits for the owner's discussion.


## 5. Topologies

### 5.1 View on another machine (Tailscale, direct) **[proposal]**

| Point | Finding | Action |
|---|---|---|
| Reachability | `ServerDetection.IsLocalNetwork` accepts 100.64.0.0/10 (Tailscale) but rejects every host name, so MagicDNS names such as `birostris` are refused in detection. An explicitly configured URL is not checked. | accept names ending in the tailnet suffix, or any explicitly configured host; no tunnel code |
| Binding | the router and engine must listen on the tailnet address; Windows firewall must allow the port from the tailnet | document in the definition's `remote` notes, not in app code |
| Payload | custom engine `/metrics` is 46 KB, `/ui/state` is 563 KB per read **[probe]**; the engine ignores `Accept-Encoding: gzip` (identical byte counts, `BaseHTTP/0.6 Python`) **[probe]** | on a remote view, poll `/ui/state` less often (the definition marks it `heavy`); ask the engine side for gzip or a slim state endpoint |
| Time | engine clocks differ | use local receive time for samples; engine timestamps only as durations |
| Auth | tailnet ACLs are the gate | optional `headers` entry per server for engines with API keys, stored in the settings file, never logged |
| Wake safety | a remote probe wakes models exactly like a local one | same rule below |

### 5.2 Many views on one engine

Cost per view with the current design: one counter read every 0.5 s (46 KB, 60 to 90 ms server time on the custom engine, **[probe]** 61 ms for `/upstream/.../metrics`) plus one full poll every 2 s. That is about 92 KB/s and 12 to 18 percent of one core of a Python frontend per view. Five views would take most of a core on the thread that also serves tokens.

| Concern | Finding | Action **[proposal]** |
|---|---|---|
| Engine load | proportional to views | engine side: cache the rendered `/metrics` for 250 ms (owner owns Autobahn); view side: coalesce so one app instance shares a poll between its own readout and panel |
| llama.cpp scrape reset | `prompt_tokens_seconds` and `predicted_tokens_seconds` use one bucket reset on every scrape **[src v1.0.0]**: a second scraper (another view, Prometheus) corrupts both | never use the two gauges for curves (counters and `/slots` instead); the AGENTS.md headline rule (gauge as headline) must change for llama.cpp, see open question 2 |
| Router limit | llama-swap applies a per-model concurrency limit (429 once reached) and metrics GETs travel through the same proxy **[probe: Strata docs note limit 8; UNVERIFIED that a GET counts]** | measure with two views before deciding; fall back to the `proxy` address from `/running` on 429 |
| Shared relay | Prometheus server or a small fan-out proxy would serve many views with one scrape | **not worth it now**: engine-side TTL cache removes the load at no new component; revisit above about 4 views |

### 5.3 llama-swap in between

| Question | Answer | Evidence |
|---|---|---|
| Forwards `/upstream/<model>/metrics`, `/slots`, `/props`, `/health` | yes, any method and path, prefix stripped | **[src v272]** `internal/server/api.go handleUpstream`; **[probe v270]** `/upstream/<id>/metrics`, `/health`, `/v1/models`, `/ui/state` answered; `/slots` and `/props` reached the custom engine and got its own 404 |
| Wakes a model that is not loaded | **yes**, except paths matching `upstream.ignorePaths`; the default pattern covers static file types only, not `/metrics` or `/slots` | **[src v272]** `internal/config/upstream.go` |
| Own metrics | `/metrics` is Prometheus, **host only** (`llamaswap_cpu_util_percent`, memory, network, GPU util, VRAM, temperature, power, fan); no token or request metrics | **[src v272]** + **[probe v270]** same names |
| Own request feed | `/api/metrics/activity`: per finished request `timestamp`, `model`, `duration_ms`, `tokens{input_tokens, output_tokens, cache_tokens, reasoning_tokens, draft_tokens, draft_acc_tokens, prompt_per_second, tokens_per_second}`; `/api/metrics/stats`: totals and histograms | **[probe v270]** 25 records, **[src v272]** |
| Live request feed | `/api/events` SSE: `modelStatus`, `inflight` (id, model, path, `elapsed_ms`, bytes; throttled to 250 ms; no token counts), `activity`, `logData`, `uiConfig`, `pauseChanged`, `profileChanged` | **[src v272]**; **[probe v270]** types seen, busy `inflight` payload not seen |
| Model load state | `/running` (`stopped, starting, ready, stopping, shutdown`), and `/v1/models` `status.value` (`loaded`/`unloaded`) | **[src]**, **[probe]** |
| Host | `/api/hardware`, `/api/performance` (GPU time series) | **[probe]** |
| Usual practice | llama-swap UI and SSE for state; scrape the backend through `/upstream/<model>/metrics`; host Prometheus from `/metrics`. (From source reading, not a survey.) | research-B |

Consequences **[proposal]**: (1) the router becomes its own definition (`kind: router`) with `wakes: true` for `/upstream`, so the generic engine asks `/running` first and only reads an upstream that is `ready`, as today. (2) `/api/events` `inflight` gives a request count and per-request elapsed time for every engine behind it, including engines with no gauges: a "parallel requests" fallback. (3) `/api/metrics/activity` is the settlement source of 3.2 E. (4) The per-model 429 risk and `ignorePaths` advice go into the llama-swap definition notes.

### 5.4 The custom router

`custom-router.json` (ops repo) is not a router: it is the profile list of the custom backend (`qwen-27b-custom` on 18089, `qwen-27b-swift` on 18091, 4 parallel, 196,608 context, rc `serving-rc4-20261114-0745`). The router is the same llama-swap (v270 live **[probe]**) launching `custom-backend.ps1`; ops docs confirm "existing exclusive llama-swap group" and that Stratoscope "reads `/upstream/<id>/health` and `/metrics`". Verified by probe: `/running` lists `proxy: http://127.0.0.1:18091`, state `ready`, and `/upstream/<id>/{health,metrics,v1/models,ui/state}` all answer; `/upstream/<id>/{slots,props}` give the engine's JSON 404. So for the custom engine the definition needs no router-specific part.

## 6. New-engine checklist for coding agents

### 6.1 Steps

1. **Read before probing.** Read section 1 for the closest engine and the definition of that engine. Search the engine's source and docs for its monitoring endpoints; name file, tag and line for every metric you plan to use (AGENTS.md: name the source of every displayed value).
2. **Probe, read-only.** GET only, never POST, never an endpoint that loads or generates (`/health_generate`, llama-swap `/upstream/<model>` of a model that is not `ready`). Order: `/health`, `/v1/models`, `/version` or `/api/version`, `/metrics`, `/slots`, `/props`, the engine's own state endpoint. Save every response byte for byte as `Tests/Fixtures/<engine>-<version>-<state>.<ext>`, three states at least: idle, prefill, decode (and one parallel state if the engine has batching). Record payload size and server time per GET.
3. **Classify every field** as L, E, G, W or A with evidence: capture the same counter twice during a long request (live counters move, end-of-request ones do not) and once after it ends. Write the class and the two numbers into the fixture README.
4. **Fill in the definition**: `detect`, `sources` (mark `every`, `wakes`, `heavy`), `fields` using the table 6.2 names, `slots`, `status`, `curves`. Prefer an `extends` of the closest engine. A field with no source stays out.
5. **Test with recorded fixtures**: a `<name>.snapshot.json` for each state, both platforms' suites pass; one test that a missing key gives null; one that a restart (falling counter) gives a gap; one that the detector picks this engine and not its neighbours.
6. **Check the safety facts**: does any `sources` GET wake the engine? Does a second scraper change the values (llama.cpp resets two gauges per scrape)? Does the router count the GET against its concurrency limit?
7. **Document** the engine row in README "Supported inference engines" with the version checked and what was verified live versus only on recordings (CONTRIBUTING rule).
8. Run the focused tests only; the full gate once, at the end.

### 6.2 Every possible input parameter

Classes: L live, E end-of-request, G gauge, W windowed gauge, A absent, `?` not verified. Engines: **vLLM**, **SG** SGLang, **llc** llama.cpp, **Oll** Ollama, **LMS** LM Studio, **MLX** (mlx-vlm / vllm-mlx / oMLX), **Str** Strata, **Bas** Basalt, **Auto** Autobahn. "Shown" = in the app today; "new" = proposal.

| # | Field | Meaning, unit | Class by engine | Display target |
|---|---|---|---|---|
| 1 | engine state | ready, loading, asleep, error | vLLM G `/health`; SG G; llc G `/health` 503, `/props.is_sleeping`; Oll G `/api/ps`; LMS G `loaded_instances`; MLX G `/health`; Str G; Bas G `/health.loaded`; Auto G `/health` | status dot, state text (shown) |
| 2 | model name, alias | text | all G (`/v1/models`, `/props`, `/api/ps`) | title (shown) |
| 3 | engine version | text | vLLM G `/version`; SG G `/get_server_info`; llc G `build_info`; Oll G `/api/version`; Str, Bas G `engine.version` | info line (shown) |
| 4 | running requests | count | vLLM G; SG G `num_running_reqs`; llc G `requests_processing`; MLX G `in_flight`/`num_running`/`active_requests`; Str G `live.running`; Bas G slots; Auto G; Oll A; LMS A; router `inflight` L | bars, capacity strip (bars shown) |
| 5 | waiting requests | count | vLLM G; SG G `num_queue_reqs`; llc G `requests_deferred`; MLX G; Str G `live.queued`; Bas G `live.queue`; Auto G; Oll A (503 past `OLLAMA_MAX_QUEUE`) | queued (shown), capacity strip (new) |
| 6 | slot count, max parallel | count | llc G `total_slots`; Str, Bas G `batch_slots`/`--parallel`; SG G `max_running_requests` via `/v1/loads`; Oll config only | capacity strip |
| 7 | per-slot state | idle, reading, decoding | llc G `/slots`; Str L `live.slots`; Bas L `/slots.state`; others A | bars (shown) |
| 8 | prompt read and total | tokens | llc L `/slots`; Str L; Bas L; LMS L (requester only); vLLM, SG, Oll A | prefill bar (shown) |
| 9 | tokens generated | count | llc L `n_decoded`; Str L; Bas L; vLLM L (counter) | row, speed |
| 10 | decode speed, total | tok/s | vLLM L (rate of `generation_tokens_total`); SG L (`gen_throughput`, `realtime_tokens_total`); llc **L only via `/slots`**, E via counters, W gauge; Str L `live.tok_s`; Bas L; Auto L; MLX E averages | headline, curve (shown) |
| 11 | prefill speed, total | tok/s | vLLM bursty (E at first output); SG L `realtime_tokens_total{prefill_compute}`; llc L `prompt_tokens_total`; Str L; Bas L; Auto L `q27:prefill_tokens_total` | curve, reading speed (shown) |
| 12 | generated tokens counter | cumulative | vLLM L; SG E; llc E; Auto L; Str E (`totals`) | `EngineCounters` (shown) |
| 13 | prompt tokens computed counter | cumulative | llc L; SG L (`realtime_tokens_total`); Auto L; vLLM E-like (burst); Str A | `EngineCounters` (shown) |
| 14 | prompt tokens total incl. cached | cumulative | vLLM E-like; SG E; llc L (`prompt_tokens_total` plus `_cached_total`); Auto E; Oll E | totals (shown) |
| 15 | cached prompt tokens | cumulative or per request | vLLM `prompt_tokens_cached_total`; SG `cached_tokens_total` E; llc `prompt_tokens_cached_total` L; Oll `prompt_eval_cached_count` E; OpenAI `cached_tokens` E | cache dots, hit rate (shown, per request) |
| 16 | context used, max | tokens | llc G `/slots` + `n_ctx`; Str, Bas G `engine.max_context`; SG G `max_total_num_tokens`; Oll G `context_length`; LMS G `loaded_instances[].config.context_length`; MLX G | context bar (shown) |
| 17 | finished requests | list: tokens, speed, duration | Str G `requests[]`; Bas G `requests[]`; mlx-vlm G `recent`; **router E `/api/metrics/activity`**; vLLM, SG, llc A (histograms only) | request bars (shown for Str, Bas) |
| 18 | conversation / prefix cache stats | slots, bytes, evictions | Str G; Bas G; vLLM prefix hits and queries L; SG `cache_hit_rate` G; mlx-vlm G | cache section (shown for Str) |
| 19 | KV cache usage | fraction 0 to 1 | vLLM G `kv_cache_usage_perc`; SG G `token_usage`; Auto G; llc A (v1.0.0); others A | **new** capacity strip |
| 20 | prefix cache hit rate | fraction | vLLM from `prefix_cache_hits/queries` rate; SG G; vllm-mlx G `cache_hit_rate`; oMLX `cache_efficiency` | **new** |
| 21 | speculative decoding acceptance | accepted / drafted | vLLM, Auto counters L; SG G `spec_accept_rate`; llc E counters; Bas L `drafts_*`; Str `totals.drafts_*` E | **new** (Auto, Bas, llc have it) |
| 22 | time to first token | seconds | vLLM L histogram; SG L histogram; Bas `ttft_s` per slot; LMS E per request; MLX E; llama-swap activity: not present | **new** per request row |
| 23 | inter-token latency | seconds | vLLM L; SG L; others A | **new** (optional) |
| 24 | queue time | seconds | vLLM E histogram; SG per request | **new** (optional) |
| 25 | preemptions | count | vLLM L; SG retracted gauges; Auto `num_preemptions_total` | **new** warning mark |
| 26 | finish reasons | stop, length, abort | vLLM `request_success_total{finished_reason}`; Auto same; Str, Bas `finish` | request row (shown) |
| 27 | reasoning tokens | count | OpenAI `reasoning_tokens` E; llama-swap activity E; Str `thinking` phase | thinking state (shown) |
| 28 | model load state and progress | unloaded, loading, loaded, sleeping | llc router `/models.status`; llama-swap `/running`, `/v1/models.status`, `/api/events modelStatus`; vllm `engine_sleep_state`; LMS `model_load.progress` (requester only); Oll `/api/ps` | loading text (shown) |
| 29 | GPU, RAM, CPU | percent, bytes, watts, temperature | Str G `hardware`; Bas G; Auto `/ui/state`; llama-swap G `/metrics` (`llamaswap_gpu_*`), `/api/hardware`; MLX vllm-mlx `metal_memory_bytes` | system section (shown for Str, Bas, Auto) |
| 30 | router activity per request | tokens, cache, speeds, duration | llama-swap E `/api/metrics/activity` (25 records in the probe) | **new** settlement source |
| 31 | router in-flight requests | id, model, elapsed, bytes | llama-swap L `/api/events inflight` (250 ms; busy payload not yet seen) | **new** fallback for field 4 |

### 6.3 Missing parameters worth showing **[proposal]**

| Rank | Parameter | Why | Who offers it | Cost |
|---|---|---|---|---|
| 1 | KV cache usage | answers "how many more parallel requests fit" directly | vLLM, SGLang, Autobahn (31 vLLM families in the probe) | one gauge read |
| 2 | Queue depth with capacity (running of slots, waiting) | the same question from the other side; Autobahn queues when the KV pool is full (ops rollout note) | vLLM, SGLang, llama.cpp, Strata, Basalt, Autobahn, MLX servers | one gauge read |
| 3 | Prefix-cache hit rate | explains slow prefills on agents with long contexts (a 132K-token Kilo prompt at 99 % cache in the probe) | vLLM, SGLang, Autobahn, Strata | a ratio of two counter rates |
| 4 | Speculative-decoding acceptance | explains decode speed swings (Autobahn DFlash) | Autobahn, vLLM, Basalt, llama.cpp (E) | ratio of two counters |
| 5 | TTFT (and inter-token latency) | the number users feel; TTFT is also per request | vLLM, SGLang, Autobahn, Basalt, router activity (no) | histogram mean over the window |
| 6 | Preemptions | warns that the engine is thrashing | vLLM, Autobahn, SGLang | counter delta |
| 7 | Model load state and progress | no more "server not responding" while a model loads | llama-swap, llama.cpp router, LM Studio | already partly in `RoutedProvider` |
| 8 | Router activity feed | speed for engines with nothing live (Ollama, LM Studio, mlx-lm behind llama-swap) | llama-swap | one GET per few seconds |


## Evidence and files

| What | Where |
|---|---|
| Standards, vLLM, SGLang, OTel, OpenAI (line-level) | `side-session-notes/local_f01be397-.../research-A-vllm-sglang-otel-openai.md` |
| llama.cpp, llama-swap, Ollama, LM Studio, MLX (line-level) | `.../research-B-llamacpp-swap-ollama-lmstudio-mlx.md` |
| Libraries | `.../research-C-libraries.md` |
| Live captures (router `/running`, `/v1/models`, `/metrics`, `/api/metrics/*`, custom engine `/metrics`, `/health`, `/ui/state`) | `.../probes/` |

Limits of this plan: only the Autobahn engine behind llama-swap was probed live; every other engine row rests on source at a tag, docs, or the repository's own recordings (Strata 0.1.56, Basalt 0.3.2, llama.cpp `b1-d493f84`). A definition for an engine I could not probe needs recorded fixtures from a real install before it ships (checklist step 2). llama.cpp v1.0.0 and llama-swap v272 were read from source; the recorded llama.cpp fixture is an older build, so its live behaviour on v1.0.0 is not probed.

## Open questions for the owner

| # | Question | My recommendation |
|---|---|---|
| 1 | Allow dashed **estimated** segments (section 3.4)? It loosens the "never estimate" rule in AGENTS.md and CONTRIBUTING. | Yes for engines with no live source, labelled, never in numbers, averages or the scale; setting off by default where a live source exists |
| 2 | llama.cpp headline rule says "use `predicted_tokens_seconds`", but that gauge is end-of-request and reset by every scrape (two views corrupt each other). Change the headline to the `/slots` derived speed? | Yes: derived speed from `/slots` as headline, gauge unused |
| 3 | JSONPath: strict subset of RFC 9535 in our own tiny evaluator (no new package), or JsonPath.Net plus a Swift library now? | Subset first; adopt libraries when an engine needs `..` or functions. Swift candidates are too new (0.1.0 published the day of this check) |
| 4 | Shared definitions folder `Sources/EngineDefinitions/` at the repo level (AGENTS.md says nothing new in the root for Windows-specific files; this is shared) | Yes |
| 5 | Ship definitions for engines I could only read from source (vLLM, SGLang, mlx-*, Ollama, LM Studio) before someone records fixtures from a real install? | Ship as "recorded from docs" with a visible marker; promote when a real fixture exists |
| 6 | Router metric GETs may count against llama-swap's per-model request limit (limit 8 on Birostris). Measure with two views first, then fall back to the `proxy` address on 429 (the Mac already does)? | Measure first |
| 7 | Ops-side changes, yours to approve: add `/metrics`, `/slots`, `/props`, `/health` to llama-swap `upstream.ignorePaths` so polling never loads a model; Autobahn: 250 ms cache on `/metrics`, gzip, slim state endpoint | Yes, small |
| 8 | Accept Tailscale MagicDNS names in server detection (today only IP ranges, including 100.64/10)? | Yes, suffix-matched |
| 9 | Linux: a front end is outside this plan; keep the interpreter Linux-clean. Is a Linux build wanted soon? | Later |
| 10 | The Swift side is "mirrored, unbuilt". Build and test it as step 4 of the migration (needs a Mac or a Linux CI job), or keep it unbuilt until the definitions land? | Build it with the first definition, since fixtures make both sides comparable |
