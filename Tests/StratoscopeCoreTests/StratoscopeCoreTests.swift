import Foundation
import Testing
@testable import StratoscopeCore

@Suite struct FormatTests {
    @Test func testSpeedIsPaddedToThreeDigits() {
        #expect(Fmt.speed(5) == "\u{2007}\u{2007}5 t/s")
        #expect(Fmt.speed(222) == "222 t/s")
        #expect(Fmt.speed(nil) == "\u{2007}\u{2007}0 t/s")
        #expect(Fmt.speed(-3) == "\u{2007}\u{2007}0 t/s")
    }

    @Test func testNumbersUseEnglishFormatting() {
        #expect(Fmt.number(1234567) == "1,234,567")
        #expect(Fmt.number(26.44, 1) == "26.4")
        #expect(Fmt.number(nil) == "–")
        #expect(Fmt.number(.infinity) == "–")
    }

    @Test func testDuration() {
        #expect(Fmt.duration(42) == "42 s")
        #expect(Fmt.duration(125) == "2 min 5 s")
        #expect(Fmt.duration(3 * 3600 + 12 * 60) == "3 h 12 min")
        #expect(Fmt.duration(-5) == "0 s")
    }
}

@Suite struct CacheHitTests {
    @Test func testClassification() {
        #expect(CacheHit.classify(prompt: 3000, reused: 0) == .small)
        #expect(CacheHit.classify(prompt: 50_000, reused: 45_000) == .hit)       // exactly 90 %
        #expect(CacheHit.classify(prompt: 50_000, reused: 44_999) == .partial)
        #expect(CacheHit.classify(prompt: 50_000, reused: 8_192) == .partial)
        #expect(CacheHit.classify(prompt: 50_000, reused: 8_191) == .miss)
    }
}

@Suite struct ConfigTests {
    private func home(with json: String?) throws -> String {
        let dir = NSTemporaryDirectory() + "stratoscope-test-\(UUID().uuidString)"
        try FileManager.default.createDirectory(atPath: dir + "/.config", withIntermediateDirectories: true)
        if let json = json { try json.write(toFile: dir + "/.config/stratoscope.json", atomically: true, encoding: .utf8) }
        return dir
    }

    @Test func testDefaults() throws {
        let c = Config.load(environment: [:], home: try home(with: nil))
        #expect(c.engine == "strata")
        #expect(c.url == nil); #expect(c.router == nil); #expect(c.compactAt == nil)
    }

    @Test func testEnvironmentBeatsFile() throws {
        let h = try home(with: #"{"url":"http://file:1","webui":"http://ui:2","compact_at":1000}"#)
        let c = Config.load(environment: ["STRATOSCOPE_URL": "http://env:3"], home: h)
        #expect(c.url == "http://env:3")
        #expect(c.webui == "http://ui:2")
        #expect(c.compactAt == 1000)
    }

    @Test func testRouterNoneDisablesRouter() throws {
        let c = Config.load(environment: ["STRATOSCOPE_ROUTER_URL": "none"], home: try home(with: nil))
        #expect(c.router == nil)
    }

    @Test func testEnvironmentCompactAt() throws {
        let c = Config.load(environment: ["STRATOSCOPE_COMPACT_AT": "60000"], home: try home(with: nil))
        #expect(c.compactAt == 60000)
    }
}

@Suite struct StrataCacheLogTests {
    private func parked(_ tokens: Int, total: Int, bytes: Int = 1000) -> String {
        "conversation cache: parked \(tokens) tokens in 12.3 ms; parked=\(total) bytes=\(bytes) evictions=0 snapshot_bytes=\(bytes)"
    }

    @Test func testParkRestoreEvict() {
        let log = StrataCacheLog()
        log.handle(parked(1000, total: 1), known: true)
        log.handle(parked(2000, total: 2), known: true)
        #expect(log.entries.map(\.tokens) == [1000, 2000])
        #expect(log.entries.map(\.id) == [1, 2])

        log.handle("conversation cache: restored 1000 tokens in 5 ms parked=1", known: true)
        #expect(log.entries.map(\.tokens) == [2000])

        log.handle(parked(3000, total: 2), known: true)
        log.handle("conversation cache: evicted 1 parked conversation", known: true)
        #expect(log.entries.map(\.tokens) == [3000])
    }

    @Test func testSupersededCopyIsReplaced() {
        let log = StrataCacheLog()
        log.handle(parked(1000, total: 1), known: true)
        log.handle("conversation cache: dropped 1 superseded copy parked=0", known: true)
        log.handle(parked(1500, total: 1), known: true)    // same conversation, grown
        #expect(log.entries.map(\.tokens) == [1500])
    }

