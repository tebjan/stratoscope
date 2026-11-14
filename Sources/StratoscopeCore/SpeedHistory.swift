import Foundation

/// The curves of the history graph.
public enum HistoryCurve: Int { case prefill = 0, decode, total }

/// One running request as the menu bar bar shows it. `reading`: the bar is its prompt progress (`fraction`); otherwise its own decode speed.
public struct RequestBar: Equatable {
    public var reading: Bool
    public var fraction: Double
    public var tokensPerSecond: Double?
    public init(reading: Bool, fraction: Double = 0, tokensPerSecond: Double? = nil) {
        self.reading = reading; self.fraction = fraction; self.tokensPerSecond = tokensPerSecond
    }
}

/// The per-request bars of the menu bar item. One bar per running request, at most `maxBars`. Source: the batch slots the engine reports.
/// A single request, or an engine without slots, is one bar from the live request. The Swift twin of RequestBars in SpeedHistory.cs.
public enum RequestBars {
    public static let maxBars = 8

    public static func from(_ s: EngineSnapshot?) -> [RequestBar] {
        guard let s = s, s.isBusy else { return [] }
        let active = (s.slots ?? []).filter { $0.isReading || $0.isDecoding }
        if active.count > 1 {
            return active.prefix(maxBars).map { slot in
                if slot.isReading {
                    let f = (slot.promptTokens ?? 0) > 0 ? Swift.min(1, Swift.max(0, (slot.promptRead ?? 0) / (slot.promptTokens ?? 1))) : 0
                    return RequestBar(reading: true, fraction: f)
                }
                return RequestBar(reading: false, tokensPerSecond: slot.tokensPerSecond)
            }
        }
        switch s.status {
        case .prefill: return [RequestBar(reading: true, fraction: Double(s.live?.percent ?? 0) / 100)]
        case .thinking, .decode: return [RequestBar(reading: false, tokensPerSecond: s.live?.decodeTokensPerSecond)]
        default: return []
        }
    }

    /// The largest decode speed among the bars, or nil when none is known. What the speed scale is fed.
    public static func peakSpeed(_ bars: [RequestBar]) -> Double? {
        bars.compactMap { $0.reading ? nil : $0.tokensPerSecond }.filter { $0.isFinite }.max()
    }

    /// The gap between two bars as a share of the bar height they would have without gaps (never under one device pixel).
    public static let gapShare = 0.25

    /// The bars of a stack, in device pixels: every bar is `bar` high, the first `extra` of them one pixel more; `gap` between two.
    public struct Stack: Equatable {
        public var bar: Int, gap: Int, total: Int, extra: Int
        public func height(of i: Int) -> Int { bar + (i < extra ? 1 : 0) }
        public func top(of i: Int) -> Int { i * (bar + gap) + Swift.min(i, extra) }
    }

    /// The rows of `count` bars in device pixels (twin of BarRows in ReadoutPainter.cs). One bar keeps `singlePixels`. With several, the stack
    /// height grows with the square root of the count, `H(n) = H1 * sqrt(n)`, capped at `capPixels`, so it grows less than the count while every
    /// bar gets thinner. The gap is a quarter of the bar height (at least one pixel); the bar is what is left, never under two pixels (then the
    /// stack grows to fit). The stack is exactly the rounded H(n) high: leftover pixels make the first bars one pixel taller.
    public static func rows(count: Int, singlePixels: Int, capPixels: Int) -> Stack {
        if count <= 1 { return Stack(bar: singlePixels, gap: 0, total: singlePixels, extra: 0) }
        let height = Swift.min(Double(singlePixels) * Double(count).squareRoot(), Double(capPixels))
        let gap = Swift.max(1, Int((height / Double(count) * gapShare + 0.5).rounded(.down)))
        let total = Swift.min(capPixels, Swift.max(2 * count + gap * (count - 1), Int((height + 0.5).rounded(.down))))
        let room = total - gap * (count - 1)
        return Stack(bar: room / count, gap: gap, total: total, extra: room % count)
    }
}

/// The last 60 seconds of speed, for the history graph and the adaptive scales. One sample per poll. The Swift twin of SpeedHistory.cs.
/// Curves (tokens per second, from the engine's own numbers; unknown is NaN and breaks the line):
///  - prefill: `live.prefillTokensPerSecond`, prompt tokens per second.
///  - decode: `live.decodeTokensPerSecond`, generated tokens per second.
///  - total: tokens processed per second through the system, prompt plus generated, the throughput meaning of vLLM
///    (vllm:prompt_tokens_total + vllm:generation_tokens_total as a rate) and of OpenTelemetry GenAI token usage
///    (gen_ai.client.token.usage, input and output tokens). The sum of the two curves; unknown when either is unknown.
/// While the engine is idle all three are 0. One request that reads does not write, so an unreported decode speed is 0 then, and the other
/// way round; with parallel requests an unreported speed stays unknown. Samples are raw; smoothing is applied when the graph reads them.
public struct SpeedHistory {
    public static let windowSeconds = 60.0
    public static let capacity = 1024

