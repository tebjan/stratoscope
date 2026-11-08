import Foundation

/// Adapter for the Strata local inference engine.
///
/// Data sources:
///  - the router's `/running` (optional, when a llama-swap style router sits in front of Strata)
///  - the Strata server's `/metrics`
///  - the Strata log, for the parked-conversation list that `/metrics` does not name
public final class StrataProvider: EngineProvider {
    public static let id = "strata"

    private let config: Config
    private let http = JSONFetcher()
    private let cacheLog = StrataCacheLog()
    private var proxy = "http://127.0.0.1:8095"
    private var lastRead: (Double, Date)?
    private let clients: ClientConfigReader?

    /// `clients`: where to look for the summarise threshold of the AI clients that use this engine.
    /// Nil (the default) reads no client config.
    public init(config: Config, clients: ClientConfigReader? = nil) { self.config = config; self.clients = clients }

    // MARK: Polling

    public func poll(_ completion: @escaping (EngineSnapshot?) -> Void) {
        guard let router = config.router else { pollDirect(completion); return }
        http.get(router + "/running") { [weak self] r in
            guard let self = self else { return }
            guard let running = (r?["running"] as? [[String: Any]])?.first else { self.pollDirect(completion); return }
            if let p = running["proxy"] as? String { self.proxy = p }
            let name = (running["model"] as? String) ?? "Model"
            let state = running["state"] as? String ?? "?"
            if state != "ready" {
                completion(self.placeholder(model: name, status: .loading("State: \(state)"))); return
            }
            self.http.get(self.proxy + "/metrics") { m in
                guard let m = m else { completion(self.placeholder(model: name, status: .loading("Server not responding"))); return }
                completion(self.snapshot(from: m, model: name))
            }
        }
    }

    /// Without a router: ask the Strata server directly. If nothing answers, the menu bar item stays hidden.
    private func pollDirect(_ completion: @escaping (EngineSnapshot?) -> Void) {
        let base = config.url ?? "http://127.0.0.1:8095"
        http.get(base + "/metrics") { [weak self] m in
            guard let self = self, let m = m, let eng = m["engine"] as? [String: Any] else { completion(nil); return }
            self.proxy = base
            completion(self.snapshot(from: m, model: eng["model"] as? String ?? "Strata"))
        }
    }

    private func placeholder(model: String, status: EngineStatus) -> EngineSnapshot {
        EngineSnapshot(engine: "Strata", model: model, status: status, context: ContextUsage(max: 0), links: links())
    }

    // MARK: Translation

    private func links() -> [EngineLink] {
        var out: [EngineLink] = []
        if let u = URL(string: proxy + "/") { out.append(EngineLink(title: "Open Strata UI", url: u)) }
        if let r = config.router, let u = URL(string: r + "/ui/") { out.append(EngineLink(title: "Open router UI", url: u)) }
        if let w = config.webui, let u = URL(string: w) { out.append(EngineLink(title: "Open chat UI", url: u)) }
        if let l = currentLogPath() { out.append(EngineLink(title: "Open log", url: URL(fileURLWithPath: l))) }
        return out
    }