    @Test func testHistoryHasNoTimestamps() {
        let log = StrataCacheLog()
        log.handle(parked(1000, total: 1), known: false)
        #expect(log.entries.first?.parkedAt == nil)
    }

    @Test func testCacheOffClearsEverything() {
        let log = StrataCacheLog()
        log.handle(parked(1000, total: 1), known: true)
        log.handle("conversation cache: cache off", known: true)
        #expect(log.entries.isEmpty)
    }
}

@Suite struct StrataSnapshotTests {
    private func metrics(_ live: String) -> [String: Any] {
        let json = """
        {"engine": {"model": "m", "max_context": 98304},
         "live": \(live),
         "requests": [{"prompt_tokens": 42385, "reused": 42297, "prompt_read": 88, "prompt_ms": 2444.2,
                       "output_tokens": 9, "decode_tok_s": 30.7, "duration_s": 2.7}],
         "hardware": {"ram_used": 100, "ram_total": 200, "gpu_util": 90, "cpu": 10},
         "conversation_cache": {"budget_mib": 7168, "bytes": 1048576, "slots": 4, "parked": 0, "requests": 10, "requests_reused": 9, "evictions": 1},
         "totals": {"requests": 5, "output_tokens": 50, "prompt_tokens": 1000, "reused": 500, "since": 1700000000}}
        """
        return try! JSONSerialization.jsonObject(with: Data(json.utf8)) as! [String: Any]
    }

    private func provider() -> StrataProvider {
        var c = Config(); c.log = "/nonexistent.log"
        return StrataProvider(config: c)
    }

    @Test func testIdle() {
        let s = provider().snapshot(from: metrics(#"{"state": "idle", "queued": 0}"#), model: "m")
        #expect(s.status == .idle)
        #expect(s.live == nil)
        #expect(s.recent.count == 1)
        #expect(s.context.used == 42394)           // last request: prompt + output
        #expect(s.cache?.budgetBytes == 7168.0 * 1_048_576)
        #expect(s.system?.gpuPercent == 90)
        #expect(s.totals?.requests == 5)
    }

    @Test func testPrefillWithReuse() {
        let live = #"{"state": "busy", "phase": "prompt", "queued": 2, "prompt_total": 100000, "prompt_read": 60000, "generated": 0, "max_tokens": 4096, "elapsed_s": 100, "prefill_tok_s_mean": 250, "prompt_tokens": 100000}"#
        let s = provider().snapshot(from: metrics(live), model: "m")
        #expect(s.status == .prefill)
        #expect(s.queued == 2)
        #expect(s.live?.percent == 60)
        #expect(abs((s.live?.reusedEstimate ?? 0) - 35_000) < 0.001)   // 60,000 read - 250 t/s x 100 s
        #expect(s.live?.prefillTokensPerSecond == 250)
    }

    @Test func testDecodeAndThinking() {
        let base = #"{"state": "busy", "phase": "%@", "prompt_total": 10, "prompt_read": 10, "generated": 50, "max_tokens": 100, "elapsed_s": 5, "tok_s": 0, "tok_s_mean": 26.5}"#
        #expect(provider().snapshot(from: metrics(String(format: base, "decode")), model: "m").status == .decode)
        let thinking = provider().snapshot(from: metrics(String(format: base, "thinking")), model: "m")
        #expect(thinking.status == .thinking)
        #expect(thinking.live?.decodeTokensPerSecond == 26.5)   // falls back to the mean while the live value is 0
    }

    @Test func testContextMaxIsZeroWhenEngineDoesNotSayAndSnapshotIsEmptyOtherwise() {
        let m: [String: Any] = ["engine": ["model": "m"], "live": ["state": "idle"]]
        #expect(provider().snapshot(from: m, model: "m").context.max == 0)
    }

    @Test func testMissingSectionsStayNil() {
        let m: [String: Any] = ["engine": ["model": "m"], "live": ["state": "idle"]]
        let s = provider().snapshot(from: m, model: "m")
        #expect(s.system == nil); #expect(s.cache == nil); #expect(s.totals == nil)
    }
}

@Suite struct DemoProviderTests {
    @Test func testScenarios() {
        #expect(DemoProvider(scenario: .prefill).snapshot().status == .prefill)
        #expect(DemoProvider(scenario: .decode).snapshot().status == .decode)
        #expect(DemoProvider(scenario: .idle).snapshot().status == .idle)
    }

    @Test func testRegistryKnowsBothEngines() {
        #expect(EngineRegistry.available == ["strata", "demo"])
        var c = Config(); c.engine = "demo"
        #expect(EngineRegistry.make(c) is DemoProvider)
    }
}
