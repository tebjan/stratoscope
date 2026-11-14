import Foundation

/// An adaptive speed scale for bars and graphs: 0 to `max` tokens per second. The Swift twin of SpeedScale.cs; keep them in step.
/// Rise: a value above the maximum sets the maximum to that value plus `riseMargin`, rounded up to two significant digits (120 becomes 130), so a speed
/// that creeps upwards does not move the scale with every sample.
/// Fall: when every value stays below `fallBelow` of the maximum for `fallHoldSeconds`, the maximum steps back to the next nice number above the
/// peak of that period (never below `floor`). The new maximum is above the peak by the margin, so the scale does not oscillate.
public struct SpeedScale {
    /// The share of the scale below which values count as low.
    public static let fallBelow = 0.5
    /// How long every value must stay low before the maximum steps back, in seconds.
    public static let fallHoldSeconds = 25.0
    /// The room above a value when the maximum rises or falls to it.
    public static let riseMargin = 0.05
    /// The maximum a scale starts at and never falls below, tokens per second.
    public static let defaultMax = 100.0

    public let floor: Double
    public private(set) var max: Double
    /// The lower mark of the scale. A speed is never negative.
    public var min: Double { 0 }
    private var lowSince: Double?
    private var lowPeak = 0.0

    /// `initialMax` is the first maximum and the floor: what the engine reports when it does, else `defaultMax`.
    public init(initialMax: Double = SpeedScale.defaultMax) {
        floor = initialMax > 0 ? SpeedScale.roundUp(initialMax) : SpeedScale.defaultMax
        max = floor
    }

    /// The fraction of the scale a value fills, 0...1.
    public func fraction(_ value: Double) -> Double { value.isFinite && value > 0 ? Swift.min(1, value / max) : 0 }

    /// Feeds one observation: the largest speed shown now. `now` is any monotonic clock in seconds. Unknown values are not fed.
    @discardableResult
    public mutating func observe(_ value: Double, now: Double) -> Double {
        guard value.isFinite, value >= 0 else { return max }
        if value > max {
            max = SpeedScale.roundUp(value * (1 + SpeedScale.riseMargin))
            lowSince = nil
            return max
        }
        if value >= max * SpeedScale.fallBelow { lowSince = nil; return max }
        if lowSince != nil { lowPeak = Swift.max(lowPeak, value) } else { lowSince = now; lowPeak = value }
        if now - (lowSince ?? now) >= SpeedScale.fallHoldSeconds {
            let target = Swift.max(floor, SpeedScale.roundUp(lowPeak * (1 + SpeedScale.riseMargin)))
            if target < max { max = target }
            lowSince = nil
        }
        return max
    }

    /// The smallest round number at or above `value` with two significant digits (126 becomes 130, 283 becomes 290, 1260 becomes 1300): the
    /// scale stays close to the values, so a graph never wastes half its height. It still moves only on a rise or after the hold.
    public static func roundUp(_ value: Double) -> Double {
        guard value.isFinite, value > 0 else { return 1 }
        let step = pow(10, Foundation.floor(log10(value)) - 1)
        return (value / step * (1 - 1e-9)).rounded(.up) * step
    }
}

/// A one-pole IIR low-pass, `y = d * y + (1 - d) * x`. The first value, and the first one after an unknown value (NaN), passes unchanged.
public struct OnePoleFilter {
    public let damping: Double
    private var y = 0.0
    private var primed = false

    public init(damping: Double) { self.damping = Swift.min(Swift.max(damping, 0), 0.999) }

    /// Feeds one sample and returns the filtered one. NaN resets the filter and returns NaN.
    /// `dt` is the time since the previous sample in seconds: the damping is per second, a sample step uses `d ^ dt` (dt 1 is the plain y = d*y + (1-d)*x).
    public mutating func step(_ x: Double, dt: Double = 1) -> Double {
        guard x.isFinite else { primed = false; return .nan }
        let d = damping <= 0 ? 0 : pow(damping, Swift.max(0, dt))
        y = primed ? d * y + (1 - d) * x : x
        primed = true
        return y
    }
}

/// How strongly the history graph is smoothed (setting `history_smoothing`).
public enum HistorySmoothing: String, CaseIterable {
    case off, normal, high, highest
    /// The damping d of the filter: 0 (off), 0.5 (default), 0.9, 0.95.
    public var damping: Double {
        switch self { case .off: return 0; case .normal: return 0.5; case .high: return 0.9; case .highest: return 0.95 }
    }
    public var title: String { rawValue.prefix(1).uppercased() + rawValue.dropFirst() }
}
