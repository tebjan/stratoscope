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
// This file: the Basalt source is live.tok_s, summed by the engine over its slots.

/// Reads the Basalt local inference engine (verified against Basalt 0.3.2 with its recorded payloads). Port of BasaltProvider.cs.
///
/// Basalt answers at the same addresses as Strata, so `StrataProvider` finds the router and the server and hands a Basalt
/// document to this reader (`recognises`). Data sources: Basalt's `/metrics`, and its `/slots` (the llama.cpp shape), asked only
/// while a request runs or when the engine has several slots (an idle slot says what it holds): `/metrics` gives the size of the
/// prompt, only `/slots` says how much of it is read, and it also carries the request's token limit and its cache reuse.
///
/// Engine details (version, KV format, draft window, expert cache, conversation cache budget) go to `EngineSnapshot.info`, the video
/// card's temperature, power and memory to `SystemStats`, the slot list to `EngineSnapshot.slots`. Basalt has no conversation cache
/// list and no totals, so `cache` and `totals` stay nil.
public final class BasaltReader {
    private let http = JSONFetcher()

    public init() {}

    /// Basalt sends `live.slots` as an object (`total`, `active`, `queue_depth`); Strata sends a list.
    public static func recognises(_ metrics: [String: Any]) -> Bool {
        (metrics["live"] as? [String: Any])?["slots"] is [String: Any]
    }

    /// Translates `metrics` after asking `<base>/slots` while a request runs or when there are several slots. `compactAt` and `links` are the provider's (they depend on its config).
    public func read(_ metrics: [String: Any], base: String, model: String, compactAt: Double?, links: [EngineLink],
                     _ completion: @escaping (EngineSnapshot?) -> Void) {
        let live = metrics["live"] as? [String: Any]
        let state = live?["state"] as? String
        let total = ((live?["slots"] as? [String: Any])?["total"] as? NSNumber)?.doubleValue ?? 0
        guard state == "reading" || state == "generating" || total > 1 else {
            completion(snapshot(from: metrics, slots: [], model: model, compactAt: compactAt, links: links)); return
        }
        // A refused or failed /slots leaves the progress and the slot list unknown and the rest as it is.
        http.getArray(base + "/slots") { [self] rows in
            completion(snapshot(from: metrics, slots: rows ?? [], model: model, compactAt: compactAt, links: links))
        }
    }

