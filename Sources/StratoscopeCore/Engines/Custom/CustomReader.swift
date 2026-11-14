import Foundation

// SPEED RULE (name the source of every value; never estimate).
// When several requests run together, the headline decode speed (and the headline prefill speed while reading) is the engine's
// own total over all requests. Each engine reports that total itself:
//  - Strata: live.tok_s, the total of the generating requests (serve/server.py _tok_s). The prefill headline is
//    live.prefill_tok_s_mean, the engine's mean. The Windows Strata adapter also has a fallback: when the engine has no total yet,
//    it sums derived per-slot speeds; the macOS reader does not have that fallback.
//  - Basalt: live.tok_s, which the engine sums over its slots.
//  - llama.cpp: /metrics llamacpp:predicted_tokens_seconds, the server-wide decode rate over all slots (prompt_tokens_seconds for
//    prefill). It needs the server's --metrics flag. No sum is used.
//  - custom: /ui/state tick.sample.decode_tps and tick.sample.prefill_tps, the engine's counter deltas over all requests between
//    samples. The per-request sum is used only when the sample has no rate.
// A request's own speed is shown on its own row only. Never add up the per-request rows to make the headline unless the engine
// has no total.
// This file: the custom source is /ui/state tick.sample.decode_tps and tick.sample.prefill_tps.

/// The numbers of the custom 27B server's `GET /metrics` (Prometheus text, series prefix `q27:`). nil means the series was not in the answer.
/// The scheduler gauges are read at the time of the request; the totals count since the engine started.
struct CustomMetrics {
    static let prefix = "q27:"

    var running: Double?
    var waiting: Double?
    /// The most sequences the engine was built to run together (not the configured target, `target_seqs`).
    var engineSeqs: Double?
    var kvFreePages: Double?
    var kvCheckpointOnlyPages: Double?
    var prefixCacheCheckpoints: Double?
    var prefillTokensTotal: Double?
    var promptTokensTotal: Double?
    var cachedTokensTotal: Double?
    var generationTokensTotal: Double?
    var requestsTotal: Double?
    /// 1 while the scheduler holds the first waiting request back for lack of KV pages (`admission_blocked{reason="kv"}`).
    var blockedKv: Double?
    /// 1 while it holds it back for the minimum per-sequence speed (`admission_blocked{reason="speed"}`).
    var blockedSpeed: Double?

    /// nil when the text has no `q27:` series at all: that is no custom server.
    static func parse(_ text: String?) -> CustomMetrics? {
        let v = PrometheusText.parse(text, prefix: prefix)
        if v.isEmpty { return nil }
        return CustomMetrics(
            running: v["num_requests_running"], waiting: v["num_requests_waiting"], engineSeqs: v["engine_seqs"],
            kvFreePages: v["kv_free_pages"], kvCheckpointOnlyPages: v["kv_checkpoint_only_pages"],
            prefixCacheCheckpoints: v["prefix_cache_checkpoints"], prefillTokensTotal: v["prefill_tokens_total"],
            promptTokensTotal: v["prompt_tokens_total"], cachedTokensTotal: v["cached_tokens_total"],
            generationTokensTotal: v["generation_tokens_total"], requestsTotal: v["requests_total"],
            blockedKv: v["admission_blocked{reason=\"kv\"}"], blockedSpeed: v["admission_blocked{reason=\"speed\"}"])
    }
}

/// One request of `GET /ui/state` `seqs` (worker.py `Scheduler.stats()`, stats.py `tick()`, server.py `GET /ui/state`). nil means the field was
/// missing or of another type.
struct CustomSeq {
    var rid: String?
    /// waiting, running, parked or admitted.
    var state: String?
    /// The engine's own phase: prefill or decode.
    var phase: String?
    var prompt: Double?
    /// Prompt tokens the engine has run (capped at the prompt).
    var fed: Double?
    var out: Double?
    var maxNew: Double?
    /// Prompt tokens actually prefilled, not restored from the cache.
    var computed: Double?
    var prefillSeconds: Double?
    var decodeSeconds: Double?
    /// Prompt tokens restored from the cache, and those that could have been (`cached`, `reusable`).
    var cached: Double?
    var reusable: Double?
    /// Seconds to the first token, and how often the request was pushed out by the scheduler (`ttft`, `preempted`).
    var ttft: Double?
    var preempted: Double?

