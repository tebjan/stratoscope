import Foundation
import Testing
@testable import StratoscopeCore

@Suite struct SpeedScaleTests {
    @Test func testARiseAddsAMarginAndRoundsUpAtOnce() {
        for (value, expected) in [(120.0, 130.0), (60, 63), (1200, 1300), (283, 300)] {
            var scale = SpeedScale(initialMax: 20)
            scale.observe(value, now: 0)
            #expect(scale.max == expected, "\(value)")
        }
    }

    @Test func testTheMaximumDoesNotMoveWhileValuesStayBelowIt() {
        var scale = SpeedScale(initialMax: 100)
        for i in 0..<100 { #expect(scale.observe(60 + Double(i % 7) * 5, now: Double(i)) == 100) }
    }

    @Test func testAValueAboveTheMaximumRaisesItOnce() {
        var scale = SpeedScale(initialMax: 100)
        scale.observe(120, now: 0)
        #expect(scale.max == 130)
        for v in [121.0, 125, 128, 126, 129] { #expect(scale.observe(v, now: 1) == 130) }
        #expect(scale.observe(135, now: 2) == 150)
    }

    @Test func testTheMaximumStepsBackOnlyAfterTheHoldTimeAndNeverBelowTheFloor() {
        var scale = SpeedScale(initialMax: 20)
        scale.observe(400, now: 0)
        #expect(scale.max == 420)
        var t = 1.0
        while t < SpeedScale.fallHoldSeconds { #expect(scale.observe(100, now: t) == 420); t += 1 }
        #expect(scale.observe(100, now: SpeedScale.fallHoldSeconds + 1) == 110)
        var now = SpeedScale.fallHoldSeconds + 1
        for _ in 0..<100 { now += 1; scale.observe(0, now: now) }
        #expect(scale.max == 20)
    }

    @Test func testASteppedDownScaleDoesNotOscillate() {
        var scale = SpeedScale(initialMax: 20)
        scale.observe(400, now: 0)
        var seen = Set<Double>()
        for i in 1...120 { seen.insert(scale.observe(100, now: Double(i))) }
        #expect(seen == [420, 110])
    }

    @Test func testTheMaximumHasTwoSignificantDigits() {
        for (value, expected) in [(5.1, 5.1), (12.01, 13), (99, 99), (101, 110), (126, 130), (283, 290), (1260, 1300)] {
            #expect(abs(SpeedScale.roundUp(value) - expected) < 1e-9, "\(value)")
        }
    }

    @Test func testTheChartScalesOfTheSpeedSectionFollowTheSameRule() {
        var h = SpeedHistory()
        var s = EngineSnapshot(engine: "Strata", model: "m", status: .idle, context: ContextUsage(max: 0))
        s.recent = [RequestStats(decodeTokensPerSecond: 30)]
        h.add(now: Date(timeIntervalSince1970: 1000), s)
        #expect(h.decodeChartScale.max == 32)
        s.recent = [RequestStats(decodeTokensPerSecond: 28)]
        h.add(now: Date(timeIntervalSince1970: 1002), s)
        #expect(h.decodeChartScale.max == 32)
    }

    @Test func testUnknownValuesAreIgnored() {
        var scale = SpeedScale(initialMax: 100)
        #expect(scale.observe(.nan, now: 0) == 100)
        #expect(scale.observe(.infinity, now: 1) == 100)
        #expect(scale.observe(-5, now: 2) == 100)
        #expect(scale.fraction(.nan) == 0)
    }
}

@Suite struct OnePoleFilterTests {
    @Test func testTheSettingsHaveTheirDamping() {
        #expect(HistorySmoothing.off.damping == 0)
        #expect(HistorySmoothing.normal.damping == 0.5)
        #expect(HistorySmoothing.high.damping == 0.9)
        #expect(HistorySmoothing.highest.damping == 0.95)
    }

    @Test func testItFollowsYEqualsDYPlusOneMinusDX() {
        var f = OnePoleFilter(damping: 0.5)
        #expect(f.step(10) == 10)
        #expect(abs(f.step(20) - 15) < 1e-9)
        #expect(abs(f.step(0) - 7.5) < 1e-9)
    }

    @Test func testAnUnknownValueBreaksTheLineAndRestartsTheFilter() {
        var f = OnePoleFilter(damping: 0.9)
        _ = f.step(100)
        #expect(f.step(.nan).isNaN)
        #expect(f.step(40) == 40)
    }
}

@Suite struct SpeedHistoryTests {
    @Test func testTheBufferKeepsTheLastTwoMinutesAtTheSampleSpacing() {
        var h = SpeedHistory()
        #expect(SpeedHistory.windowSeconds == 120)
        for k in 0...600 { h.addSample(now: Double(k) * SpeedHistory.sampleSeconds, prefill: 10, decode: 20) }
        #expect(h.sampleCount == Int(SpeedHistory.windowSeconds / SpeedHistory.sampleSeconds) + 1)
        let samples = h.readSmoothed(.decode, damping: 0, now: 300)
        #expect(samples.count == h.sampleCount)
        #expect(samples.first?.age == 120)
        #expect(samples.last?.age == 0)
        #expect(abs((samples[samples.count - 2].age - samples[samples.count - 1].age) - SpeedHistory.sampleSeconds) < 1e-9)
        #expect(samples.allSatisfy { $0.value == 20 })
    }

    @Test func testTheTotalIsPromptPlusGeneratedAndUnknownWhenEitherIs() {
        var h = SpeedHistory()
        h.addSample(now: 0, prefill: 100, decode: 30)
        #expect(h.latest(.total) == 130)
        h.addSample(now: 2, prefill: .nan, decode: 30)
        #expect(h.latest(.total).isNaN)
        #expect(h.latest(.decode) == 30)
    }

    @Test func testTheCurvesAreSmoothedWhenReadAndRawInside() {
        var h = SpeedHistory()
        h.addSample(now: 0, prefill: 0, decode: 0)
        h.addSample(now: 2, prefill: 0, decode: 100)
        #expect(h.latest(.decode) == 100)
        #expect(abs(h.readSmoothed(.decode, damping: HistorySmoothing.normal.damping, now: 2)[1].value - 75) < 1e-9)     // 0.5 per second, 2 s apart: d = 0.25
        #expect(h.readSmoothed(.decode, damping: 0, now: 2)[1].value == 100)
    }

    @Test func testAnIdleEngineIsZeroAndAnOfflineOneIsUnknown() {
        let idle = EngineSnapshot(engine: "Strata", model: "m", status: .idle, context: ContextUsage(max: 0))
        #expect(SpeedHistory.rates(idle).prefill == 0 && SpeedHistory.rates(idle).decode == 0)
        #expect(SpeedHistory.rates(nil).prefill.isNaN)
    }
}

@Suite struct RequestBarTests {
    private func slot(_ i: Int, _ state: String, read: Double? = nil, total: Double? = nil, tps: Double? = nil) -> BatchSlot {
        BatchSlot(index: i, state: state, promptTokens: total, promptRead: read, tokensPerSecond: tps)
    }

    @Test func testOneRequestIsOneBar() {
        var s = EngineSnapshot(engine: "Strata", model: "m", status: .prefill, live: LiveRequest(promptRead: 500, promptTotal: 1000), context: ContextUsage(max: 0))
        #expect(RequestBars.from(s) == [RequestBar(reading: true, fraction: 0.5)])
        s.status = .decode; s.live = LiveRequest(decodeTokensPerSecond: 26)
        #expect(RequestBars.from(s) == [RequestBar(reading: false, tokensPerSecond: 26)])
    }

    @Test func testParallelRequestsAreOneBarEachAndAtMostEight() {
        let slots = [slot(0, "decoding", tps: 30), slot(1, "reading", read: 100, total: 400), slot(2, "idle"), slot(3, "decoding", tps: 25)]
        let s = EngineSnapshot(engine: "Strata", model: "m", status: .decode, live: LiveRequest(running: 3), context: ContextUsage(max: 0), slots: slots)
        let bars = RequestBars.from(s)
        #expect(bars.count == 3)
        #expect(bars[1] == RequestBar(reading: true, fraction: 0.25))
        #expect(RequestBars.peakSpeed(bars) == 30)
        let many = (0..<12).map { slot($0, "decoding", tps: 10) }
        #expect(RequestBars.from(EngineSnapshot(engine: "Strata", model: "m", status: .decode, live: LiveRequest(running: 12), context: ContextUsage(max: 0), slots: many)).count == 8)
    }

    @Test func testTheStackGrowsWithTheCountWhileBarsGetThinner() {
        for single in [12, 18, 24] {
            let one = RequestBars.rows(count: 1, singlePixels: single, capPixels: single * 3)
            #expect(one.total == single && one.bar == single)
            var previous = one
            for n in 2...8 {
                let r = RequestBars.rows(count: n, singlePixels: single, capPixels: single * 3)
                #expect(r.bar >= 2 && r.gap >= 1)
                #expect(r.total == n * r.bar + r.extra + (n - 1) * r.gap)
                #expect(r.total <= single * 3)
                #expect(r.total >= previous.total)
                #expect(r.height(of: 0) <= previous.height(of: 0))
                #expect(r.height(of: 0) < single)
                #expect(r.total < single * n)
                previous = r
            }
        }
    }

    @Test func testTheWindowAverageIsOfTheRawSamplesAndSkipsUnknownOnes() {
        var h = SpeedHistory()
        h.addSample(now: 0, prefill: 0, decode: 0)
        h.addSample(now: 100, prefill: 10, decode: 100)
        h.addSample(now: 110, prefill: 20, decode: 200)
        h.addSample(now: 121, prefill: .nan, decode: 300)
        #expect(h.average(.prefill, now: 121) == 15)
        #expect(h.average(.decode, now: 121) == 200)
        #expect(h.average(.total, now: 121) == 165)
        #expect(SpeedHistory().average(.total, now: 0).isNaN)
    }
}

@Suite struct CounterRateTests {
    @Test func testPrefillAndDecodeComeFromTheCounterDeltasAndTotalIsTheirSum() {
        var h = SpeedHistory()
        #expect(!h.addCounters(now: Date(timeIntervalSince1970: 0), prefillComputed: 1000, generated: 5000))
        #expect(h.addCounters(now: Date(timeIntervalSince1970: 0.5), prefillComputed: 2200, generated: 5100))
        #expect(abs(h.latest(.prefill) - 2400) < 1e-9)
        #expect(abs(h.latest(.decode) - 200) < 1e-9)
        #expect(abs(h.latest(.total) - 2600) < 1e-9)
    }

    @Test func testACounterThatFallsIsAGapAndMissingCountersAddNothing() {
        var h = SpeedHistory()
        h.addCounters(now: Date(timeIntervalSince1970: 0), prefillComputed: 1000, generated: 5000)
        #expect(h.addCounters(now: Date(timeIntervalSince1970: 0.5), prefillComputed: 20, generated: 30))
        #expect(h.latest(.total).isNaN)
        #expect(!h.addCounters(now: Date(timeIntervalSince1970: 1), prefillComputed: nil, generated: 5))
        #expect(h.addCounters(now: Date(timeIntervalSince1970: 1.5), prefillComputed: 120, generated: 130))
        #expect(abs(h.latest(.prefill) - 200) < 1e-9)
    }

    @Test func testTheDampingIsPerSecondAtAnySampleRate() {
        for damping in [0.5, 0.9, 0.95] {
            func after(_ spacing: Double) -> Double {
                var f = OnePoleFilter(damping: damping)
                _ = f.step(0)
                var y = 0.0, t = spacing
                while t <= 2.0 + 1e-9 { y = f.step(100, dt: spacing); t += spacing }
                return y
            }
            let expected = 100 * (1 - pow(damping, 2))
            for spacing in [2.0, 0.5, 0.1] { #expect(abs(after(spacing) - expected) < 1e-6) }
        }
    }
}