    /// The engine's log: configured path, otherwise the most recently written *.log of the Strata install.
    func currentLogPath() -> String? {
        if let l = config.log { return l }
        let dir = NSHomeDirectory() + "/.local/share/strata/install"
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: dir) else { return nil }
        var best: (String, Date)?
        for n in names where n.hasSuffix(".log") {
            let p = dir + "/" + n
            if let m = (try? fm.attributesOfItem(atPath: p))?[.modificationDate] as? Date, best == nil || m > best!.1 { best = (p, m) }
        }
        return best?.0
    }

    /// Strata's /metrics has no such value. It comes from the configs of the clients that talk to this
    /// server and use this model; where those say nothing (or disagree), the user's own `compact_at` is the fallback.
    private func compactAt(model: String) -> Double? {
        let endpoints = [config.router, config.url ?? "http://127.0.0.1:8095", proxy].compactMap { $0 }
        return clients?.compactAt(endpoints: endpoints, model: config.clientModel ?? model) ?? config.compactAt
    }

    func snapshot(from m: [String: Any], model: String) -> EngineSnapshot {
        let eng = m["engine"] as? [String: Any] ?? [:], live = m["live"] as? [String: Any] ?? [:]
        let hw = m["hardware"] as? [String: Any] ?? [:], cc = m["conversation_cache"] as? [String: Any] ?? [:]
        let tot = m["totals"] as? [String: Any] ?? [:], reqs = m["requests"] as? [[String: Any]] ?? []
        func num(_ o: [String: Any], _ k: String) -> Double? { (o[k] as? NSNumber)?.doubleValue }

        // Status
        let state = live["state"] as? String ?? "idle"
        let phase = (live["phase"] as? String) ?? ""
        let reading = state != "idle" && (phase.contains("prompt") || (num(live, "generated") ?? 0) == 0)
        let status: EngineStatus = state == "idle" ? .idle : reading ? .prefill : (phase == "thinking" ? .thinking : .decode)

        // Prefill speed: Strata's mean, otherwise derived from progress between two polls.
        var prefill = num(live, "prefill_tok_s_mean")
        if (prefill ?? 0) <= 0, reading, let read = num(live, "prompt_read"), let (r0, t0) = lastRead, read > r0 {
            prefill = (read - r0) / Date().timeIntervalSince(t0)
        }
        if reading, let read = num(live, "prompt_read") {
            if lastRead == nil || read != lastRead!.0 { lastRead = (read, Date()) }
        } else { lastRead = nil }

        // Cache reuse of the running request is not reported directly: when it starts, `prompt_read`
        // already sits at the reused part; later, read = mean prefill speed x elapsed time.
        var reused = 0.0
        if reading, let read = num(live, "prompt_read") {
            let mean = num(live, "prefill_tok_s_mean") ?? 0, el = num(live, "elapsed_s") ?? 0
            reused = (mean > 0 && el > 1) ? max(0, read - mean * el) : read
        }
        var liveRequest: LiveRequest?
        if state != "idle" {
            let t = num(live, "tok_s")
            liveRequest = LiveRequest(
                promptRead: num(live, "prompt_read") ?? 0, promptTotal: num(live, "prompt_total") ?? 0,
                generated: num(live, "generated") ?? 0, maxTokens: num(live, "max_tokens") ?? 0,
                elapsedSeconds: num(live, "elapsed_s") ?? 0, prefillTokensPerSecond: prefill,
                decodeTokensPerSecond: (t ?? 0) > 0 ? t : num(live, "tok_s_mean"), reusedEstimate: reused)
        }

        let recent = reqs.map {
            RequestStats(promptTokens: num($0, "prompt_tokens") ?? 0, reusedTokens: num($0, "reused") ?? 0,
                         promptRead: num($0, "prompt_read") ?? 0, promptMilliseconds: num($0, "prompt_ms") ?? 0,
                         outputTokens: num($0, "output_tokens") ?? 0, decodeTokensPerSecond: num($0, "decode_tok_s"),
                         durationSeconds: num($0, "duration_s") ?? 0)
        }

        var ctxUsed = num(live, "prompt_tokens").map { $0 + (num(live, "generated") ?? 0) }
        if ctxUsed == nil, let l = recent.first { ctxUsed = l.promptTokens + l.outputTokens }

        let system = hw.isEmpty ? nil : SystemStats(ramUsedBytes: num(hw, "ram_used"), ramTotalBytes: num(hw, "ram_total"),
                                                    gpuPercent: num(hw, "gpu_util"), cpuPercent: num(hw, "cpu"))

        var cache: CacheStats?
        if !cc.isEmpty {
            cacheLog.update(path: currentLogPath())
            var entries = cacheLog.entries
            let parked = Int(num(cc, "parked") ?? Double(entries.count))
            while entries.count > parked && !entries.isEmpty { entries.removeFirst() }
            cache = CacheStats(budgetBytes: (num(cc, "budget_mib") ?? 0) * 1_048_576, usedBytes: num(cc, "bytes") ?? 0,
                               slots: Int(num(cc, "slots") ?? 0), parked: parked, requests: num(cc, "requests") ?? 0,
                               requestsReused: num(cc, "requests_reused") ?? 0, evictions: num(cc, "evictions") ?? 0,
                               entries: entries)
        }

        var totals: Totals?
        if !tot.isEmpty {
            totals = Totals(requests: num(tot, "requests") ?? 0, outputTokens: num(tot, "output_tokens") ?? 0,
                            promptTokens: num(tot, "prompt_tokens") ?? 0, reusedTokens: num(tot, "reused") ?? 0,
                            since: num(tot, "since").map { Date(timeIntervalSince1970: $0) })
        }

        return EngineSnapshot(engine: "Strata", model: model, status: status, queued: Int(num(live, "queued") ?? 0),
                              live: liveRequest, recent: recent,
                              context: ContextUsage(used: ctxUsed, max: num(eng, "max_context") ?? 0,   // 0 = the engine did not say
                                                    compactAt: compactAt(model: model)),
                              system: system, cache: cache, totals: totals, links: links())
    }
}
