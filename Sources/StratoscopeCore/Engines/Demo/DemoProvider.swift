import Foundation

/// Fixed fake data. Used for the README screenshot (`STRATOSCOPE_ENGINE=demo`), for trying out the UI
/// without an engine, and as the smallest possible example of an `EngineProvider`.
public final class DemoProvider: EngineProvider {
    public static let id = "demo"

    public enum Scenario { case prefill, decode, idle }
    private let scenario: Scenario

    /// The moment the fake data is "now". Pinned, so every screenshot comes out identical.
    public static let referenceDate = Date(timeIntervalSince1970: 1_780_000_000)

    public init(scenario: Scenario = .prefill) { self.scenario = scenario }

    public func poll(_ completion: @escaping (EngineSnapshot?) -> Void) { completion(snapshot()) }

    public func snapshot() -> EngineSnapshot {
        // (prompt tokens, tokens actually read, tokens generated, decode t/s, prefill t/s)
        func r(_ prompt: Double, _ read: Double, _ out: Double, _ tps: Double, _ pre: Double) -> RequestStats {
            let ms = read / pre * 1000
            return RequestStats(promptTokens: prompt, reusedTokens: prompt - read, promptRead: read, promptMilliseconds: ms,
                                outputTokens: out, decodeTokensPerSecond: tps, durationSeconds: ms / 1000 + out / tps)
        }
        // Newest first. The four oldest requests climb to the 110k marker; then the client starts over with a shorter
        // conversation (the drop), which the cache can mostly not reuse (orange).
        let recent = [
            r(42385, 88, 9, 30.7, 190),     r(41120, 2100, 212, 27.9, 238),  r(40210, 23826, 640, 24.1, 231),
            r(39800, 3100, 118, 28.6, 245), r(38950, 2600, 305, 26.4, 252),  r(38020, 2600, 77, 29.3, 240),
            r(36400, 27400, 410, 25.2, 226), r(35900, 27900, 150, 27.0, 249), r(109200, 3500, 96, 28.8, 244),
            r(106900, 2900, 540, 25.9, 235), r(104300, 2300, 61, 29.9, 255),  r(101800, 3400, 233, 27.7, 241),
        ]
        let gib = 1_073_741_824.0
        var live: LiveRequest?
        var status = EngineStatus.idle
        switch scenario {
        case .prefill:
            status = .prefill
            live = LiveRequest(promptRead: 57_840, promptTotal: 101_000, generated: 0, maxTokens: 24_576, elapsedSeconds: 232,
                               prefillTokensPerSecond: 247, decodeTokensPerSecond: nil, reusedEstimate: 41_000)
        case .decode:
            status = .decode
            live = LiveRequest(promptRead: 61_000, promptTotal: 61_000, generated: 1_840, maxTokens: 24_576, elapsedSeconds: 94,
                               prefillTokensPerSecond: 251, decodeTokensPerSecond: 26.4, reusedEstimate: 60_900)
        case .idle: break
        }
        // The context is the whole prompt plus what was generated so far (not only the part read so far).
        let contextUsed: Double = switch scenario { case .prefill: 101_000; case .decode: 62_840; case .idle: 42_394 }
        let entries = [
            CacheEntry(id: 5, tokens: 18_910, bytes: 0.8 * gib, parkedAt: DemoProvider.referenceDate.addingTimeInterval(-3900)),
            CacheEntry(id: 6, tokens: 42_385, bytes: 1.7 * gib, parkedAt: DemoProvider.referenceDate.addingTimeInterval(-1260)),
            CacheEntry(id: 7, tokens: 61_204, bytes: 2.4 * gib, parkedAt: DemoProvider.referenceDate.addingTimeInterval(-195)),
        ]
        return EngineSnapshot(
            engine: "Demo", model: "qwen3-coder-30b-iq3", status: status, queued: 0, live: live, recent: recent,
            context: ContextUsage(used: contextUsed, max: 131_072, compactAt: 110_000),
            system: SystemStats(ramUsedBytes: 48.3 * gib, ramTotalBytes: 64 * gib, gpuPercent: 94, cpuPercent: 18),
            cache: CacheStats(budgetBytes: 7 * gib, usedBytes: 4.9 * gib, slots: 4, parked: 3, requests: 214,
                              requestsReused: 186, evictions: 12, entries: entries),
            totals: Totals(requests: 214, outputTokens: 61_480, promptTokens: 7_940_000, reusedTokens: 6_660_000,
                           since: DemoProvider.referenceDate.addingTimeInterval(-3 * 3600 - 12 * 60)),
            links: [EngineLink(title: "Open engine UI", url: URL(string: "http://127.0.0.1:8095/")!)])
    }
}