    func isState(_ word: String) -> Bool { state?.caseInsensitiveCompare(word) == .orderedSame }
    func isPhase(_ word: String) -> Bool { phase?.caseInsensitiveCompare(word) == .orderedSame }

    /// The `seqs` of a `/ui/state` document (`tick.seqs`; a top-level `seqs` is accepted too), or nil when the text is not a JSON object with a list.
    static func parse(_ text: String?) -> [CustomSeq]? {
        guard let text = text, let obj = (try? JSONSerialization.jsonObject(with: Data(text.utf8))) as? [String: Any] else { return nil }
        return parse(object: obj)
    }

    static func parse(object obj: [String: Any]) -> [CustomSeq]? {
        let tick = obj["tick"] as? [String: Any]
        guard let list = (tick?["seqs"] as? [Any]) ?? (obj["seqs"] as? [Any]) else { return nil }
        return list.compactMap { ($0 as? [String: Any]).map { d in
            CustomSeq(rid: d["rid"] as? String, state: d["state"] as? String, phase: d["phase"] as? String,
                      prompt: customNumber(d, "prompt"), fed: customNumber(d, "fed"), out: customNumber(d, "out"), maxNew: customNumber(d, "max_new"),
                      computed: customNumber(d, "computed"), prefillSeconds: customNumber(d, "prefill_s"), decodeSeconds: customNumber(d, "decode_s"),
                      cached: customNumber(d, "cached"), reusable: customNumber(d, "reusable"), ttft: customNumber(d, "ttft"),
                      preempted: customNumber(d, "preempted"))
        } }
    }
}

/// A finite number of a JSON object; nil when missing, null, boolean or of another type.
private func customNumber(_ d: [String: Any]?, _ key: String) -> Double? {
    guard let n = d?[key] as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite else { return nil }
    return n.doubleValue
}

/// A named byte count of `tick.vram.parts`, with its sub-parts.
struct CustomVramPart {
    var name: String
    var bytes: Double?
    var parts: [CustomVramPart]
}

/// The parts of `GET /ui/state` that are not per request (worker.py `Scheduler.stats()`, stats.py `tick()`; the shape is the live capture
/// Tests/Fixtures/custom/custom-ui-state-idle.json). nil in a field means the key was missing or of another type.
struct CustomUiState {
    // tick.sample: the latest NVML sample.
    var util: Double?, memUtil: Double?, power: Double?, powerLimit: Double?, smClock: Double?, memClock: Double?, temp: Double?
    var memUsedBytes: Double?, memTotalBytes: Double?
    // tick.sample: the engine's own total token rates over all requests (as Strata's live.tok_s)
    var decodeTps: Double?, prefillTps: Double?
    // tick.vram
    var vramTotal: Double?, vramBudget: Double?, vramFreeAtStart: Double?
    var vramParts: [CustomVramPart] = []
    // tick.cache
    var poolPages: Double?, freePages: Double?, parkPages: Double?, checkpointOnlyPages: Double?, checkpoints: Double?
    var hits: Double?, lookups: Double?, cachedTokens: Double?, evicted: Double?, hasCache = false
    // tick.sched
    var running: Double?, waiting: Double?, blocked: String?, targetSeqs: Double?, hasSched = false
    // tick.engine
    var engineState: String?
    // info
    var maxTokens: Double?, maxSeqs: Double?, spec: Bool?, kvDtype: String?, gpu: String?, hasInfo = false
    // tick.seqs
    var seqs: [CustomSeq]?
    // log events of kind "done", newest first
    var done: [RequestStats] = []

    func part(_ name: String) -> CustomVramPart? { vramParts.first { $0.name == name } }
    var hasSample: Bool { util != nil || temp != nil || power != nil || memTotalBytes != nil }

