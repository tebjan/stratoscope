import Foundation
import Testing
@testable import StratoscopeCore

/// Reads the recorded payloads in Tests/Fixtures (shared with the Windows tests, see Tests/Fixtures/README.md).
enum Fixture {
    static let directory = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("Fixtures")

    static func data(_ name: String) -> Data {
        try! Data(contentsOf: directory.appendingPathComponent(name))
    }
    static func object(_ name: String) -> [String: Any] {
        try! JSONSerialization.jsonObject(with: data(name)) as! [String: Any]
    }
    static func rows(_ name: String) -> [[String: Any]] {
        try! JSONSerialization.jsonObject(with: data(name)) as! [[String: Any]]
    }
    static func number(_ o: [String: Any], _ key: String) -> Double? { (o[key] as? NSNumber)?.doubleValue }
}

// The same expectations as Tests/StratoscopeWindowsTests/BasaltProviderTests.cs, on the same Basalt 0.3.2 recordings.
// Differences by design: the Mac model's LiveRequest counters are not optional, so "unknown" is 0 there; the conversation cache
// budget is carried in `info` (a `CacheStats` would show used and parked as 0).
@Suite struct BasaltFixtureTests {
    private let reader = BasaltReader()

    private func snapshot(_ fixture: String, slots: String? = nil) -> EngineSnapshot {
        reader.snapshot(from: Fixture.object("basalt-0.3.2-\(fixture).json"),
                        slots: slots.map { Fixture.rows("basalt-0.3.2-\($0).slots.json") } ?? [],
                        model: "m", compactAt: nil, links: [])
    }

    private func inline(_ json: String) -> [String: Any] {
        try! JSONSerialization.jsonObject(with: Data(json.utf8)) as! [String: Any]
    }

    private func inlineRows(_ json: String) -> [[String: Any]] {
        try! JSONSerialization.jsonObject(with: Data(json.utf8)) as! [[String: Any]]
    }

    private func translate(_ metrics: [String: Any], slots: [[String: Any]] = []) -> EngineSnapshot {
        reader.snapshot(from: metrics, slots: slots, model: "m", compactAt: nil, links: [])
    }

    @Test func testPrefillShowsTheProgressFromSlotsAndTheEnginesOwnSpeed() {
        let s = snapshot("prefill", slots: "prefill")
        #expect(s.engine == "Basalt")
        #expect(s.status == .prefill)
        #expect(s.queued == 2)
        #expect(s.live?.promptTotal == 56967)
        #expect(s.live?.promptRead == 33771)
        #expect(s.live?.percent == 59)
        #expect(s.live?.prefillTokensPerSecond == 4706.7)
        #expect(s.live?.elapsedSeconds == 7.9)
        #expect(s.live?.decodeTokensPerSecond == nil)       // live.tok_s is null while reading: not 0
        #expect(s.live?.maxTokens == 32000)
        #expect(s.context.used == 56967)
        #expect(s.context.max == 131072)
    }

    @Test func testPrefillWithoutSlotsHasTheSizeButNoProgress() {
        let s = snapshot("prefill")
        #expect(s.live?.promptTotal == 56967)
        #expect(s.slots == nil)
    }

    @Test func testTheRunningRequestComesFromTheSlotWhenMetricsDescribeAnotherOne() {
        // At the start of a read /metrics still names the newest request (41,198 tokens); the slot holds the one that is read (56,967).
        let s = snapshot("prefill-start", slots: "prefill-start")
        #expect(s.live?.promptTotal == 56967)
        #expect(s.live?.promptRead == 404)
        #expect(s.live?.prefillTokensPerSecond == nil)      // the engine has no mean yet
    }

