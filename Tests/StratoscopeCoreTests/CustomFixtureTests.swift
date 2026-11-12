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

// Per-request reading and writing from /ui/state. custom-ui-state-busy.json is hand-written after the engine's source (worker.py, stats.py),
// not captured from a live request. Its requests: r1 running prefill (prompt 6000, fed 2400, computed 2400, prefill_s 3.0), r2 running decode
// (out 300, max_new 2048, decode_s 10.0), r3 and r5 waiting, r4 running prefill with prefill_s 0.
@Suite struct CustomUiStateTests {
    private let reader = CustomReader()

    private func text(_ name: String) -> String { String(decoding: Fixture.data("custom/" + name), as: UTF8.self) }
    private func busy() -> CustomMetrics { CustomMetrics.parse(text("custom-busy.metrics.txt"))! }
    private func seqs(_ rids: String...) -> [CustomSeq] {
        let all = CustomSeq.parse(text("custom-ui-state-busy.json"))!
        return rids.isEmpty ? all : all.filter { rids.contains($0.rid ?? "") }
    }
    private func translate(_ s: [CustomSeq]?) -> EngineSnapshot { reader.translate(busy(), model: "m", seqs: s) }

    @Test func testAPrefillSeqIsReadingWithFedAndPromptAndItsSpeed() {
        let s = translate(seqs("r1"))
        #expect(s.status == .prefill)
        #expect(s.live?.promptRead == 2400)                 // fed
        #expect(s.live?.promptTotal == 6000)                // prompt
        #expect(s.live?.percent == 40)
        #expect(s.live?.prefillTokensPerSecond == 800)      // computed 2400 / prefill_s 3.0
        #expect(s.live?.decodeTokensPerSecond == nil)
    }

    @Test func testADecodeSeqIsWritingWithOutAndMaxNewAndItsSpeed() {
        let s = translate(seqs("r2"))
        #expect(s.status == .decode)
        #expect(s.live?.generated == 300)                   // out
        #expect(s.live?.maxTokens == 2048)                  // max_new
        #expect(s.live?.decodeTokensPerSecond == 30)        // out 300 / decode_s 10.0
        #expect(s.live?.prefillTokensPerSecond == nil)
    }

    @Test func testWaitingSeqsAreQueuedAndNotBusy() {
        let s = translate(seqs())
        #expect(s.queued == 2)                              // r3 and r5
        #expect(s.live?.running == 3)                       // r1, r2, r4 run; the waiting ones do not
        #expect(s.status == .decode)                        // r2 writes
        #expect(translate(seqs("r3")).status == .running)   // nothing in the list runs: the aggregate state stays
    }

    @Test func testParkedAndAdmittedCountAsRunningWithoutAPhase() {
        let s = translate([CustomSeq(state: "parked", phase: "decode"), CustomSeq(state: "admitted", phase: "prefill")])
        #expect(s.status == .running)
        #expect(s.live?.running == 2)
        #expect(s.live?.promptRead == nil)
        #expect(s.live?.generated == nil)
    }

    @Test func testASeqWithZeroTimeHasNoSpeed() {
        let s = translate(seqs("r4"))
        #expect(s.status == .prefill)
        #expect(s.live?.promptRead == 0)                    // the engine says fed 0
        #expect(s.live?.prefillTokensPerSecond == nil)      // prefill_s 0: no speed
    }

    @Test func testAnEngineWithoutUiStateStaysRunning() {
        let s = translate(nil)
        #expect(s.status == .running)
        #expect(s.live?.promptRead == nil)
        #expect(s.live?.generated == nil)
        #expect(CustomSeq.parse("<html>") == nil)
    }
}

// The whole /ui/state: custom-ui-state-idle.json is a live capture (RTX 5090 Laptop GPU, idle after two short requests). Expected values are
// read from that file: tick.sample, tick.vram.parts ("free", "KV cache"), tick.cache, tick.sched and info.
@Suite struct CustomUiStateFullTests {
    private let reader = CustomReader()

