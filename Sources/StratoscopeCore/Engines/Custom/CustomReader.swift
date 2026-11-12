import Foundation

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

/// Reads the custom 27B server (its metrics use the q27: prefix) behind a llama-swap router or directly.
///
/// Data sources, all GET on endpoints the server answers without waking it:
///  - `/health`: `{"status":"ok"}` when the engine is ready; another status (`unloaded`, `loading`) or HTTP 503 means no request can run
///    yet and `/metrics` is not asked. It names no engine, so it is recognised by its metrics.
///  - `/metrics` (Prometheus text, prefix `q27:`): running and waiting requests, the engine's sequence capacity, free KV pages,
///    prefix cache checkpoints, which admission block holds a request back, and the token totals.
///
/// Not reported, so not drawn: speeds, the phase of a request, prompt sizes, finished requests, context size, RAM, video card.
/// The phase is `.running` only: the metrics are totals across all requests and give no per-request phase, so neither reading nor
/// writing is shown. They return once the engine reports per-request phases.
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
                completion(translate(m, model: model, compactAt: compactAt, links: links))
            }
        }
    }

    /// Translates the metrics into a snapshot: running requests give `.running`, none give `.idle`. Internal so tests can feed recorded payloads.
    func translate(_ m: CustomMetrics, model: String, compactAt: Double? = nil,
                   links: [EngineLink] = []) -> EngineSnapshot {
        let running = m.running ?? 0
        let busy = running > 0
        let status: EngineStatus = busy ? .running : .idle

        // Only the number of requests running together is known; every other counter of the model stays nil (unknown).
        let live: LiveRequest? = busy ? LiveRequest(running: running > 1 ? Int(running) : nil) : nil

        // Counters since the engine started. The Mac model's totals are not optional, so they are given only when the engine
        // reports all four (an unknown one would show as 0). Prompt tokens are all prompts admitted, reused ones included.
        var totals: Totals?
        if let requests = m.requestsTotal, let prompt = m.promptTokensTotal, let cached = m.cachedTokensTotal, let output = m.generationTokensTotal {
            totals = Totals(requests: requests, outputTokens: output, promptTokens: prompt, reusedTokens: cached)
        }

        let blocked: String? = m.blockedKv == 1 ? "kv" : m.blockedSpeed == 1 ? "speed" : (m.blockedKv == 0 && m.blockedSpeed == 0) ? "none" : nil
        var info: EngineInfo?
        if m.engineSeqs != nil || m.kvFreePages != nil || m.prefixCacheCheckpoints != nil || blocked != nil {
            info = EngineInfo(batchSlots: m.engineSeqs, kvFreePages: m.kvFreePages, kvCheckpointOnlyPages: m.kvCheckpointOnlyPages,
                              prefixCheckpoints: m.prefixCacheCheckpoints, admissionBlocked: blocked)
        }

        return EngineSnapshot(engine: Self.brand, model: model, status: status,
                              queued: m.waiting.map { Int(max(0, min($0, 1e9))) } ?? 0,
                              live: live, recent: [], context: ContextUsage(max: 0, compactAt: compactAt),
                              totals: totals, links: links, info: info)
    }
}
