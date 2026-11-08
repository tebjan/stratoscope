import Foundation

/// Rebuilds the list of parked conversations from the Strata log.
///
/// Strata's `/metrics` reports how many conversations are parked, but not which ones. The log
/// records every park, restore and eviction, so the list is reconstructed from it. The id `K<n>`
/// is just a running number of parks. The entry count is trimmed to the number `/metrics` reports.
final class StrataCacheLog {
    private(set) var entries: [CacheEntry] = []
    private var offset: UInt64 = 0
    private var seq = 0
    private var primed = false
    private var pendingDrop = 0
    private var lastPath: String?

    private static func rx(_ p: String) -> NSRegularExpression { try! NSRegularExpression(pattern: p) }
    private static let reParked = rx(#"conversation cache: parked (\d+) tokens in [\d.]+ ms; parked=(\d+) bytes=(\d+) evictions=(\d+) snapshot_bytes=(\d+)"#)
    private static let reRestored = rx(#"conversation cache: restored (\d+) tokens .*?parked=(\d+)"#)
    private static let reBorrowed = rx(#"conversation cache: borrowed \d+ tokens .*?parked=(\d+)"#)
    private static let reEvicted = rx(#"conversation cache: evicted (\d+) parked conversation"#)
    private static let reDropped = rx(#"conversation cache: dropped (\d+) superseded .*?parked=(\d+)"#)

    private func ints(_ re: NSRegularExpression, _ s: String) -> [Int]? {
        guard let m = re.firstMatch(in: s, range: NSRange(s.startIndex..., in: s)) else { return nil }
        return (1..<m.numberOfRanges).compactMap { Int((s as NSString).substring(with: m.range(at: $0))) }
    }

    private func reset() { entries = []; primed = false; pendingDrop = 0 }
    private func reconcile(_ parked: Int) { while entries.count > parked && !entries.isEmpty { entries.removeFirst() } }

    /// Feeds one log line. `known` is false while catching up on old history (no timestamps then).
    func handle(_ line: String, known: Bool) {
        guard line.contains("conversation cache") else { return }
        if line.contains("sized from the free RAM") || line.contains("cache off") { entries = []; pendingDrop = 0; return }
        if let v = ints(Self.reParked, line) {
            let n = v[0]
            for _ in 0..<pendingDrop {      // superseded copy of the same conversation: the largest smaller one
                let cand = entries.enumerated().filter { $0.element.tokens <= n }.max { $0.element.tokens < $1.element.tokens }
                if let c = cand { entries.remove(at: c.offset) }
            }
            pendingDrop = 0
            seq += 1
            entries.append(CacheEntry(id: seq, tokens: n, bytes: Double(v[4]), parkedAt: known ? Date() : nil))
            reconcile(v[1]); return
        }
        if let v = ints(Self.reRestored, line) {
            if let i = entries.firstIndex(where: { $0.tokens == v[0] }) { entries.remove(at: i) }
            pendingDrop = 0; reconcile(v[1]); return
        }
        if let v = ints(Self.reBorrowed, line) { reconcile(v[0]); return }
        if let v = ints(Self.reEvicted, line) { for _ in 0..<min(v[0], entries.count) { entries.removeFirst() }; return }
        if let v = ints(Self.reDropped, line) { pendingDrop = v[0]; return }
    }

    /// Reads whatever was appended to the log since the last call.
    func update(path: String?) {
        guard let path = path else { return }
        if path != lastPath { lastPath = path; offset = 0; reset() }
        guard let fh = FileHandle(forReadingAtPath: path) else { return }
        defer { try? fh.close() }
        guard let size = try? fh.seekToEnd() else { return }
        if size < offset { offset = 0; reset() }
        var start = offset
        if !primed && size > 8_000_000 { start = size - 8_000_000 }   // only the recent history on first read
        guard (try? fh.seek(toOffset: start)) != nil, let data = try? fh.readToEnd(), !data.isEmpty,
              let nl = data.lastIndex(of: 10) else { return }
        let chunk = data[data.startIndex...nl]
        offset = start + UInt64(chunk.count)
        let known = primed
        for line in String(decoding: chunk, as: UTF8.self).split(separator: "\n") { handle(String(line), known: known) }
        primed = true
    }
}
