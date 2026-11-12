import Foundation
import Testing
@testable import StratoscopeCore

// The same expectations as Tests/StratoscopeWindowsTests/LlamaCppProviderTests.cs (LlamaCppSnapshotTests), on the same recordings
// (Tests/Fixtures/README.md). Differences by design: the Mac model's context maximum is not optional, so an unknown maximum is 0; the
// counters since the server started are carried in `info` (the Mac `Totals` has a request count llama-server does not report). The probe
// order and the router/direct flows need a fake source, which the Mac core does not have; they are covered on Windows only.
@Suite struct LlamaCppFixtureTests {
    private let reader = LlamaCppReader()

    private func text(_ name: String) -> String { String(decoding: Fixture.data(name), as: UTF8.self) }
    private func slots(_ fixture: String) -> [LlamaSlot] { LlamaCppReader.slots(from: text(fixture))! }
    private func props() -> LlamaProps { LlamaProps(Fixture.object("llamacpp-props.json")) }
    private func metrics(_ fixture: String) -> LlamaCppMetrics { LlamaCppMetrics.parse(text(fixture))! }
    private func translate(_ rows: [LlamaSlot], _ props: LlamaProps?, _ metrics: LlamaCppMetrics?) -> EngineSnapshot {
        reader.translate(rows, props: props, metrics: metrics, model: "m")
    }

    @Test func testARunningSlotWithNothingGeneratedIsReadingAndShowsWhatTheEngineSays() {
        let s = translate(slots("llamacpp-prefill.slots.json"), props(), metrics("llamacpp-prefill.metrics.txt"))
        #expect(s.engine == "llama.cpp")
        #expect(s.status == .prefill)
        #expect(s.queued == 0)
        #expect(s.live?.promptRead == 26889)
        #expect(s.live?.promptTotal == nil)                 // llama-server does not say how long the prompt is
        #expect(s.live?.percent == nil)                     // so there is no percentage, not 0
        #expect(s.live?.prefillTokensPerSecond == nil)      // the gauge is 0 during a long read: unknown here
        #expect(s.live?.decodeTokensPerSecond == nil)       // the gauge is 0 when nothing was generated: unknown, not 0
        #expect(s.live?.generated == 0)
        #expect(s.live?.reusedEstimate == 0)                // the engine counted tokens and reused none: a real 0
        #expect(s.live?.maxTokens == 400)
        #expect(s.live?.running == nil)
        #expect(s.context.used == 26889)
        #expect(s.context.max == 98304)
    }

    @Test func testThePromptSpeedGaugeIsShownWhileReadingAndOnlyWhenItIsAboveZero() {
        let rows = slots("llamacpp-prefill.slots.json")
        let read = translate(rows, props(), LlamaCppMetrics.parse("llamacpp:prompt_tokens_seconds 1423.5\nllamacpp:predicted_tokens_seconds 90\n"))
        #expect(read.live?.prefillTokensPerSecond == 1423.5)
        #expect(read.live?.decodeTokensPerSecond == nil)    // nothing is generated yet: the generation gauge is not this request's
        let idle = translate(slots("llamacpp-idle.slots.json"), props(), metrics("llamacpp-idle.metrics.txt"))
        #expect(idle.live == nil)                           // the gauges still hold the last window while idle: not shown as a speed
    }

    @Test func testASlotThatHasJustStartedHasNoCountsYetAndAnUnlimitedRequestHasNoLimit() {
        let s = translate(slots("llamacpp-prefill-start.slots.json"), props(), nil)
        #expect(s.status == .prefill)
        #expect(s.live?.promptRead == nil)                  // the slot still holds the last conversation until its first batch
        #expect(s.live?.reusedEstimate == nil)              // 0 read and 0 reused at the start is not a cache miss
        #expect(s.live?.maxTokens == nil)                   // -1 means no limit
        #expect(s.context.used == nil)
    }

    @Test func testGeneratedTokensMakeItDecodeWithTheEnginesOwnSpeed() {
        let s = translate(slots("llamacpp-decode.slots.json"), props(), metrics("llamacpp-decode.metrics.txt"))
        #expect(s.status == .decode)
        #expect(s.live?.generated == 316)
        #expect(s.live?.decodeTokensPerSecond == 236.42)
        #expect(s.live?.prefillTokensPerSecond == nil)
        #expect(s.live?.promptRead == nil)
        #expect(s.live?.reusedEstimate == 447)
        #expect(s.context.used == 782)                      // the slot's memory: prompt plus what it has written
        #expect(s.queued == 0)
    }

    @Test func testIdleHasNoRunningRequestAndTheContextOfTheConversationTheSlotHolds() {
        let s = translate(slots("llamacpp-idle.slots.json"), props(), metrics("llamacpp-idle.metrics.txt"))
        #expect(s.status == .idle)
        #expect(s.live == nil)
        #expect(s.slots == nil)                             // one slot has nothing to tell apart
        #expect(s.context.used == 1010)
        #expect(s.context.max == 98304)
        #expect(s.recent.isEmpty)                           // a slot resets its counters when a request ends: nothing to list
    }

