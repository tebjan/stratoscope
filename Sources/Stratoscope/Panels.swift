import Cocoa
import StratoscopeCore

/// Builds the menu sections for a snapshot and paints them. Sections the engine does not report
/// (`system`, `cache`, `totals` are optional) are left out.
final class Panels {
    var snap: EngineSnapshot?
    /// The clock the menu measures ages against. The demo engine pins it so screenshots come out the same every time.
    let now: () -> Date

    init(now: @escaping () -> Date = Date.init) { self.now = now }

    struct Section { let id: String; let height: CGFloat; let paint: (NSRect) -> Void }

    func sections(for s: EngineSnapshot) -> [[Section]] {
        var groups: [[Section]] = []
        groups.append([Section(id: "live", height: 198) { [unowned self] _ in self.paintLive() }])
        if !s.isLoading {
            groups.append([Section(id: "speed", height: 96) { [unowned self] _ in self.paintSpeed() }])
            if s.system != nil { groups.append([Section(id: "system", height: 104) { [unowned self] _ in self.paintSystem() }]) }
            if let c = s.cache {
                var g = [Section(id: "cachehead", height: 84) { [unowned self] _ in self.paintCacheHead() }]
                for i in 0..<min(c.slots, 8) { g.append(Section(id: "cacherow\(i)", height: 38) { [unowned self] _ in self.paintCacheRow(i) }) }
                groups.append(g)
            }
            if s.totals != nil { groups.append([Section(id: "totals", height: 50) { [unowned self] _ in self.paintTotals() }]) }
        }
        return groups
    }

    // MARK: Words for the state

    static func word(_ s: EngineSnapshot) -> String {
        switch s.status {
        case .loading: return "Loading"
        case .idle: return "Ready"
        case .prefill: return "Reading"
        case .thinking: return "Thinking"
        case .decode: return "Writing"
        }
    }

    static func color(_ s: EngineSnapshot) -> NSColor {
        switch s.status {
        case .loading: return .systemYellow
        case .idle: return .systemGreen
        case .prefill: return .systemOrange
        case .thinking, .decode: return .systemBlue
        }
    }

    // MARK: Live

