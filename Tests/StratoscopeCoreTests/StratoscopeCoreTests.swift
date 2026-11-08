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
}

@Suite struct ContextTests {
    @Test func testCompactAtComesFromEnvironment() throws {
        let h = NSTemporaryDirectory() + "stratoscope-test-\(UUID().uuidString)"
        try FileManager.default.createDirectory(atPath: h, withIntermediateDirectories: true)
        #expect(Config.load(environment: ["STRATOSCOPE_COMPACT_AT": "60000"], home: h).compactAt == 60000)
        #expect(Config.load(environment: [:], home: h).compactAt == nil)   // nobody said: no marker
    }

    @Test func testFreeIsLimitMinusUsed() {
        #expect(ContextUsage(used: 57_840, max: 98_304).free == 40_464)
    }

    @Test func testFreeIsNilWhenEitherNumberIsUnknown() {
        #expect(ContextUsage(used: nil, max: 98_304).free == nil)
        #expect(ContextUsage(used: 1_000, max: 0).free == nil)   // 0 = the engine did not report a limit
    }

    @Test func testFreeNeverGoesNegative() {
        #expect(ContextUsage(used: 100, max: 50).free == 0)
    }

    @Test func testContextAfterRequestIsPromptPlusAnswer() {
        #expect(RequestStats(promptTokens: 42_385, outputTokens: 9).contextTokens == 42_394)
    }
}

@Suite struct ClientLimitsTests {
    private let kilo = """
    {
      // local model through the router
      "model": "openai-compatible/m1",
      "provider": {
        "openai-compatible": {
          "options": { "apiKey": "SECRET-VALUE", "baseURL": "http://127.0.0.1:8080/v1", },
          "models": {
            "m1": { "limit": { "context": 90112, "output": 24576 } },   /* main */
            "m2": { "limit": { "context": 32768, "output": 8192 } }
          }
        }
      }
    }
    """
    private let opencode = """
    { "providers": { "llama-swap": { "settings": { "baseURL": "http://localhost:8080/v1", "apiKey": "SECRET-VALUE" },
      "models": { "m1": { "modelID": "m1", "limit": { "context": 90112, "output": 24576 } } } } } }
    """

    @Test func testReadsBothLayoutsAndComments() {
        let k = ClientLimits.parse(kilo, client: "Kilo")
        #expect(k.count == 2)
        #expect(k.first { $0.model == "m1" }?.compactAt == 65_536)
        #expect(k.first { $0.model == "m1" }?.baseURL == "http://127.0.0.1:8080/v1")
        let o = ClientLimits.parse(opencode, client: "OpenCode")
        #expect(o.count == 1 && o[0].compactAt == 65_536)
    }

