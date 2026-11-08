import Foundation

/// How well a request used the prompt cache. Drives the colour of the progress bar and the dots.
public enum CacheHit {
    case hit, partial, miss
    case small   // prompt too short for the cache to matter

    public var label: String {
        switch self {
        case .hit: return "hit"
        case .partial: return "partial"
        case .miss: return "miss"
        case .small: return "n/a (small prompt)"
        }
    }

    /// - hit: at least 90 % of the prompt came from cache
    /// - partial: less, but at least 8,192 tokens were reused
    /// - miss: anything else
    public static func classify(prompt: Double, reused: Double) -> CacheHit {
        if prompt < 4096 { return .small }
        if reused >= 0.9 * prompt { return .hit }
        return reused >= 8192 ? .partial : .miss
    }
}
