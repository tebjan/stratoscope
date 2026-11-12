import Foundation
import Testing
@testable import StratoscopeCore

// Strata 0.1.56 recordings (Tests/Fixtures/strata-*.json). Each expected value is the value written in the recording.
@Suite struct StrataFixtureTests {
    private func snapshot(_ fixture: String) -> EngineSnapshot {
        var c = Config(); c.log = "/nonexistent.log"
        return StrataProvider(config: c).snapshot(from: Fixture.object(fixture), model: "m")
    }

    @Test func testHardwareGivesGpuTemperaturePowerAndVideoMemory() {
        let s = snapshot("strata-0.1.56-decode.json").system
        #expect(s?.gpuTemperatureCelsius == 48)                 // hardware.gpu_temp
        #expect(s?.gpuPowerWatts == 152.273)                    // hardware.gpu_power
        #expect(s?.gpuPowerLimitWatts == 175)                   // hardware.gpu_power_limit
        #expect(s?.gpuMemoryUsedBytes == 24_911_855_616)        // hardware.gpu_mem_used
        #expect(s?.gpuMemoryTotalBytes == 25_651_314_688)       // hardware.gpu_mem_total
    }

    @Test func testEngineBlockGivesTheEngineInfo() {
        let i = snapshot("strata-0.1.56-decode.json").info
        #expect(i?.version == "0.1.56")                         // engine.version
        #expect(i?.kvFormat == "int8")                          // engine.kv
        #expect(i?.draftWindow == "6")                          // engine.spec is the number 6
        #expect(i?.expertCacheMib == 12470)                     // engine.expert_cache_mib
        #expect(i?.batchSlots == 4)                             // engine.batch_slots
        #expect(i?.expertHitRate == 0.919)                      // requests[0].hit_rate
    }

    @Test func testTheNewestRequestCarriesItsFinishTimeReasonAndPromptTime() {
        let r = snapshot("strata-0.1.56-decode.json").recent.first
        #expect(r?.finishedAt == Date(timeIntervalSince1970: 1794158914.8974314))   // requests[0].time
        #expect(r?.finish == "stop")                                                // requests[0].finish
        #expect(r?.promptMilliseconds == 347.2)                                     // requests[0].prompt_ms
        #expect(r?.expertHitRate == 0.919)                                          // requests[0].hit_rate
    }

    @Test func testBatchSlotsKeepStateTokensAndElapsedTimePerSlot() {
        let s = snapshot("strata-parallel-mixed.json")
        #expect(s.slots?.count == 4)
        #expect(s.slots?[0].state == "decoding")
        #expect(s.slots?[0].promptTokens == 1180)
        #expect(s.slots?[0].generated == 520)
        #expect(s.slots?[0].elapsedSeconds == 5.2)              // live.slots[0].elapsed_s
        #expect(s.slots?[1].promptRead == 2048)
        #expect(s.slots?[1].elapsedSeconds == 2.0)              // live.slots[1].elapsed_s
        #expect(s.slots?[3].state == "idle")
        #expect(s.slots?[3].heldTokens == 2200)                 // live.slots[3].held_tokens
        #expect(s.slots?[3].elapsedSeconds == nil)              // an idle slot reports no elapsed time
        #expect(s.info?.batchSlots == 4)
    }

    @Test func testSeveralRunningSlotsSetTheRunningCount() {
        #expect(snapshot("strata-parallel-mixed.json").live?.running == 3)       // live.running = 3: slots 0, 1 and 2
        #expect(snapshot("strata-parallel-decode.json").live?.running == 4)      // live.running = 4
        #expect(snapshot("strata-0.1.56-decode.json").live?.running == nil)      // one request: no running count
    }
}