    /// nil when the text is not a JSON object.
    static func parse(_ text: String?) -> CustomUiState? {
        guard let text = text, let obj = (try? JSONSerialization.jsonObject(with: Data(text.utf8))) as? [String: Any] else { return nil }
        var s = CustomUiState()
        let tick = obj["tick"] as? [String: Any]
        if let d = tick?["sample"] as? [String: Any] {
            s.util = customNumber(d, "util"); s.memUtil = customNumber(d, "mem_util"); s.power = customNumber(d, "power")
            s.powerLimit = customNumber(d, "power_limit"); s.smClock = customNumber(d, "sm_clock"); s.memClock = customNumber(d, "mem_clock")
            s.temp = customNumber(d, "temp"); s.memUsedBytes = customNumber(d, "used"); s.memTotalBytes = customNumber(d, "total")
            s.decodeTps = customNumber(d, "decode_tps"); s.prefillTps = customNumber(d, "prefill_tps")
        }
        if let d = tick?["vram"] as? [String: Any] {
            s.vramTotal = customNumber(d, "total"); s.vramBudget = customNumber(d, "budget"); s.vramFreeAtStart = customNumber(d, "free_at_start")
            func parts(_ any: Any?) -> [CustomVramPart] {
                (any as? [Any] ?? []).compactMap { ($0 as? [String: Any]).flatMap { p in
                    (p["name"] as? String).map { CustomVramPart(name: $0, bytes: customNumber(p, "bytes"), parts: parts(p["parts"])) } } }
            }
            s.vramParts = parts(d["parts"])
        }
        if let d = tick?["cache"] as? [String: Any] {
            s.hasCache = true
            s.poolPages = customNumber(d, "pool_pages"); s.freePages = customNumber(d, "free_pages"); s.parkPages = customNumber(d, "park_pages")
            s.checkpointOnlyPages = customNumber(d, "checkpoint_only_pages"); s.checkpoints = customNumber(d, "checkpoints")
            s.hits = customNumber(d, "hits"); s.lookups = customNumber(d, "lookups"); s.cachedTokens = customNumber(d, "cached_tokens")
            s.evicted = customNumber(d, "evicted")
        }
        if let d = tick?["sched"] as? [String: Any] {
            s.hasSched = true
            s.running = customNumber(d, "running"); s.waiting = customNumber(d, "waiting"); s.targetSeqs = customNumber(d, "target_seqs")
            s.blocked = d["blocked"] as? String
        }
        s.engineState = tick?["engine"] as? String
        if let d = (obj["info"] as? [String: Any]) ?? (tick?["info"] as? [String: Any]) {
            s.hasInfo = true
            s.maxTokens = customNumber(d, "max_tokens"); s.maxSeqs = customNumber(d, "max_seqs"); s.kvDtype = d["kv_dtype"] as? String
            s.gpu = d["gpu"] as? String
            if let b = d["spec"] as? NSNumber, CFGetTypeID(b) == CFBooleanGetTypeID() { s.spec = b.boolValue }
        }
        s.seqs = CustomSeq.parse(object: obj)
        // The engine's log keeps the last events oldest first; the "done" ones are the finished requests. Newest 20.
        let events = (obj["log"] as? [Any] ?? []).compactMap { $0 as? [String: Any] }.filter { $0["kind"] as? String == "done" }
        s.done = events.reversed().prefix(20).compactMap { e in
            guard let rec = e["rec"] as? [String: Any] else { return nil }
            return RequestStats(promptTokens: customNumber(rec, "prompt_tokens") ?? 0, reusedTokens: customNumber(rec, "cached_tokens") ?? 0,
                                promptRead: customNumber(rec, "computed_tokens") ?? 0,
                                promptMilliseconds: (customNumber(rec, "prefill_s") ?? 0) * 1000,
                                outputTokens: customNumber(rec, "completion_tokens") ?? 0, decodeTokensPerSecond: customNumber(rec, "decode_tps"),
                                durationSeconds: customNumber(rec, "total") ?? 0,
                                finishedAt: customNumber(e, "t").map { Date(timeIntervalSince1970: $0) }, finish: rec["reason"] as? String)
        }
        return s
    }
}