    /// Translates one /metrics document and the /slots rows. Internal so tests can feed recorded payloads.
    func snapshot(from m: [String: Any], slots: [[String: Any]], model: String, compactAt: Double?, links: [EngineLink]) -> EngineSnapshot {
        let eng = m["engine"] as? [String: Any] ?? [:], live = m["live"] as? [String: Any] ?? [:]
        let hw = m["hardware"] as? [String: Any] ?? [:], reqs = m["requests"] as? [[String: Any]] ?? []
        let slotCount = live["slots"] as? [String: Any]
        func num(_ o: [String: Any], _ k: String) -> Double? { (o[k] as? NSNumber)?.doubleValue }
        /// A number or a string, kept as text (Basalt sends the draft window as a number).
        func text(_ o: [String: Any], _ k: String) -> String? {
            if let s = o[k] as? String { return s }
            guard let d = num(o, k) else { return nil }
            return d == d.rounded() && abs(d) < 1e15 ? String(Int(d)) : String(d)
        }
        /// The sum of `key` over the rows, or nil when any row does not say.
        func sum(_ rows: [[String: Any]], _ key: String) -> Double? {
            var total = 0.0
            for r in rows { guard let v = num(r, key) else { return nil }; total += v }
            return total
        }

        // Status: Basalt says "reading" and "generating" and does not tell reasoning from the answer. Anything but "idle"
        // besides those means the weights are not ready for a request, and the engine's own word is the reason.
        let state = live["state"] as? String
        let status: EngineStatus
        switch state {
        case "idle": status = .idle
        case "reading": status = .prefill
        case "generating": status = .decode
        case "loading":
            let load = live["load"] as? [String: Any]
            if (load?["kind"] as? String) == "wake" { status = .loading("Waking up") }
            else if let p = load.flatMap({ num($0, "percent") }) { status = .loading("Loading weights \(Int(p.rounded())) %") }
            else { status = .loading("Loading weights") }
        case "unloaded": status = .loading("Weights not loaded")
        case "asleep": status = .loading("Asleep")
        case .some(let other): status = .loading("State: \(other)")
        case .none: status = .loading("State not reported")
        }
        if case .loading = status {
            return EngineSnapshot(engine: "Basalt", model: model, status: status, context: ContextUsage(max: 0), links: links)
        }

        // The running request: the slot row when /slots was asked (it is the request that holds the engine; /metrics describes the
        // newest one, which may still wait in the queue), otherwise /metrics.
        // With several requests running together the headline is their total, like Strata's: reading slots add up their prompts,
        // generating slots their counts, and the speed is live.tok_s (the engine already sums it over the slots).
        let running = slots.filter { let s = $0["state"] as? String; return s == "prefill" || s == "decoding" }
        let together = running.count > 1
        let row = running.first
        let reading = status == .prefill
        var liveRequest: LiveRequest?
        if status != .idle {
            let speed = num(live, "tok_s"), mean = num(live, "prefill_tok_s_mean")
            var total = row.flatMap { num($0, "prompt_tokens") } ?? num(live, "prompt_tokens")
            var read = row.flatMap { num($0, "prefill_done") }
            var generated = row.flatMap { num($0, "generated") } ?? num(live, "generated")
            if together {
                if reading {
                    let readers = running.filter { ($0["state"] as? String) == "prefill" }
                    total = sum(readers, "prompt_tokens")
                    read = sum(readers, "prefill_done")      // several readers: Basalt says nothing
                    generated = 0
                } else {
                    generated = sum(running.filter { ($0["state"] as? String) == "decoding" }, "generated")
                    total = nil; read = nil
                }
            }
            // Several requests at once only when Basalt has more than one slot.
            var parallel: Int?
            if together { parallel = running.count }
            else if let sc = slotCount, (num(sc, "total") ?? 0) > 1, let active = num(sc, "active"), active > 1 { parallel = Int(active) }
            // The model's counters are not optional (an unknown number is 0 there, as before); `running` and the slot list carry what is known.
            liveRequest = LiveRequest(
                promptRead: read ?? 0,
                promptTotal: total ?? 0,
                generated: generated ?? 0,
                maxTokens: together ? 0 : row.flatMap { num($0, "max_tokens") } ?? 0,
                elapsedSeconds: num(live, "elapsed_s") ?? 0,
                prefillTokensPerSecond: reading && (mean ?? 0) > 0 ? mean : nil,     // Basalt keeps sending the last read's mean while it generates
                decodeTokensPerSecond: (speed ?? 0) > 0 ? speed : nil,
                reusedEstimate: together ? 0 : row.flatMap { num($0, "reused") } ?? 0,   // the engine's own number once it knows it
                running: parallel)
        }

        // One entry per slot, for an engine with more than one: what each holds or runs. A single slot has nothing to tell apart.
        var batchSlots: [BatchSlot]?
        if slots.count > 1 {
            batchSlots = slots.enumerated().map { position, s in
                let st = s["state"] as? String
                let active = st == "prefill" || st == "decoding"
                let mapped: String?
                switch st {
                case "prefill": mapped = "reading"
                case "decoding": mapped = "decoding"
                case "idle": mapped = "idle"
                default: mapped = nil
                }
                let speed = num(s, "tok_s")
                return BatchSlot(
                    index: Int(num(s, "id") ?? Double(position)),
                    state: mapped,
                    heldTokens: st == "idle" ? num(s, "ctx_used") : nil,                  // an idle slot keeps its last conversation
                    promptTokens: active ? num(s, "prompt_tokens") : nil,
                    promptRead: active ? num(s, "prefill_done") : nil,
                    generated: active ? num(s, "generated") : nil,
                    tokensPerSecond: st == "decoding" && (speed ?? 0) > 0 ? speed : nil,
                    maxTokens: active ? num(s, "max_tokens") : nil)
            }
        }

        // The cache verdict of a row comes from the prompt and the reused part. Basalt's "hit_rate" is the expert cache's, not this.
        let recent = reqs.map { r -> RequestStats in
            let prompt = num(r, "prompt_tokens") ?? 0, reused = num(r, "reused") ?? 0
            let time = num(r, "time")
            let finished = time.flatMap { $0.isFinite && $0 > 0 && $0 < 253_402_300_000 ? Date(timeIntervalSince1970: $0) : nil }
            return RequestStats(promptTokens: prompt, reusedTokens: reused, promptRead: max(0, prompt - reused),
                                promptMilliseconds: num(r, "prompt_ms") ?? 0, outputTokens: num(r, "output_tokens") ?? 0,
                                decodeTokensPerSecond: num(r, "decode_tok_s"), durationSeconds: num(r, "duration_s") ?? 0,
                                finishedAt: finished, finish: r["finish"] as? String, expertHitRate: num(r, "hit_rate"))
        }

        // The slot closest to the limit when several run (the context bar shows how full the fullest is).
        var ctxUsed = running.compactMap { num($0, "ctx_used") }.max()
        if ctxUsed == nil, status != .idle, let p = num(live, "prompt_tokens") { ctxUsed = p + (num(live, "generated") ?? 0) }
        if ctxUsed == nil, let newest = reqs.first, let p = num(newest, "prompt_tokens"), let o = num(newest, "output_tokens") { ctxUsed = p + o }

        // One video card is shown (Basalt reports a list; this one is the first).
        let gpu = (hw["gpus"] as? [[String: Any]])?.first
        let system = hw.isEmpty ? nil : SystemStats(
            ramUsedBytes: num(hw, "ram_used"), ramTotalBytes: num(hw, "ram_total"),
            gpuPercent: gpu.flatMap { num($0, "util") }, cpuPercent: num(hw, "cpu"),
            gpuTemperatureCelsius: gpu.flatMap { num($0, "temp") }, gpuPowerWatts: gpu.flatMap { num($0, "power") },
            gpuPowerLimitWatts: gpu.flatMap { num($0, "power_limit") },
            gpuMemoryUsedBytes: gpu.flatMap { num($0, "mem_used") }, gpuMemoryTotalBytes: gpu.flatMap { num($0, "mem_total") })

        // The conversation cache: Basalt reports its budget and slot count, not what is parked. A budget of 0 is the cache switched off.
        var cacheBudget: Double?, cacheSlots: Int?
        if let budget = num(eng, "conversation_cache_mib"), budget > 0 {
            cacheBudget = budget * 1_048_576
            cacheSlots = num(eng, "conversation_cache_slots").map { Int($0) }
        }
        let info: EngineInfo? = eng.isEmpty ? nil : EngineInfo(
            version: eng["version"] as? String, kvFormat: eng["kv"] as? String, draftWindow: text(eng, "spec"),
            expertCacheMib: num(eng, "expert_cache_mib"), batchSlots: slotCount.flatMap { num($0, "total") },
            expertHitRate: reqs.first.flatMap { num($0, "hit_rate") },
            conversationCacheBudgetBytes: cacheBudget, conversationCacheSlots: cacheSlots)

        let queue = num(live, "queued") ?? slotCount.flatMap { num($0, "queue_depth") } ?? 0
        return EngineSnapshot(engine: "Basalt", model: model, status: status, queued: Int(queue), live: liveRequest, recent: recent,
                              context: ContextUsage(used: ctxUsed, max: num(eng, "max_context") ?? 0, compactAt: compactAt),
                              system: system, links: links, engineVersion: eng["version"] as? String,
                              info: info, slots: batchSlots)
    }
}
