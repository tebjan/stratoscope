import Foundation

/// Reads the Basalt local inference engine (verified against Basalt 0.3.2 with its recorded payloads). Port of BasaltProvider.cs.
///
/// Basalt answers at the same addresses as Strata, so `StrataProvider` finds the router and the server and hands a Basalt
/// document to this reader (`recognises`). Data sources: Basalt's `/metrics`, and its `/slots` (the llama.cpp shape), asked only
/// while a request runs: `/metrics` gives the size of the prompt, only `/slots` says how much of it is read, and it also carries the
/// request's token limit and its cache reuse.
///
/// This snapshot model has no place for Basalt's engine details (version, KV format, expert cache), the video card's temperature,
/// power and memory, or its conversation cache budget; the Windows app shows them.
public final class BasaltReader {
    private let http = JSONFetcher()

    public init() {}

    /// Basalt sends `live.slots` as an object (`total`, `active`, `queue_depth`); Strata sends a list.
    public static func recognises(_ metrics: [String: Any]) -> Bool {
        (metrics["live"] as? [String: Any])?["slots"] is [String: Any]
    }

    /// Translates `metrics` after asking `<base>/slots` while a request runs. `compactAt` and `links` are the provider's (they depend on its config).
    public func read(_ metrics: [String: Any], base: String, model: String, compactAt: Double?, links: [EngineLink],
                     _ completion: @escaping (EngineSnapshot?) -> Void) {
        let state = (metrics["live"] as? [String: Any])?["state"] as? String
        guard state == "reading" || state == "generating" else {
            completion(snapshot(from: metrics, slots: [], model: model, compactAt: compactAt, links: links)); return
        }
        // A refused or failed /slots leaves the progress unknown and the rest as it is.
        http.getArray(base + "/slots") { [self] rows in
            completion(snapshot(from: metrics, slots: rows ?? [], model: model, compactAt: compactAt, links: links))
        }
    }

    /// Translates one /metrics document and the /slots rows. Internal so tests can feed recorded payloads.
    func snapshot(from m: [String: Any], slots: [[String: Any]], model: String, compactAt: Double?, links: [EngineLink]) -> EngineSnapshot {
        let eng = m["engine"] as? [String: Any] ?? [:], live = m["live"] as? [String: Any] ?? [:]
        let hw = m["hardware"] as? [String: Any] ?? [:], reqs = m["requests"] as? [[String: Any]] ?? []
        func num(_ o: [String: Any], _ k: String) -> Double? { (o[k] as? NSNumber)?.doubleValue }

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
        let row = slots.first { ($0["state"] as? String) == "prefill" || ($0["state"] as? String) == "decoding" }
        let reading = status == .prefill
        var liveRequest: LiveRequest?
        if status != .idle {
            let speed = num(live, "tok_s"), mean = num(live, "prefill_tok_s_mean")
            liveRequest = LiveRequest(
                promptRead: row.flatMap { num($0, "prefill_done") } ?? 0,
                promptTotal: row.flatMap { num($0, "prompt_tokens") } ?? num(live, "prompt_tokens") ?? 0,
                generated: row.flatMap { num($0, "generated") } ?? num(live, "generated") ?? 0,
                maxTokens: row.flatMap { num($0, "max_tokens") } ?? 0,
                elapsedSeconds: num(live, "elapsed_s") ?? 0,
                prefillTokensPerSecond: reading && (mean ?? 0) > 0 ? mean : nil,     // Basalt keeps sending the last read's mean while it generates
                decodeTokensPerSecond: (speed ?? 0) > 0 ? speed : nil,
                reusedEstimate: row.flatMap { num($0, "reused") } ?? 0)              // the engine's own number once it knows it
        }

        // The cache verdict of a row comes from the prompt and the reused part. Basalt's "hit_rate" is the expert cache's, not this.
        let recent = reqs.map { r -> RequestStats in
            let prompt = num(r, "prompt_tokens") ?? 0, reused = num(r, "reused") ?? 0
            return RequestStats(promptTokens: prompt, reusedTokens: reused, promptRead: max(0, prompt - reused),
                                promptMilliseconds: num(r, "prompt_ms") ?? 0, outputTokens: num(r, "output_tokens") ?? 0,
                                decodeTokensPerSecond: num(r, "decode_tok_s"), durationSeconds: num(r, "duration_s") ?? 0)
        }

        var ctxUsed = row.flatMap { num($0, "ctx_used") }
        if ctxUsed == nil, status != .idle, let p = num(live, "prompt_tokens") { ctxUsed = p + (num(live, "generated") ?? 0) }
        if ctxUsed == nil, let l = recent.first { ctxUsed = l.promptTokens + l.outputTokens }

        // The first video card; Basalt nests them under `gpus`.
        let gpu = (hw["gpus"] as? [[String: Any]])?.first
        let system = hw.isEmpty ? nil : SystemStats(ramUsedBytes: num(hw, "ram_used"), ramTotalBytes: num(hw, "ram_total"),
                                                    gpuPercent: gpu.flatMap { num($0, "util") }, cpuPercent: num(hw, "cpu"))

        let queue = num(live, "queued") ?? ((live["slots"] as? [String: Any]).flatMap { num($0, "queue_depth") }) ?? 0
        return EngineSnapshot(engine: "Basalt", model: model, status: status, queued: Int(queue), live: liveRequest, recent: recent,
                              context: ContextUsage(used: ctxUsed, max: num(eng, "max_context") ?? 0, compactAt: compactAt),
                              system: system, links: links, engineVersion: eng["version"] as? String)
    }
}