/// Reads the custom 27B server (its metrics use the q27: prefix) behind a llama-swap router or directly.
///
/// Data sources, all GET on endpoints the server answers without waking it:
///  - `/health`: `{"status":"ok"}` when the engine is ready; another status (`unloaded`, `loading`) or HTTP 503 means no request can run
///    yet and `/metrics` is not asked. It names no engine, so it is recognised by its metrics.
///  - `/metrics` (Prometheus text, prefix `q27:`): running and waiting requests, the engine's sequence capacity, free KV pages,
///    prefix cache checkpoints, which admission block holds a request back, and the token totals.
///
///  - `/ui/state` (JSON, about 150 KB, asked only while `/metrics` says requests run): `seqs` with each request's state, its own phase
///    (prefill or decode), prompt, fed, out, max_new, computed, prefill_s and decode_s. If it is missing, refused or not JSON, the phase is `.running` only.
///
/// Not reported, so not drawn: finished requests, context size, RAM, video card. The metrics alone are totals across all requests:
/// without `/ui/state` neither reading nor writing is shown, and no speed.
public final class CustomReader {
    /// The value of `engine:` in the config file.
    public static let id = "custom"
    public static let brand = "Custom"

    private let http = JSONFetcher()

    public init() {}

    /// A text GET answered a document with `q27:` series: this is a custom server.
    static func recognises(_ metricsText: String?) -> Bool { CustomMetrics.parse(metricsText) != nil }

    /// The state text of a `/health` status other than ok, or nil when the engine is ready.
    static func notReady(_ status: String?) -> String? {
        if let s = status, s.caseInsensitiveCompare("ok") == .orderedSame { return nil }
        return status.map { "State: " + $0 } ?? "State not reported"
    }

    private enum Health { case ready, notReady(String), unknown, named }

    /// `/health` of a server that names no engine. A `service` (Basalt, Strata) means it is not the custom server; 503 is "not ready".
    private static func health(status: Int?, body: String?) -> Health {
        if status == 503 { return .notReady("Engine not ready (HTTP 503)") }
        guard let status = status, (200..<300).contains(status), let body = body,
              let obj = (try? JSONSerialization.jsonObject(with: Data(body.utf8))) as? [String: Any] else { return .unknown }
        if let service = obj["service"] as? String, !service.trimmingCharacters(in: .whitespaces).isEmpty { return .named }
        if let reason = notReady(obj["status"] as? String) { return .notReady(reason) }
        return .ready
    }

    /// Asks `<base>/health`, then `<base>/metrics`. Calls `completion` with nil when this is no custom server (the caller keeps its own
    /// behaviour); with a loading placeholder when the engine is not ready. Router: `base` is `<router>/upstream/<model>`.
    public func read(base: String, model: String, compactAt: Double?, links: [EngineLink],
                     _ completion: @escaping (EngineSnapshot?) -> Void) {
        http.getText(base + "/health") { [self] status, body in
            switch Self.health(status: status, body: body) {
            case .named: completion(nil); return
            case .notReady(let reason):
                completion(EngineSnapshot(engine: Self.brand, model: model, status: .loading(reason), context: ContextUsage(max: 0), links: links))
                return
            case .ready, .unknown: break
            }
            http.getText(base + "/metrics") { [self] code, text in
                guard let code = code, (200..<300).contains(code), let m = CustomMetrics.parse(text) else { completion(nil); return }
                // Asked on every poll, idle too: the video card, memory parts, cache counters, engine settings and finished requests are only
                // there. A missing, refused or non-JSON answer leaves ui nil: the aggregate state stays and those parts are not drawn.
                http.getText(base + "/ui/state") { [self] stateCode, stateText in
                    let ui = stateCode.flatMap { (200..<300).contains($0) ? CustomUiState.parse(stateText) : nil }
                    completion(translate(m, model: model, compactAt: compactAt, links: links, ui: ui))
                }
            }
        }
    }

