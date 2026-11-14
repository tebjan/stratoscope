import Foundation

/// The speed of each batch slot from a cumulative count the engine reports per slot (tokens generated, or prompt tokens computed), taken
/// between polls: the oldest sample of the last few seconds against the newest, needing at least a second between them. A slot with fewer
/// samples, or one that started a new request (its count fell, or time did not advance), has no speed yet. For engines whose slots carry the
/// count but no usable speed (Strata, llama-server). One tracker per count. The C# twin is `SlotRateTracker.cs`.
final class SlotRateTracker {
    private static let window: TimeInterval = 4, minSpan: TimeInterval = 1
    private var history: [Int: [(at: Date, value: Double)]] = [:]

    /// `slots`: the slots that run in the phase this tracker measures, each with its cumulative count (nil when the slot does not say).
    /// A slot that is not listed loses its history. Returns tokens per second by slot id, only for slots that have a speed.
    func update(_ slots: [(id: Int, value: Double?)], now: Date) -> [Int: Double] {
        var rates: [Int: Double] = [:]
        let live = Set(slots.map { $0.id })
        for gone in history.keys where !live.contains(gone) { history[gone] = nil }
        for (id, value) in slots {
            guard let count = value else { continue }
            var samples = history[id] ?? []
            if let last = samples.last, count < last.value || now <= last.at { samples.removeAll() }      // a new request in this slot
            samples.append((now, count))
            samples.removeAll { now.timeIntervalSince($0.at) > Self.window }
            history[id] = samples
            let oldest = samples[0]
            let span = now.timeIntervalSince(oldest.at)
            if span >= Self.minSpan, count >= oldest.value { rates[id] = (count - oldest.value) / span }
        }
        return rates
    }
}
