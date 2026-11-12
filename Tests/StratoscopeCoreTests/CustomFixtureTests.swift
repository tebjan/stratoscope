import Foundation
import Testing
@testable import StratoscopeCore

// The same expectations as Tests/StratoscopeWindowsTests/the Windows custom-server tests, on the same recordings (Tests/Fixtures/custom/README.md).
// Differences by design: the Mac model's context maximum and totals are not optional, so an unknown maximum is 0 and totals are
// given only when the engine reports all four counters. The probe order and the router/direct reads need a fake source, which
// the Mac core does not have; they are covered on Windows only.
@Suite struct CustomParseTests {
    private let reader = CustomReader()

    private func text(_ name: String) -> String { String(decoding: Fixture.data("custom/" + name), as: UTF8.self) }
    private func metrics(_ fixture: String) -> CustomMetrics { CustomMetrics.parse(text(fixture))! }
    private func translate(_ m: CustomMetrics) -> EngineSnapshot {
        reader.translate(m, model: "m")
    }

    @Test func testPrometheusTextKeepsLabelsAndSkipsCommentsAndBadValues() {
        let values = PrometheusText.parse("# HELP x\nq27:a 1\nq27:b{reason=\"kv\"} 2.5 123\nq27:c nan\nother:d 4\nq27:e\n", prefix: "q27:")
        #expect(values.count == 2)
        #expect(values["a"] == 1)
        #expect(values["b{reason=\"kv\"}"] == 2.5)
    }

    @Test func testTheRecordedIdleMetricsAreRead() {
        let m = metrics("custom-idle.metrics.txt")
        #expect(m.running == 0)
        #expect(m.waiting == 0)
        #expect(m.engineSeqs == 4)
        #expect(m.kvFreePages == 3409)
        #expect(m.kvCheckpointOnlyPages == 1)
        #expect(m.prefixCacheCheckpoints == 1)
        #expect(m.prefillTokensTotal == 34)
        #expect(m.generationTokensTotal == 2)
        #expect(m.blockedKv == 0)
        #expect(m.blockedSpeed == 0)
    }

    @Test func testTextWithoutQ27SeriesIsNoCustomDocument() {
        #expect(CustomMetrics.parse(nil) == nil)
        #expect(CustomMetrics.parse("llamacpp:prompt_tokens_total 3\n") == nil)
        #expect(CustomMetrics.parse("{\"status\":\"ok\"}") == nil)
    }

    @Test func testAnIdleEngineIsReadyAndShowsWhatItReports() {
        let s = translate(metrics("custom-idle.metrics.txt"))
        #expect(s.engine == "Custom")
        #expect(s.status == .idle)
        #expect(s.queued == 0)
        #expect(s.live == nil)
        #expect(s.info?.batchSlots == 4)
        #expect(s.info?.kvFreePages == 3409)
        #expect(s.info?.prefixCheckpoints == 1)
        #expect(s.info?.admissionBlocked == "none")
        #expect(s.totals?.promptTokens == 34)
        #expect(s.totals?.outputTokens == 2)
        #expect(s.context.max == 0)                         // the custom server reports no context size
        #expect(s.system == nil)
        #expect(s.recent.isEmpty)
    }

    @Test func testRunningRequestsAreRunningWithoutAPhase() {
        let m = metrics("custom-busy.metrics.txt")
        let running = translate(m)
        #expect(running.status == .running)                     // no per-request phase: neither reading nor writing
        #expect(running.isReading == false)
        #expect(running.live?.running == 3)
        #expect(running.live?.prefillTokensPerSecond == nil)    // no speed is reported
        #expect(running.live?.decodeTokensPerSecond == nil)
        #expect(running.live?.promptRead == nil)                // unknown counters are nil, not 0
        #expect(running.live?.generated == nil)
        #expect(running.queued == 2)
        #expect(running.info?.admissionBlocked == "kv")
        #expect(running.info?.kvCheckpointOnlyPages == 12)
    }

    @Test func testWaitingRequestsOfAnIdleSchedulerOnlyCountInTheQueue() {
        let s = translate(CustomMetrics.parse("q27:num_requests_running 0\nq27:num_requests_waiting 3\n")!)
        #expect(s.status == .idle)
        #expect(s.queued == 3)
        #expect(s.info == nil)                              // nothing else was reported: no section
        #expect(s.totals == nil)
    }

    @Test func testAMissingBlockFlagIsUnknownNotNotBlocked() {
        let s = translate(CustomMetrics.parse("q27:num_requests_running 0\nq27:engine_seqs 4\nq27:admission_blocked{reason=\"kv\"} 0\n")!)
        #expect(s.info?.admissionBlocked == nil)
        #expect(s.info?.kvFreePages == nil)
    }
}

@Suite struct CustomDetectionTests {
    private func text(_ name: String) -> String { String(decoding: Fixture.data("custom/" + name), as: UTF8.self) }

    @Test func testAnUnnamedEngineThatSendsQ27MetricsIsCustom() {
        #expect(CustomReader.recognises(text("custom-idle.metrics.txt")))
        #expect(CustomReader.recognises(text("custom-busy.metrics.txt")))
        #expect(!CustomReader.recognises("llamacpp:prompt_tokens_total 3\n"))
        #expect(!CustomReader.recognises(nil))
    }

    @Test func testAnEngineThatIsNotReadyIsLoadingWithItsOwnWord() {
        #expect(CustomReader.notReady("unloaded") == "State: unloaded")
        #expect(CustomReader.notReady(nil) == "State not reported")
        #expect(CustomReader.notReady("ok") == nil)
        #expect(CustomReader.notReady("OK") == nil)
        let health = try! JSONSerialization.jsonObject(with: Fixture.data("custom/custom-idle.health.json")) as! [String: Any]
        #expect(CustomReader.notReady(health["status"] as? String) == nil)
        #expect(health["service"] == nil)                   // it names no engine, which is why detection reads the metrics
    }
}
