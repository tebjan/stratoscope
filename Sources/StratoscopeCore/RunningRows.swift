import Foundation

/// How a row of one running request is drawn in the panel.
public enum RunningRowStyle: Int, Comparable {
    /// State word and speed, a bar, and its numbers under the bar.
    case full
    /// State word with its count and the speed on one line, a thin bar under it.
    case compact
    /// The bar alone (many requests); the numbers are in the tooltip of the row.
    case barOnly
    public static func < (a: RunningRowStyle, b: RunningRowStyle) -> Bool { a.rawValue < b.rawValue }
}

public struct RunningRowLayout: Equatable {
    /// Distance from the top of one row to the top of the next, in points.
    public var pitch: Double
    public var style: RunningRowStyle
    /// Height of the bar, in points.
    public var bar: Double
}

/// The per-request rows of the panel live block. They share an area of fixed height, so the panel never changes size with the number of
/// requests: the pitch is the area divided by the count, at most `maxPitch`, and the style falls back to a thinner row where text no
/// longer fits. Mirrors `RunningRows.cs` (Windows); not built in the Windows development environment.
public enum RunningRows {
    public static let maxRows = RequestBars.maxBars
    public static let areaHeight = 96.0
    public static let maxPitch = 40.0
    public static let fullPitch = 36.0
    public static let compactPitch = 24.0

    public static func layout(count: Int) -> RunningRowLayout {
        let n = Double(min(max(count, 1), maxRows))
        let pitch = min(maxPitch, areaHeight / n)
        if pitch >= fullPitch { return RunningRowLayout(pitch: pitch, style: .full, bar: 6) }
        if pitch >= compactPitch { return RunningRowLayout(pitch: pitch, style: .compact, bar: 4) }
        let gap = max(2, (pitch / 4).rounded())
        return RunningRowLayout(pitch: pitch, style: .barOnly, bar: min(8, pitch - gap))
    }

    /// The token limit all generating requests share (shown once), or nil when limits differ or one is unknown.
    public static func sharedLimit(_ running: [BatchSlot]) -> Double? {
        var limit: Double?
        for slot in running where slot.isDecoding {
            guard let m = slot.maxTokens, m > 0 else { return nil }
            if let l = limit, l != m { return nil }
            limit = m
        }
        return limit
    }
}