    @Test func testDecodeShowsSpeedGeneratedCountAndTheContextOfTheSlot() {
        let s = snapshot("decode", slots: "decode")
        #expect(s.status == .decode)
        #expect(s.live?.decodeTokensPerSecond == 147.2)
        #expect(s.live?.generated == 1002)
        #expect(s.live?.prefillTokensPerSecond == nil)      // the last read's mean is not a speed now
        #expect(s.context.used == 57969)
        #expect(s.live?.reusedEstimate == 0)
    }

    // Four slots (--parallel 4), recorded while requests of about 20,000 tokens ran together with a long conversation in slot 1.

    @Test func testSlotsAreListedOneEntryEachWithTheirStateAndNumbers() throws {
        let rows = Fixture.rows("basalt-0.3.2-parallel-mixed.slots.json")
        let s = snapshot("parallel-mixed", slots: "parallel-mixed")
        let list = try #require(s.slots)
        #expect(list.map { $0.state } == ["decoding", "decoding", "reading", "idle"])
        #expect(list.map { $0.index } == [0, 1, 2, 3])
        let reading = list[2]
        #expect(reading.promptTokens == Fixture.number(rows[2], "prompt_tokens"))
        #expect(reading.promptRead == Fixture.number(rows[2], "prefill_done"))
        #expect(reading.maxTokens == Fixture.number(rows[2], "max_tokens"))
        #expect(reading.tokensPerSecond == nil)              // not generating: no speed
        #expect(reading.isReading)
        let decoding = list[1]
        #expect(decoding.generated == Fixture.number(rows[1], "generated"))
        #expect(decoding.tokensPerSecond == Fixture.number(rows[1], "tok_s"))
        #expect(decoding.heldTokens == nil)
        #expect(decoding.tokens == (decoding.promptTokens ?? 0) + (decoding.generated ?? 0))
        let idle = list[3]
        #expect(idle.heldTokens == Fixture.number(rows[3], "ctx_used"))   // an idle slot says what it still holds
        #expect(!idle.isActive)
        #expect(s.info?.batchSlots == 4)
    }

    @Test func testRequestsRunningTogetherGetTheirTotalInTheHeadline() throws {
        let rows = Fixture.rows("basalt-0.3.2-parallel-mixed.slots.json")
        let m = Fixture.object("basalt-0.3.2-parallel-mixed.json")
        let s = snapshot("parallel-mixed", slots: "parallel-mixed")
        #expect(s.status == .decode)                         // generating while any slot decodes
        #expect(s.live?.running == 3)
        #expect(s.live?.isParallel == true)
        #expect(s.live?.generated == (Fixture.number(rows[0], "generated") ?? -1) + (Fixture.number(rows[1], "generated") ?? -1))
        let live = try #require(m["live"] as? [String: Any])
        #expect(s.live?.decodeTokensPerSecond == Fixture.number(live, "tok_s"))     // the engine's own total over the slots
        #expect(s.live?.maxTokens == 0)                      // no single limit: unknown
        #expect(s.context.used == rows.compactMap { Fixture.number($0, "ctx_used") }.max())   // the fullest slot
        #expect(s.queued == 2)
    }

    @Test func testADecodingSlotAndAReadingOneAreBothShown() throws {
        let s = snapshot("parallel-reading", slots: "parallel-reading")
        let list = try #require(s.slots)
        #expect(list.map { $0.state } == ["decoding", "reading", "idle", "idle"])
        #expect(s.live?.running == 2)
        #expect(list[1].isReading)
        #expect((list[1].promptRead ?? .infinity) < (list[1].promptTokens ?? 0))
    }