    /// Translates the metrics into a snapshot: running requests give `.running`, none give `.idle`. With `seqs` (`/ui/state` `tick.seqs`)
    /// the phase of each running request is the engine's own: phase prefill is reading, decode is writing. With `ui` (the whole `/ui/state`,
    /// asked on every poll) the card, memory, KV cache, engine settings, slots, context and finished requests are filled too; `seqs` wins
    /// over `ui.seqs` when both are given. Internal so tests can feed payloads.
    func translate(_ m: CustomMetrics, model: String, compactAt: Double? = nil,
                   links: [EngineLink] = [], seqs givenSeqs: [CustomSeq]? = nil, ui: CustomUiState? = nil) -> EngineSnapshot {
        let seqs = givenSeqs ?? ui?.seqs
        let running = m.running ?? 0
        let busy = running > 0
        var status: EngineStatus = busy ? .running : .idle

        // Without /ui/state only the number of requests running together is known; every other counter stays nil (unknown).
        var live: LiveRequest? = busy ? LiveRequest(running: running > 1 ? Int(running) : nil) : nil
        var waitingFromSeqs: Int?

        if busy, let seqs = seqs {
            // waiting: queued, not drawn as busy. running, parked and admitted: counted as running. The phase is the engine's own word for each
            // of them: a parked request that is decoding is still decoding (the scheduler parks it between its turns), so it counts as writing.
            let active = seqs.filter { $0.isState("running") || $0.isState("parked") || $0.isState("admitted") }
            let readers = active.filter { $0.isPhase("prefill") }
            let writers = active.filter { $0.isPhase("decode") }
            if !active.isEmpty {
                waitingFromSeqs = seqs.filter { $0.isState("waiting") }.count
                status = !writers.isEmpty ? .decode : !readers.isEmpty ? .prefill : .running
                let together = active.count > 1
                // Speed only from exposed values and only for a time above zero; several together: the sum over those in the phase.
                func speed(_ tokens: Double?, _ seconds: Double?) -> Double? {
                    guard let t = tokens, let s = seconds, s > 0 else { return nil }
                    return t / s
                }
                func sum(_ values: [Double?]) -> Double? {
                    values.isEmpty || values.contains(where: { $0 == nil }) ? nil : values.compactMap { $0 }.reduce(0, +)
                }
                live = LiveRequest(
                    promptRead: status == .prefill ? sum(readers.map { $0.fed }) : nil,
                    promptTotal: status == .prefill && !together ? active[0].prompt : nil,
                    generated: status == .decode ? sum(writers.map { $0.out }) : nil,
                    maxTokens: status == .decode && !together ? active[0].maxNew : nil,
                    // Speed rule at the top of this file: the engine's own sample (tick.sample.*_tps) is the total over all requests;
                    // the sum of the per-request speeds is used only when the sample has no rate.
                    prefillTokensPerSecond: status == .prefill ? (ui?.prefillTps ?? sum(readers.map { speed($0.computed, $0.prefillSeconds) })) : nil,
                    decodeTokensPerSecond: ui?.decodeTps ?? sum(writers.map { speed($0.out, $0.decodeSeconds) }),
                    reusedEstimate: sum(active.map { $0.cached }),      // the engine's own count of prompt tokens restored from the cache
                    running: together ? active.count : nil)
            }
        }

        // Counters since the engine started. The Mac model's totals are not optional, so they are given only when the engine
        // reports all four (an unknown one would show as 0). Prompt tokens are all prompts admitted, reused ones included.
        var totals: Totals?
        if let requests = m.requestsTotal, let prompt = m.promptTokensTotal, let cached = m.cachedTokensTotal, let output = m.generationTokensTotal {
            // q27:prefill_tokens_total is added per prefill chunk, so it advances while a request reads; prompt_tokens_total is added once per request.
            totals = Totals(requests: requests, outputTokens: output, promptTokens: prompt, reusedTokens: cached, prefillComputedTokens: m.prefillTokensTotal)
        }

        let blocked: String? = m.blockedKv == 1 ? "kv" : m.blockedSpeed == 1 ? "speed" : (m.blockedKv == 0 && m.blockedSpeed == 0) ? "none" : nil
        // Metrics first (asked on every poll); /ui/state fills what they lack.
        let batchSlotCount = m.engineSeqs ?? ui?.maxSeqs
        let kvFree = m.kvFreePages ?? ui?.freePages
        let kvCheckpointOnly = m.kvCheckpointOnlyPages ?? ui?.checkpointOnlyPages
        let checkpoints = m.prefixCacheCheckpoints ?? ui?.checkpoints
        let draft: String? = ui?.spec.map { $0 ? "DFlash2" : "none" }
        var info: EngineInfo?
        if batchSlotCount != nil || kvFree != nil || checkpoints != nil || blocked != nil || ui?.hasInfo == true || ui?.hasSched == true {
            info = EngineInfo(kvFormat: ui?.kvDtype, draftWindow: draft, batchSlots: batchSlotCount,
                              kvFreePages: kvFree, kvCheckpointOnlyPages: kvCheckpointOnly,
                              prefixCheckpoints: checkpoints, admissionBlocked: blocked,
                              gpuName: ui?.gpu, targetSeqs: ui?.targetSeqs)
        }

        // The engine itself says it is loading or restarting (tick.engine): nothing can run yet.
        if let state = ui?.engineState, state.caseInsensitiveCompare("ready") != .orderedSame { status = .loading("State: " + state) }

        // The video card: the NVML sample (tick.sample). Video memory used is the total minus the "free" part of the engine's own
        // accounting (tick.vram.parts). RAM and CPU are not in /ui/state: nil.
        var system: SystemStats?
        if let u = ui, u.hasSample || u.vramTotal != nil {
            let free = u.part("free")?.bytes
            system = SystemStats(gpuPercent: u.util, gpuTemperatureCelsius: u.temp, gpuPowerWatts: u.power, gpuPowerLimitWatts: u.powerLimit,
                                 gpuMemoryUsedBytes: u.vramTotal.flatMap { t in free.map { t - $0 } },
                                 gpuMemoryTotalBytes: u.vramTotal, gpuMemoryBusyPercent: u.memUtil)
        }

        // The KV cache: its budget is the "KV cache" part of the video memory, in use is that minus its own "free" part; the counters are
        // the engine's (lookups, hits, evicted). No conversation slots or parked list: the engine has none.
        var cache: CacheStats?
        if let u = ui, let kv = u.part("KV cache"), let kvBytes = kv.bytes {
            let kvFreeBytes = kv.parts.first { $0.name == "free" }?.bytes
            cache = CacheStats(budgetBytes: kvBytes, usedBytes: kvFreeBytes.map { kvBytes - $0 } ?? 0,
                               requests: u.lookups ?? 0, requestsReused: u.hits ?? 0, evictions: u.evicted ?? 0)
        }

        // One row per engine slot: a running request fills one, the rest are idle. Only when the slot count is known.
        var slots: [BatchSlot]?
        if let count = batchSlotCount, count > 0, let seqs = seqs {
            let held = seqs.filter { $0.isState("running") || $0.isState("parked") || $0.isState("admitted") }
            var rows: [BatchSlot] = []
            for i in 0..<max(Int(count), held.count) {
                guard i < held.count else { rows.append(BatchSlot(index: i, state: "idle")); continue }
                let s = held[i], isRunning = s.isState("running")
                let state = isRunning ? (s.isPhase("decode") ? "decoding" : s.isPhase("prefill") ? "reading" : "running") : (s.state ?? "").lowercased()
                var tps: Double?
                if isRunning, s.isPhase("decode"), let out = s.out, let sec = s.decodeSeconds, sec > 0 { tps = out / sec }
                rows.append(BatchSlot(index: i, state: state, promptTokens: s.prompt, promptRead: s.fed, generated: s.out,
                                      tokensPerSecond: tps, maxTokens: s.maxNew))
            }
            slots = rows
        }

        // The context of the fullest running request: the prompt fed so far plus what it wrote (the prompt when fed is missing); otherwise
        // the newest finished one. The maximum is info.max_tokens (0 = unknown).
        let recent = ui?.done ?? []
        var ctxUsed: Double?
        for s in (seqs ?? []) where s.isState("running") {
            if let basis = s.fed ?? s.prompt { ctxUsed = max(ctxUsed ?? 0, basis + (s.out ?? 0)) }
        }
        if ctxUsed == nil, let newest = recent.first { ctxUsed = newest.contextTokens }

        return EngineSnapshot(engine: Self.brand, model: model, status: status,
                              queued: waitingFromSeqs ?? m.waiting.map { Int(max(0, min($0, 1e9))) } ?? 0,
                              live: live, recent: recent,
                              context: ContextUsage(used: ctxUsed, max: ui?.maxTokens ?? 0, compactAt: compactAt),
                              system: system, cache: cache, totals: totals, links: links, info: info, slots: slots)
    }
}