    @Test func testSeveralSlotsAreListedAndTheHeadlineIsTheirTotal() {
        let s = translate(slots("llamacpp-parallel-mixed.slots.json"), props(), metrics("llamacpp-decode.metrics.txt"))
        let list = s.slots!
        #expect(s.status == .decode)                        // generating while any slot decodes
        #expect(list.map(\.state) == ["decoding", "reading", "idle", "idle"])
        #expect(list.map(\.index) == [0, 1, 2, 3])
        #expect(s.live?.running == 2)
        #expect(s.live?.isParallel == true)
        #expect(s.live?.generated == 378)
        #expect(s.live?.maxTokens == nil)                   // one limit per request, not for the total
        #expect(s.live?.reusedEstimate == nil)
        #expect(list[0].maxTokens == 4096)
        #expect(list[1].maxTokens == nil)                   // no limit
        #expect(list[1].promptRead == 5120)
        #expect(list[1].promptTokens == nil)                // the whole prompt is not known
        #expect(list[2].heldTokens == 18000)                // an idle slot says what it holds
        #expect(list[2].isActive == false)
        #expect(list[3].heldTokens == nil)                  // a slot that never ran has no task and says nothing
        #expect(s.context.used == 9321)                     // the fullest running slot
        #expect(s.context.max == 32768)
    }

    @Test func testTotalsAreTheCountersSinceTheServerStartedAndTheCacheShareComesFromThem() {
        let info = translate(slots("llamacpp-idle.slots.json"), props(), metrics("llamacpp-decode.metrics.txt")).info!
        #expect(info.totalPromptTokens == 29304 + 53713)    // what was read plus what was reused
        #expect(info.totalReusedTokens == 53713)
        #expect(info.totalOutputTokens == 2343)
        #expect(abs(info.totalPromptMilliseconds! - 18536.5) < 1e-6)
        #expect(abs(info.totalDecodeMilliseconds! - 9880.73) < 1e-6)
    }

    @Test func testWithoutMetricsAndPropsOnlyWhatSlotsSayIsShown() {
        let s = translate(slots("llamacpp-decode.slots.json"), nil, nil)
        #expect(s.status == .decode)
        #expect(s.live?.decodeTokensPerSecond == nil)
        #expect(s.info == nil)
        #expect(s.queued == 0)
        #expect(s.context.max == 98304)                     // every slot says its context size
    }

    @Test func testTheBuildAndTheSlotCountComeFromProps() {
        let info = translate(slots("llamacpp-idle.slots.json"), props(), nil).info!
        #expect(info.version == "b1-d493f84")
        #expect(info.batchSlots == 1)
        #expect(info.kvFormat == nil)
        #expect(info.totalPromptTokens == nil)              // no metrics: no totals
    }

    @Test func testMissingFieldsAreUnknownNotZero() {
        let rows = LlamaCppReader.slots(from: "[{\"is_processing\": true, \"n_prompt_tokens\": \"many\", \"next_token\": \"x\"}, null]")!
        #expect(rows.count == 1)                            // a row that is no object is dropped
        let s = translate(rows, nil, nil)
        #expect(s.status == .prefill)
        #expect(s.live?.promptRead == nil)
        #expect(s.live?.generated == nil)                   // the slot did not say: not 0
        #expect(s.live?.reusedEstimate == nil)
        #expect(s.context.used == nil)
        #expect(s.context.max == 0)                         // the Mac model's 0 for "not said"
    }

    @Test func testThePrometheusTextIsReadByNameAndOnlyFiniteNumbersCount() {
        let m = LlamaCppMetrics.parse("# HELP llamacpp:x y\nllamacpp:prompt_tokens_seconds 12.5\r\nllamacpp:predicted_tokens_seconds nan\nllamacpp:requests_deferred{slot=\"1\"} 4\nllamacpp:requests_processing 3 1700000000\nother:prompt_tokens_total 9\n")!
        #expect(m.promptTokensPerSecond == 12.5)
        #expect(m.predictedTokensPerSecond == nil)
        #expect(m.requestsDeferred == nil)                  // labelled series are not read
        #expect(m.requestsProcessing == 3)                  // a timestamp after the value is ignored
        #expect(m.promptTokensTotal == nil)                 // another prefix is another program's metric
        #expect(LlamaCppMetrics.parse("# nothing\nprocess_cpu_seconds_total 1\n") == nil)
        #expect(LlamaCppMetrics.parse("") == nil)
        #expect(LlamaCppMetrics.parse(nil) == nil)
    }

    @Test func testOnlyALlamaServersPropsAreRecognised() {
        func isLlama(_ json: String) -> Bool {
            LlamaProps(try! JSONSerialization.jsonObject(with: Data(json.utf8)) as! [String: Any]).isLlamaServer
        }
        #expect(props().isLlamaServer)
        #expect(!isLlama("{\"status\":\"ok\"}"))
        #expect(!isLlama("{\"total_slots\":\"4\"}"))
        #expect(!isLlama("{\"default_generation_settings\":{\"n_ctx\":8192}}"))
    }

    @Test func testASleepingServerAndAServerWithoutMetricsAreRecognisedFromProps() {
        func parsed(_ json: String) -> LlamaProps {
            LlamaProps(try! JSONSerialization.jsonObject(with: Data(json.utf8)) as! [String: Any])
        }
        let asleep = parsed("{\"total_slots\":1,\"build_info\":\"b1\",\"is_sleeping\":true}")
        #expect(asleep.isLlamaServer)
        #expect(asleep.sleeping)                            // /slots is not asked: that would wake it
        let plain = parsed("{\"total_slots\":1,\"build_info\":\"b1\",\"endpoint_metrics\":false}")
        #expect(plain.metricsDisabled)                      // /metrics is not asked
        #expect(!props().metricsDisabled)
        #expect(!props().sleeping)
    }

    @Test func testADocumentThatIsNoSlotListIsNotRead() {
        #expect(LlamaCppReader.slots(from: "{\"x\":1}") == nil)
        #expect(LlamaCppReader.slots(from: "not json") == nil)
    }
}