    @Test func testCommentMarkersInsideStringsSurvive() {
        let t = ClientLimits.stripJSONComments(#"{"u": "http://x/y // not a comment"} // real comment"#)
        #expect(t.contains("http://x/y // not a comment"))
        #expect(!t.contains("real comment"))
    }

    @Test func testBrokenFileGivesNothing() {
        #expect(ClientLimits.parse("{ not json", client: "Kilo").isEmpty)
        #expect(ClientLimits.parse("{}", client: "Kilo").isEmpty)
    }

    @Test func testMatchesByAddressAndModel() {
        let l = ClientLimits.parse(kilo, client: "Kilo")
        #expect(ClientLimits.compactAt(limits: l, endpoints: ["http://127.0.0.1:8080"], model: "m1") == 65_536)
        #expect(ClientLimits.compactAt(limits: l, endpoints: ["http://localhost:8080"], model: "m1") == 65_536)   // same address
        #expect(ClientLimits.compactAt(limits: l, endpoints: ["http://127.0.0.1:8095"], model: "m1") == nil)      // other server
        #expect(ClientLimits.compactAt(limits: l, endpoints: ["http://127.0.0.1:8080"], model: "other") == nil)   // other model
        #expect(ClientLimits.compactAt(limits: l, endpoints: [], model: "m1") == nil)
    }

    @Test func testClientsThatAgreeCountOnce() {
        let l = ClientLimits.parse(kilo, client: "Kilo") + ClientLimits.parse(opencode, client: "OpenCode")
        #expect(ClientLimits.compactAt(limits: l, endpoints: ["http://127.0.0.1:8080"], model: "m1") == 65_536)
    }

    @Test func testClientsThatDisagreeGiveNoMarker() {
        let a = ClientModelLimit(client: "A", baseURL: "http://127.0.0.1:8080/v1", model: "m", context: 100_000, output: 20_000)
        let b = ClientModelLimit(client: "B", baseURL: "http://127.0.0.1:8080/v1", model: "m", context: 100_000, output: 30_000)
        #expect(ClientLimits.compactAt(limits: [a, b], endpoints: ["http://127.0.0.1:8080"], model: "m") == nil)
    }

    @Test func testOutputLargerThanContextIsIgnored() {
        let a = ClientModelLimit(client: "A", baseURL: "http://127.0.0.1:8080/v1", model: "m", context: 8_000, output: 9_000)
        #expect(ClientLimits.compactAt(limits: [a], endpoints: ["http://127.0.0.1:8080"], model: "m") == nil)
    }

    private func home() throws -> String {
        let dir = NSTemporaryDirectory() + "stratoscope-test-\(UUID().uuidString)"
        try FileManager.default.createDirectory(atPath: dir + "/.config/kilo", withIntermediateDirectories: true)
        try kilo.write(toFile: dir + "/.config/kilo/kilo.jsonc", atomically: true, encoding: .utf8)
        return dir
    }

    @Test func testReaderFindsKiloConfig() throws {
        let r = ClientConfigReader(home: try home())
        #expect(r.compactAt(endpoints: ["http://127.0.0.1:8080"], model: "m1") == 65_536)
        #expect(ClientConfigReader(home: "/nonexistent").limits().isEmpty)
    }

    @Test func testStrataReadsClientConfigAndFallsBackToManualSetting() throws {
        let m = try JSONSerialization.jsonObject(with: Data(#"{"engine": {"max_context": 98304}, "live": {"state": "idle"}}"#.utf8)) as! [String: Any]
        var c = Config(); c.log = "/nonexistent.log"; c.router = "http://127.0.0.1:8080"
        let reader = ClientConfigReader(home: try home())
        #expect(StrataProvider(config: c, clients: reader).snapshot(from: m, model: "m1").context.compactAt == 65_536)
        #expect(StrataProvider(config: c, clients: nil).snapshot(from: m, model: "m1").context.compactAt == nil)   // switched off
        c.compactAt = 50_000
        #expect(StrataProvider(config: c, clients: reader).snapshot(from: m, model: "m1").context.compactAt == 65_536)   // what the client says wins
        #expect(StrataProvider(config: c, clients: reader).snapshot(from: m, model: "unknown").context.compactAt == 50_000)   // nothing to read: fallback
        #expect(StrataProvider(config: c, clients: nil).snapshot(from: m, model: "m1").context.compactAt == 50_000)
    }

    @Test func testConfigCanSwitchReadingOff() throws {
        #expect(Config.load(environment: [:], home: "/nonexistent").readClientConfigs == true)
        #expect(Config.load(environment: ["STRATOSCOPE_CLIENT_CONFIGS": "none"], home: "/nonexistent").readClientConfigs == false)
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

    @Test func testCompactAtIsPassedThroughOnlyWhenConfigured() {
        let m = metrics(#"{"state": "idle", "queued": 0}"#)
        #expect(provider().snapshot(from: m, model: "m").context.compactAt == nil)
        var c = Config(); c.log = "/nonexistent.log"; c.compactAt = 60_000
        #expect(StrataProvider(config: c).snapshot(from: m, model: "m").context.compactAt == 60_000)
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