    private func text(_ name: String) -> String { String(decoding: Fixture.data("custom/" + name), as: UTF8.self) }
    private func idle() -> EngineSnapshot {
        let ui = CustomUiState.parse(text("custom-ui-state-idle.json"))!
        return reader.translate(CustomMetrics.parse(text("custom-idle.metrics.txt"))!, model: "m", ui: ui)
    }

    @Test func testTheVideoCardComesFromTheSampleAndTheVramParts() {
        let sys = idle().system
        #expect(sys?.gpuPercent == 0)                           // sample.util
        #expect(sys?.gpuMemoryBusyPercent == 0)                 // sample.mem_util
        #expect(sys?.gpuTemperatureCelsius == 34)               // sample.temp
        #expect(sys?.gpuPowerWatts == 7.178)                    // sample.power
        #expect(sys?.gpuPowerLimitWatts == 175)                 // sample.power_limit
        #expect(sys?.gpuMemoryTotalBytes == 25651314688)        // vram.total
        #expect(sys?.gpuMemoryUsedBytes == 25651314688 - 2007592960)   // total minus the "free" part
        #expect(sys?.ramUsedBytes == nil)                       // RAM and CPU are not in /ui/state
        #expect(sys?.cpuPercent == nil)
    }

    @Test func testTheKvCacheIsTheKvPartOfTheVideoMemory() {
        let cache = idle().cache
        #expect(cache?.budgetBytes == 4034396160)               // vram.parts "KV cache"
        #expect(cache?.usedBytes == 4034396160 - 4022599680)    // minus its own "free"
        #expect(cache?.requests == 2)                           // cache.lookups
        #expect(cache?.requestsReused == 0)                     // cache.hits
        #expect(cache?.evictions == 0)                          // cache.evicted
        #expect(cache?.slots == 0)
    }

    @Test func testTheEngineLineAndTheContextMaximum() {
        let s = idle()
        #expect(s.info?.gpuName == "NVIDIA GeForce RTX 5090 Laptop GPU")   // info.gpu
        #expect(s.info?.draftWindow == "DFlash2")               // info.spec true
        #expect(s.info?.kvFormat == "nvfp4")                    // info.kv_dtype
        #expect(s.info?.targetSeqs == 4)                        // tick.sched.target_seqs
        #expect(s.info?.batchSlots == 4)                        // metrics engine_seqs
        #expect(s.context.max == 98304)                         // info.max_tokens
        #expect(s.status == .idle)                              // tick.engine "ready"
    }

    @Test func testIdleSlotsAreFilledUpToTheEngineSlotCount() {
        let slots = idle().slots
        #expect(slots?.count == 4)
        #expect(slots?.allSatisfy { $0.state == "idle" } == true)
    }

    @Test func testFinishedRequestsAreTheDoneEventsOfTheLog() {
        let s = idle()
        #expect(!s.recent.isEmpty)
        #expect(s.recent.count <= 20)
        #expect(s.recent.allSatisfy { $0.finish != nil && $0.finishedAt != nil })
        #expect(s.context.used == s.recent[0].contextTokens)    // idle: the newest finished request
    }

    @Test func testRunningSeqsGiveSlotsAndTheContextOfTheFullestOne() {
        let ui = CustomUiState.parse(text("custom-ui-state-busy.json"))!
        let s = reader.translate(CustomMetrics.parse(text("custom-busy.metrics.txt"))!, model: "m", ui: ui)
        #expect(s.status == .decode)
        #expect(s.slots?.count == 4)                            // 3 running seqs and one idle row up to engine_seqs
        #expect(s.slots?[0].state == "reading")
        #expect(s.slots?[1].state == "decoding")
        #expect(s.slots?[1].tokensPerSecond == 30)              // out 300 / decode_s 10.0
        #expect(s.slots?[3].state == "idle")
        #expect(s.context.used == 2400)                         // fed + out: r1 2400, r2 1000 + 300, r4 0
    }

    @Test func testAnEngineThatIsRestartingIsLoading() {
        var ui = CustomUiState()
        ui.engineState = "restarting"
        let s = reader.translate(CustomMetrics.parse(text("custom-idle.metrics.txt"))!, model: "m", ui: ui)
        #expect(s.isLoading)
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
