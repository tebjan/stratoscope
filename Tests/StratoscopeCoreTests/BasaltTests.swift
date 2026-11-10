import Foundation
import Testing
@testable import StratoscopeCore

// Numbers from Basalt 0.3.2 payloads recorded read-only behind a llama-swap router (the same documents the Windows tests use):
// the video card is only in hardware.gpus[], without the flat gpu_* fields that a patched engine adds.
@Suite struct BasaltSnapshotTests {
    private func object(_ json: String) -> [String: Any] {
        try! JSONSerialization.jsonObject(with: Data(json.utf8)) as! [String: Any]
    }

    private func metrics(state: String, live: String = "", requests: String = "[]") -> [String: Any] {
        object("""
        {"engine": {"model": "m", "max_context": 131072, "version": "0.3.2", "kv": "int8", "spec": 8, "expert_cache_mib": 17086, "conversation_cache_mib": 0},
         "live": {"state": "\(state)", "load": null, "queued": 2, "slots": {"total": 1, "active": 1, "queue_depth": 2}\(live)},
         "requests": \(requests),
         "hardware": {"gpus": [{"index": 0, "util": 100, "mem_used": 24114204672, "mem_total": 25651314688, "temp": 62}],
                      "cpu": 4.0, "ram_used": 34636144640, "ram_total": 68152971264}}
        """)
    }

    private func slot(_ json: String) -> [[String: Any]] { [object(json)] }

    private let reader = BasaltReader()

    private func snapshot(_ m: [String: Any], slots: [[String: Any]] = []) -> EngineSnapshot {
        reader.snapshot(from: m, slots: slots, model: "m", compactAt: nil, links: [])
    }

    @Test func testBasaltIsRecognisedByItsSlotsObjectAndStrataIsNot() {
        #expect(BasaltReader.recognises(metrics(state: "idle")))
        #expect(!BasaltReader.recognises(object(#"{"engine": {"model": "m"}, "live": {"state": "idle", "slots": []}}"#)))
        #expect(!BasaltReader.recognises(object(#"{"engine": {"model": "m"}, "live": {"state": "idle"}}"#)))
    }

    @Test func testPrefillShowsTheProgressFromSlots() {
        let m = metrics(state: "reading", live: #", "prompt_tokens": 56967, "generated": 0, "elapsed_s": 7.9, "tok_s": null, "prefill_tok_s_mean": 4706.7"#)
        let row = slot(#"{"state": "prefill", "prompt_tokens": 56967, "prefill_done": 33771, "generated": 0, "max_tokens": 32000, "reused": null, "ctx_used": 56967}"#)
        let s = snapshot(m, slots: row)
        #expect(s.status == .prefill)
        #expect(s.queued == 2)
        #expect(s.live?.promptTotal == 56967)
        #expect(s.live?.percent == 59)
        #expect(s.live?.prefillTokensPerSecond == 4706.7)
        #expect(s.live?.decodeTokensPerSecond == nil)
        #expect(s.live?.maxTokens == 32000)
        #expect(s.context.max == 131072)
        #expect(s.system?.gpuPercent == 100)
        #expect(s.system?.cpuPercent == 4)
        #expect(s.cache == nil)
    }

    @Test func testDecodeUsesTheSlotContextAndDropsTheStalePrefillMean() {
        let m = metrics(state: "generating", live: #", "prompt_tokens": 56967, "generated": 993, "elapsed_s": 20.7, "tok_s": 147.2, "prefill_tok_s_mean": 4186.0"#)
        let row = slot(#"{"state": "decoding", "prompt_tokens": 56967, "prefill_done": 56967, "generated": 1002, "max_tokens": 32000, "reused": 0, "ctx_used": 57969}"#)
        let s = snapshot(m, slots: row)
        #expect(s.status == .decode)
        #expect(s.live?.decodeTokensPerSecond == 147.2)
        #expect(s.live?.prefillTokensPerSecond == nil)
        #expect(s.live?.generated == 1002)
        #expect(s.context.used == 57969)
    }

    @Test func testRequestRowsGetTheirCacheVerdictFromPromptAndReused() {
        let requests = """
        [{"prompt_tokens": 90494, "reused": 0, "output_tokens": 634, "prompt_ms": 22098.2, "decode_tok_s": 205.4, "duration_s": 25.2, "hit_rate": 0.92},
         {"prompt_tokens": 38964, "reused": 19435, "output_tokens": 100, "prompt_ms": 3000, "decode_tok_s": 150, "duration_s": 4, "hit_rate": 0.9}]
        """
        let s = snapshot(metrics(state: "idle", requests: requests))
        #expect(s.status == .idle)
        #expect(s.live == nil)
        #expect(CacheHit.classify(prompt: s.recent[0].promptTokens, reused: s.recent[0].reusedTokens) == .miss)
        #expect(CacheHit.classify(prompt: s.recent[1].promptTokens, reused: s.recent[1].reusedTokens) == .partial)
        #expect(s.recent[1].promptRead == 19529)
        #expect(s.context.used == 91128)           // the newest request: prompt + output
    }

    @Test func testTheRunningRequestsBarGetsItsVerdictFromTheSlotsReusedCount() {
        let m = metrics(state: "reading", live: #", "prompt_tokens": 38964, "generated": 0, "prefill_tok_s_mean": 4000"#)
        let row = slot(#"{"state": "prefill", "prompt_tokens": 38964, "prefill_done": 1000, "reused": 19435, "ctx_used": 38964}"#)
        let live = snapshot(m, slots: row).live
        #expect(CacheHit.classify(prompt: live?.promptTotal ?? 0, reused: live?.reusedEstimate ?? 0) == .partial)
    }

    @Test func testAnEngineThatCannotTakeARequestIsLoadingWithItsOwnReason() {
        func status(_ live: String) -> EngineStatus { snapshot(object(#"{"engine": {"max_context": 8192}, "live": "# + live + "}")).status }
        #expect(status(#"{"state": "loading", "load": {"kind": "weights", "percent": 37.4}}"#) == .loading("Loading weights 37 %"))
        #expect(status(#"{"state": "loading", "load": {"kind": "wake"}}"#) == .loading("Waking up"))
        #expect(status(#"{"state": "asleep"}"#) == .loading("Asleep"))
        #expect(status(#"{"state": "unloaded"}"#) == .loading("Weights not loaded"))
        #expect(status("{}") == .loading("State not reported"))
    }
}