    @Test func testSeveralReadingSlotsAddUpTheirPromptsAndSayNothingWhenBasaltSaysNoProgress() throws {
        let m = inline(#"{"engine":{"max_context":8192},"live":{"state":"reading","slots":{"total":4,"active":2,"queue_depth":0},"prefill_tok_s_mean":900}}"#)
        let rows = inlineRows(#"[{"id":0,"state":"prefill","prompt_tokens":1000,"prefill_done":null},{"id":1,"state":"prefill","prompt_tokens":3000,"prefill_done":null},{"id":2,"state":"idle","ctx_used":0},{"id":3,"state":"idle","ctx_used":0}]"#)
        let s = translate(m, slots: rows)
        #expect(s.status == .prefill)
        #expect(s.live?.promptTotal == 4000)
        #expect(s.live?.prefillTokensPerSecond == 900)
        let list = try #require(s.slots)
        #expect(list[2].heldTokens == 0)
    }

    @Test func testASingleSlotHasNoSlotList() {
        let s = snapshot("decode", slots: "decode")
        #expect(s.slots == nil)
        #expect(s.live?.running == nil)
    }

    @Test func testIdleWithFourSlotsListsWhatEachHolds() throws {
        let s = snapshot("parallel-idle", slots: "parallel-idle")
        #expect(s.status == .idle)
        #expect(s.live == nil)
        let list = try #require(s.slots)
        #expect(list.count == 4)
        #expect(list.allSatisfy { $0.state == "idle" && $0.heldTokens != nil })
    }

    @Test func testIdleHasNoRunningRequestAndTheContextOfTheNewestFinishedOne() throws {
        let s = snapshot("idle", slots: "idle")
        #expect(s.status == .idle)
        #expect(s.queued == 0)
        #expect(s.live == nil)
        #expect(!s.recent.isEmpty)
        #expect(s.context.used == s.recent[0].promptTokens + s.recent[0].outputTokens)
        #expect(s.context.max == 131072)
        let system = try #require(s.system)
        #expect(system.gpuMemoryTotalBytes != nil)
    }

    @Test func testSystemIsReadFromTheNestedVideoCardOfTheUnpatchedEngine() throws {
        let raw = String(decoding: Fixture.data("basalt-0.3.2-decode.json"), as: UTF8.self)
        #expect(!raw.contains("gpu_util"))                   // the premise: no flat fields
        let m = Fixture.object("basalt-0.3.2-decode.json")
        let hw = try #require(m["hardware"] as? [String: Any])
        let gpu = try #require((hw["gpus"] as? [[String: Any]])?.first)
        let system = try #require(translate(m).system)
        #expect(system.gpuPercent == Fixture.number(gpu, "util"))
        #expect(system.gpuTemperatureCelsius == Fixture.number(gpu, "temp"))
        #expect(system.gpuPowerWatts == Fixture.number(gpu, "power"))
        #expect(system.gpuPowerLimitWatts == Fixture.number(gpu, "power_limit"))
        #expect(system.gpuMemoryUsedBytes == Fixture.number(gpu, "mem_used"))
        #expect(system.gpuMemoryTotalBytes == Fixture.number(gpu, "mem_total"))
        #expect(system.ramUsedBytes == Fixture.number(hw, "ram_used"))
        #expect(system.ramTotalBytes == Fixture.number(hw, "ram_total"))
        #expect(system.cpuPercent == Fixture.number(hw, "cpu"))
        #expect(system.gpuPercent != nil)
    }

    @Test func testEngineDetailsAreTheEnginesOwnAndSectionsItDoesNotHaveAreNotDrawn() throws {
        let s = snapshot("decode")
        let info = try #require(s.info)
        #expect(info.version == "0.3.2")
        #expect(info.kvFormat == "int8")
        #expect(info.draftWindow == "8")
        #expect(info.expertCacheMib == 17086)
        #expect(info.batchSlots == 1)
        #expect(info.expertHitRate == 0.92)                  // the newest request's expert cache hit rate, not the cache dots
        #expect(info.conversationCacheBudgetBytes == nil)    // conversation_cache_mib is 0: the cache is switched off
        #expect(s.cache == nil)
        #expect(s.totals == nil)
        #expect(s.slots == nil)
    }

    @Test func testRequestRowsCarryTheCacheVerdictFromPromptAndReusedNotFromTheExpertHitRate() throws {
        let s = snapshot("decode")
        #expect(s.recent.count == 12)
        let partial = try #require(s.recent.first { $0.promptTokens == 38964 })    // 19,435 of 38,964 came from the conversation cache
        #expect(partial.reusedTokens == 19435)
        #expect(CacheHit.classify(prompt: partial.promptTokens, reused: partial.reusedTokens) == .partial)
        #expect(partial.promptRead == 19529)
        let miss = try #require(s.recent.first { $0.promptTokens == 90494 })       // nothing reused
        #expect(miss.reusedTokens == 0)
        #expect(CacheHit.classify(prompt: miss.promptTokens, reused: miss.reusedTokens) == .miss)
        let small = try #require(s.recent.first { $0.promptTokens == 53 })
        #expect(CacheHit.classify(prompt: small.promptTokens, reused: small.reusedTokens) == .small)
        #expect(s.recent.allSatisfy { $0.finishedAt != nil })
        #expect(s.recent[0].expertHitRate == s.info?.expertHitRate)
    }

    @Test func testTheRunningRequestsBarGetsItsVerdictFromTheSlotsReusedCount() {
        let cases: [(prompt: Double, reused: Double, expected: CacheHit)] =
            [(38964, 19435, .partial), (90494, 0, .miss), (40000, 38000, .hit)]
        for c in cases {
            var rows = Fixture.rows("basalt-0.3.2-prefill.slots.json")
            rows[0]["prompt_tokens"] = c.prompt              // as the engine reports it once it knows the reused part
            rows[0]["reused"] = c.reused
            let live = translate(Fixture.object("basalt-0.3.2-prefill.json"), slots: rows).live
            #expect(CacheHit.classify(prompt: live?.promptTotal ?? 0, reused: live?.reusedEstimate ?? 0) == c.expected)
        }
    }

    @Test func testConversationCacheBudgetIsCarriedOnlyWhenItHasABudget() throws {
        func with(_ engine: String) -> [String: Any] { inline(#"{"engine":"# + engine + #","live":{"state":"idle"}}"#) }
        let on = try #require(translate(with(#"{"conversation_cache_mib":8192,"conversation_cache_slots":4}"#)).info)
        #expect(on.conversationCacheBudgetBytes == 8192.0 * 1_048_576)
        #expect(on.conversationCacheSlots == 4)
        let off = translate(with(#"{"conversation_cache_mib":0,"conversation_cache_slots":4}"#)).info
        #expect(off?.conversationCacheBudgetBytes == nil)
        #expect(off?.conversationCacheSlots == nil)
        #expect(translate(with("{}")).info == nil)
    }

    @Test func testParallelRequestsAreCountedOnlyWithMoreThanOneSlot() {
        func with(_ slots: String) -> [String: Any] {
            inline(#"{"engine":{},"live":{"state":"generating","slots":"# + slots + #","tok_s":50}}"#)
        }
        #expect(translate(with(#"{"total":4,"active":2,"queue_depth":1}"#)).live?.running == 2)
        #expect(translate(with(#"{"total":1,"active":1,"queue_depth":0}"#)).live?.running == nil)
        #expect(translate(with(#"{"total":4,"active":2,"queue_depth":1}"#)).queued == 1)   // queue_depth stands in for queued
    }

    @Test func testMissingFieldsAreUnknownNotZero() {
        let m = inline(#"{"engine":{},"live":{"state":"reading"},"hardware":{"gpus":[null,{"util":"high"}]}}"#)
        let s = translate(m)
        #expect(s.status == .prefill)
        #expect(s.context.used == nil)
        #expect(s.info == nil)
        #expect(s.recent.isEmpty)
        // A video card list whose first entry is not an object leaves every card value unknown.
        let hw = inline(#"{"engine":{},"live":{"state":"idle"},"hardware":{"gpus":[{"util":"high"}]}}"#)
        #expect(translate(hw).system?.gpuPercent == nil)
    }
}
