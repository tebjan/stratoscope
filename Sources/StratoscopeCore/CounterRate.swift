import Foundation

/// The rate of one cumulative counter, read every `SpeedHistory.sampleSeconds`. The Swift twin of `CounterRate.cs`.
///
/// Some engines publish a counter in steps, not per token: the custom 27B engine's `q27:generation_tokens_total` rises by about a second's
/// worth of tokens once per second. The difference of two neighbouring reads then alternates between 0 and twice the real speed (a ping-pong
/// in the curve, whatever the smoothing is). So the rate is taken between change events (the reads at which the counter was seen to rise),
/// over a span of about two of the engine's own publishing intervals, and held until the next step is due.
///
/// Typical interval: the median of the last intervals between change events. Span: two typical intervals, between 1 and 3 seconds. Hold: one
/// and a half intervals, between 1 and 3 seconds; after it the rate is 0. A step after a long quiet time is spread over `burstSeconds`. A
/// counter that falls (the engine restarted) returns NaN once and starts again from that value.
struct CounterRate {
    static let minSpan = 1.0, maxSpan = 3.0, minHold = 1.0, maxHold = 3.0, burstSeconds = 1.5
    private static let maxEvents = 12, intervalsForTypical = 5

    private var events: [(t: Double, v: Double)] = []     // the baseline, then every read at which the counter rose
    private var held = 0.0

    /// Forgets everything: the next `update` is a new baseline.
    mutating func reset() { events.removeAll(); held = 0 }

    /// Feeds one read. Returns the rate in counts per second: 0 while nothing rises, NaN for the one read at which the counter fell. The first
    /// read after `reset` is the baseline and returns 0.
    mutating func update(t: Double, v: Double) -> Double {
        guard let last = events.last else { events.append((t, v)); return 0 }
        if v < last.v { events = [(t, v)]; held = 0; return .nan }
        if v > last.v {
            events.append((t, v))
            if events.count > CounterRate.maxEvents { events.removeFirst() }
            held = rateAtNewestEvent()
        }
        if events.count < 2 { return 0 }
        let hold = min(max(1.5 * typicalInterval(), CounterRate.minHold), CounterRate.maxHold)
        return t - events[events.count - 1].t <= hold ? held : 0
    }

    private func typicalInterval() -> Double {
        var gaps: [Double] = []
        var i = events.count - 1
        while i > 0 && gaps.count < CounterRate.intervalsForTypical {
            gaps.append(min(events[i].t - events[i - 1].t, CounterRate.maxSpan))
            i -= 1
        }
        gaps.sort()
        return gaps.isEmpty ? CounterRate.minSpan : gaps[gaps.count / 2]
    }

    private func rateAtNewestEvent() -> Double {
        let e = events[events.count - 1]
        let span = min(max(2 * typicalInterval(), CounterRate.minSpan), CounterRate.maxSpan)
        // The newest earlier event that is at least one span back; else the oldest one we have.
        var b = 0
        var i = events.count - 2
        while i >= 0 {
            if e.t - events[i].t >= span { b = i; break }
            i -= 1
        }
        let seconds = e.t - events[b].t
        if seconds <= 0 { return 0 }
        if seconds > CounterRate.maxSpan {
            // The window reaches back over a long quiet time: the step is one burst, not a rate over the quiet time.
            let prev = events[events.count - 2]
            return (e.v - prev.v) / min(max(e.t - prev.t, 1e-3), CounterRate.burstSeconds)
        }
        return (e.v - events[b].v) / seconds
    }
}