    private var times: [Double] = []
    private var values: [[Double]] = [[], [], []]
    private var start = 0, count = 0
    /// The scale of the per-request bars in the menu bar item.
    public var requestScale = SpeedScale()
    /// The scale of the history graph (the largest of the three curves).
    public var graphScale = SpeedScale()
    /// The scales of the bar charts of the Speed section (the speed of the last finished requests): prefill from 100 t/s, decode from 10 t/s.
    public var prefillChartScale = SpeedScale(initialMax: 100)
    public var decodeChartScale = SpeedScale(initialMax: 10)

    public init() {
        times = Array(repeating: 0, count: SpeedHistory.capacity)
        values = Array(repeating: Array(repeating: 0, count: SpeedHistory.capacity), count: 3)
    }

    public var sampleCount: Int { count }

    /// Records the state of one poll. A nil snapshot (nothing answers) records unknown values.
    public mutating func add(now: Date, _ s: EngineSnapshot?) {
        let (prefill, decode) = SpeedHistory.rates(s)
        let seconds = now.timeIntervalSince1970
        addSample(now: seconds, prefill: prefill, decode: decode)
        if let peak = RequestBars.peakSpeed(RequestBars.from(s)) { requestScale.observe(peak, now: seconds) }
        if let s = s, !s.recent.isEmpty {
            // The charts of the finished requests share the rule of the other scales: they do not change with every poll.
            let recent = s.recent.prefix(12)
            decodeChartScale.observe(recent.map { $0.decodeTokensPerSecond ?? 0 }.max() ?? 0, now: seconds)
            prefillChartScale.observe(recent.map { $0.prefillTokensPerSecond(minTokens: 1500, minMilliseconds: 2000) ?? 0 }.max() ?? 0, now: seconds)
        }
    }

    /// The prefill and decode speed of a snapshot as the curves take them.
    public static func rates(_ s: EngineSnapshot?) -> (prefill: Double, decode: Double) {
        guard let s = s, !s.isLoading else { return (.nan, .nan) }
        if s.status == .idle { return (0, 0) }
        func p(_ v: Double?) -> Double { v.flatMap { $0.isFinite ? $0 : nil } ?? .nan }
        var prefill = p(s.live?.prefillTokensPerSecond), decode = p(s.live?.decodeTokensPerSecond)
        if s.live?.isParallel != true {
            if decode.isNaN && s.status == .prefill { decode = 0 }
            if prefill.isNaN && (s.status == .decode || s.status == .thinking) { prefill = 0 }
        }
        return (prefill, decode)
    }

    /// Adds one sample; the total is prefill plus decode. Samples older than `windowSeconds` are dropped.
    public mutating func addSample(now: Double, prefill: Double, decode: Double) {
        while count > 0 && now - times[start] > SpeedHistory.windowSeconds { start = (start + 1) % SpeedHistory.capacity; count -= 1 }
        if count == SpeedHistory.capacity { start = (start + 1) % SpeedHistory.capacity; count -= 1 }
        let i = (start + count) % SpeedHistory.capacity
        let total = prefill.isNaN || decode.isNaN ? Double.nan : prefill + decode
        times[i] = now
        values[0][i] = prefill; values[1][i] = decode; values[2][i] = total
        count += 1
        func known(_ v: Double) -> Double { v.isNaN ? 0 : v }
        graphScale.observe(Swift.max(known(total), Swift.max(known(prefill), known(decode))), now: now)
    }

    /// One curve smoothed with the one-pole filter of `damping`: for each sample its age in seconds (0 = now) and its value (NaN where unknown).
    public func readSmoothed(_ curve: HistoryCurve, damping: Double, now: Double) -> [(age: Double, value: Double)] {
        var filter = OnePoleFilter(damping: damping)
        var out: [(age: Double, value: Double)] = []
        out.reserveCapacity(count)
        for k in 0..<count {
            let i = (start + k) % SpeedHistory.capacity
            let age = now - times[i]
            if age > SpeedHistory.windowSeconds { continue }
            out.append((Swift.max(0, age), filter.step(values[curve.rawValue][i])))
        }
        return out
    }

    /// The average of the raw samples of a curve over the last `windowSeconds` (smoothing not applied), over the samples with a known value;
    /// NaN when there is none. The legend of the history graph shows it.
    public func average(_ curve: HistoryCurve, now: Double) -> Double {
        var sum = 0.0, n = 0
        for k in 0..<count {
            let i = (start + k) % SpeedHistory.capacity
            let v = values[curve.rawValue][i]
            if now - times[i] > SpeedHistory.windowSeconds || v.isNaN { continue }
            sum += v; n += 1
        }
        return n == 0 ? .nan : sum / Double(n)
    }

    /// The newest raw value of a curve, or NaN when there is none.
    public func latest(_ curve: HistoryCurve) -> Double {
        count == 0 ? .nan : values[curve.rawValue][(start + count - 1) % SpeedHistory.capacity]
    }
}