    func paintLive() {
        guard let s = snap else { return }
        let inner = menuWidth - 2 * pad, col = Panels.color(s)
        dot(pad, 14, col)
        text(s.model, x: pad + 18, y: 11, size: 13, bold: true)
        text(Panels.word(s) + (s.queued > 0 ? " · Queue \(s.queued)" : ""), x: 0, y: 11, size: 12, bold: true, color: col, right: menuWidth - pad)
        if case .loading(let why) = s.status { text(why, x: pad, y: 44, size: 12); return }

        if let l = s.live, s.isReading {
            text("Prefill  \(l.percent) %", x: pad, y: 40, size: 13, bold: true)
            text(l.prefillTokensPerSecond != nil ? "\(Fmt.number(l.prefillTokensPerSecond)) t/s" : "0 t/s", x: 0, y: 40, size: 13, bold: true, right: menuWidth - pad)
            bar(pad, 60, inner, 9, l.promptTotal > 0 ? l.promptRead / l.promptTotal : 0, .systemOrange)
            var line = "\(Fmt.number(l.promptRead)) of \(Fmt.number(l.promptTotal)) tokens"
            if let sp = l.prefillTokensPerSecond, sp > 0, l.promptTotal > l.promptRead { line += " · \(Fmt.duration((l.promptTotal - l.promptRead) / sp)) left" }
            text(line, x: pad, y: 73, size: 11)
            text("\(Fmt.number(l.elapsedSeconds)) s", x: 0, y: 73, size: 11, right: menuWidth - pad)
            let hit = CacheHit.classify(prompt: l.promptTotal, reused: l.reusedEstimate)
            dot(pad, 92, hit.color)
            text("Cache \(hit.label): ≈ \(Fmt.number((l.reusedEstimate / 100).rounded() * 100)) of \(Fmt.number(l.promptTotal)) tokens", x: pad + 16, y: 90, size: 11, bold: true)
        } else if let l = s.live {
            text("Decode", x: pad, y: 40, size: 13, bold: true)
            text("\(l.decodeTokensPerSecond != nil ? Fmt.number(l.decodeTokensPerSecond, 1) : "0") t/s", x: 0, y: 40, size: 13, bold: true, right: menuWidth - pad)
            bar(pad, 60, inner, 9, l.generated / max(l.maxTokens, 1), .systemBlue)
            text("\(Fmt.number(l.generated)) of at most \(Fmt.number(l.maxTokens)) tokens", x: pad, y: 73, size: 11)
            text("\(Fmt.number(l.elapsedSeconds)) s", x: 0, y: 73, size: 11, right: menuWidth - pad)
        } else if let r = s.recent.first {
            let pre = r.prefillTokensPerSecond().map { " · prefill \(Fmt.number($0)) t/s" } ?? ""
            text("Last request", x: pad, y: 40, size: 13, bold: true)
            text("Decode \(Fmt.number(r.decodeTokensPerSecond, 1)) t/s", x: 0, y: 40, size: 13, bold: true, right: menuWidth - pad)
            let hit = CacheHit.classify(prompt: r.promptTokens, reused: r.reusedTokens)
            dot(pad, 61, hit.color)
            text("Cache \(hit.label): \(Fmt.number(r.reusedTokens)) of \(Fmt.number(r.promptTokens)) tokens", x: pad + 16, y: 59, size: 11, bold: true)
            text("Read \(Fmt.number(r.promptRead))\(pre) · \(Fmt.number(r.outputTokens)) tokens in \(Fmt.number(r.durationSeconds)) s", x: pad, y: 75, size: 11)
        } else { text("No requests yet", x: pad, y: 44, size: 12) }

        // Context: the engine's limit, what this request uses of it, and (when someone knows it) the marker
        // where the client summarises. The bars show the size after each finished request: a sudden drop is
        // where a client started over.
        text("Context", x: pad, y: 112, size: 12, bold: true)
        let known = s.context.max > 0   // some engines don't report a maximum
        text(known ? "\(Fmt.number(s.context.used)) of \(Fmt.number(s.context.max))" : Fmt.number(s.context.used), x: 0, y: 112, size: 12, right: menuWidth - pad)
        bar(pad, 132, inner, 9, known ? (s.context.used ?? 0) / s.context.max : 0, .systemTeal,
            tick: known ? s.context.compactAt.map { $0 / s.context.max } : nil)
        if let free = s.context.free { text("\(Fmt.number(free)) free", x: pad, y: 147, size: 11) }
        let sizes = Array(s.recent.prefix(12).reversed()).map { $0.contextTokens }
        if let top = sizes.max(), top > 0 {
            sparkbars(pad, 165, inner, 24, sizes, top * 1.15, .systemTeal)
        }
    }

    // MARK: Speed

    func paintSpeed() {
        guard let s = snap else { return }
        let reqs = Array(s.recent.prefix(12).reversed())
        let dec = reqs.map { $0.decodeTokensPerSecond }
        // Prefill speed only for prompts that were actually read (not served from cache).
        let pre: [Double?] = reqs.map { $0.prefillTokensPerSecond(minTokens: 1500, minMilliseconds: 2000) }
        let half = (menuWidth - 2 * pad - 16) / 2, xr = pad + half + 16
        text("Speed", x: pad, y: 8, size: 12, bold: true)
        let dv = dec.compactMap { $0 }, pv = pre.compactMap { $0 }
        text("Prefill", x: pad, y: 30, size: 11, bold: true, color: .systemOrange)
        text("\(pv.last.map { Fmt.number($0) } ?? "–") t/s", x: 0, y: 30, size: 11, right: pad + half)
        sparkbars(pad, 48, half, 30, pre, max((pv.max() ?? 0) * 1.15, 100), .systemOrange)
        text("Avg \(pv.isEmpty ? "–" : Fmt.number(pv.reduce(0, +) / Double(pv.count)))", x: pad, y: 80, size: 10)
        text("Decode", x: xr, y: 30, size: 11, bold: true, color: .systemBlue)
        text("\(dv.last.map { Fmt.number($0, 1) } ?? "–") t/s", x: 0, y: 30, size: 11, right: menuWidth - pad)
        sparkbars(xr, 48, half, 30, dec, max((dv.max() ?? 0) * 1.15, 10), .systemBlue)
        text("Avg \(dv.isEmpty ? "–" : Fmt.number(dv.reduce(0, +) / Double(dv.count), 1))", x: xr, y: 80, size: 10)
    }

    // MARK: System

    func paintSystem() {
        guard let sys = snap?.system else { return }
        let inner = menuWidth - 2 * pad
        text("System", x: pad, y: 8, size: 12, bold: true)
        func row(_ y: CGFloat, _ label: String, _ value: String, _ frac: Double, _ c: NSColor) {
            text(label, x: pad, y: y, size: 11)
            text(value, x: 0, y: y, size: 11, right: menuWidth - pad)
            bar(pad, y + 15, inner, 6, frac, c)
        }
        let used = sys.ramUsedBytes ?? 0, total = sys.ramTotalBytes ?? 1
        row(28, "RAM", "\(Fmt.gib(used)) of \(Fmt.gib(total)) GB", used / total, used / total > 0.9 ? .systemRed : .systemPurple)
        row(54, "GPU", "\(Fmt.number(sys.gpuPercent ?? 0)) %", (sys.gpuPercent ?? 0) / 100, .systemGreen)
        row(80, "CPU", "\(Fmt.number(sys.cpuPercent ?? 0)) %", (sys.cpuPercent ?? 0) / 100, .systemIndigo)
    }

    // MARK: Conversation cache

    func paintCacheHead() {
        guard let s = snap, let c = s.cache else { return }
        text("Conversation cache", x: pad, y: 8, size: 12, bold: true)
        text("\(Fmt.gib(c.usedBytes)) of \(Fmt.gib(c.budgetBytes)) GiB", x: 0, y: 8, size: 12, right: menuWidth - pad)
        bar(pad, 28, menuWidth - 2 * pad, 9, c.budgetBytes > 0 ? c.usedBytes / c.budgetBytes : 0,
            c.usedBytes / max(c.budgetBytes, 1) > 0.9 ? .systemRed : .systemMint)
        text("\(c.parked) of \(c.slots) slots used", x: pad, y: 42, size: 11)
        text("Hit rate \(c.requests > 0 ? Fmt.number(c.requestsReused / c.requests * 100) : "–") % · evicted \(Fmt.number(c.evictions))", x: 0, y: 42, size: 11, right: menuWidth - pad)
        let recent = Array(s.recent.prefix(12).reversed())
        text("Last \(recent.count):", x: pad, y: 62, size: 11)
        var x = menuWidth - pad - CGFloat(recent.count) * 14 + 4
        for q in recent { dot(x, 63, CacheHit.classify(prompt: q.promptTokens, reused: q.reusedTokens).color); x += 14 }
    }

    func paintCacheRow(_ i: Int) {
        guard let c = snap?.cache else { return }
        let list = Array(c.entries.reversed())   // newest first
        guard i < list.count else {
            text("Slot \(i + 1)", x: pad, y: 6, size: 11)
            text(c.isParkedWithoutDetails(row: i) ? "In use" : "Free", x: 0, y: 6, size: 11, right: menuWidth - pad)
            bar(pad, 24, menuWidth - 2 * pad, 6, 0, .systemMint); return
        }
        let e = list[i]
        text("K\(e.id)", x: pad, y: 6, size: 11, bold: true)
        text("\(Fmt.number(Double(e.tokens))) tokens", x: pad + 46, y: 6, size: 11)
        var right = "\(Fmt.gib(e.bytes)) GiB"
        if let at = e.parkedAt { right += " · \(Fmt.duration(now().timeIntervalSince(at))) ago" }
        text(right, x: 0, y: 6, size: 11, right: menuWidth - pad)
        bar(pad, 24, menuWidth - 2 * pad, 6, e.bytes / max(c.budgetBytes, 1), .systemMint)
    }

    // MARK: Totals

    func paintTotals() {
        guard let t = snap?.totals else { return }
        let since = t.since.map { " since \(Fmt.duration(now().timeIntervalSince($0)))" } ?? ""
        text("Total\(since)", x: pad, y: 7, size: 12, bold: true)
        text("\(Fmt.number(t.requests)) requests · \(Fmt.number(t.outputTokens)) tokens generated", x: pad, y: 22, size: 11)
        text("\(Fmt.number(t.promptTokens / 1_000_000, 1)) M prompt tokens, \(t.promptTokens > 0 ? Fmt.number(t.reusedTokens / t.promptTokens * 100) : "–") % from cache", x: pad, y: 36, size: 11)
    }
}
