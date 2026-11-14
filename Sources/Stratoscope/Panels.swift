import Cocoa
import StratoscopeCore

/// Builds the menu sections for a snapshot and paints them. Sections the engine does not report
/// (`system`, `cache`, `totals` are optional) are left out.
final class Panels {
    var snap: EngineSnapshot?
    /// The last 60 seconds of speed, drawn as the history graph; nil leaves the graph out.
    var history: SpeedHistory?
    var smoothing = HistorySmoothing.normal
    /// The clock the menu measures ages against. The demo engine pins it so screenshots come out the same every time.
    let now: () -> Date

    init(now: @escaping () -> Date = Date.init) { self.now = now }

    struct Section { let id: String; let height: CGFloat; let paint: (NSRect) -> Void }

    func sections(for s: EngineSnapshot) -> [[Section]] {
        var groups: [[Section]] = []
        groups.append([Section(id: "live", height: 198) { [unowned self] _ in self.paintLive() }])
        if !s.isLoading {
            if history != nil { groups.append([Section(id: "history", height: 144) { [unowned self] _ in self.paintHistory() }]) }
            groups.append([Section(id: "speed", height: 96) { [unowned self] _ in self.paintSpeed() }])
            if let sys = s.system {
                // A fourth row for the video memory when the engine reports it (custom server); the other engines keep three rows.
                let withVram = sys.gpuMemoryUsedBytes != nil && sys.gpuMemoryTotalBytes != nil
                groups.append([Section(id: "system", height: withVram ? 130 : 104) { [unowned self] _ in self.paintSystem() }])
            }
            if let c = s.cache {
                var g = [Section(id: "cachehead", height: 84) { [unowned self] _ in self.paintCacheHead() }]
                for i in 0..<min(c.slots, 8) { g.append(Section(id: "cacherow\(i)", height: 38) { [unowned self] _ in self.paintCacheRow(i) }) }
                groups.append(g)
            }
            if s.engine == CustomReader.brand, s.info != nil { groups.append([Section(id: "engine", height: 66) { [unowned self] _ in self.paintEngine() }]) }
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
        case .running: return "running"
        }
    }

    static func color(_ s: EngineSnapshot) -> NSColor {
        switch s.status {
        case .loading: return .systemYellow
        case .idle: return .systemGreen
        case .prefill: return .systemOrange
        case .thinking, .decode: return .systemBlue
        case .running: return .systemGray
        }
    }

    // MARK: Live

    func paintLive() {
        guard let s = snap else { return }
        let inner = menuWidth - 2 * pad, col = Panels.color(s)
        dot(pad, 14, col)
        text(s.model, x: pad + 18, y: 11, size: 13, bold: true)
        if let label = s.engineLabel { text(label, x: pad + 18, y: 27, size: 10, color: .secondaryLabelColor) }
        text(Panels.word(s) + (s.queued > 0 ? " · Queue \(s.queued)" : ""), x: 0, y: 11, size: 12, bold: true, color: col, right: menuWidth - pad)
        if case .loading(let why) = s.status { text(why, x: pad, y: 44, size: 12); return }

        if let l = s.live, s.isReading {
            text("Prefill  \(l.percent.map { "\($0)" } ?? "–") %", x: pad, y: 40, size: 13, bold: true)
            text("\(l.prefillTokensPerSecond != nil ? Fmt.number(l.prefillTokensPerSecond) : "–") t/s", x: 0, y: 40, size: 13, bold: true, right: menuWidth - pad)
            bar(pad, 60, inner, 9, (l.promptTotal ?? 0) > 0 ? (l.promptRead ?? 0) / (l.promptTotal ?? 1) : 0, .systemOrange)
            var line = "\(Fmt.number(l.promptRead)) of \(Fmt.number(l.promptTotal)) tokens"
            if let sp = l.prefillTokensPerSecond, sp > 0, let total = l.promptTotal, let read = l.promptRead, total > read { line += " · \(Fmt.duration((total - read) / sp)) left" }
            text(line, x: pad, y: 73, size: 11)
            text("\(Fmt.number(l.elapsedSeconds)) s", x: 0, y: 73, size: 11, right: menuWidth - pad)
            if let total = l.promptTotal, let reused = l.reusedEstimate {
                let hit = CacheHit.classify(prompt: total, reused: reused)
                dot(pad, 92, hit.color)
                text("Cache \(hit.label): ≈ \(Fmt.number((reused / 100).rounded() * 100)) of \(Fmt.number(total)) tokens", x: pad + 16, y: 90, size: 11, bold: true)
            } else {
                text("Cache –", x: pad, y: 90, size: 11, bold: true)
            }
        } else if let l = s.live, s.status == .running {
            // The engine does not say whether the requests read or write: no phase, no speed.
            text("Running", x: pad, y: 40, size: 13, bold: true)
            text(l.running.map { "\($0) requests" } ?? "–", x: 0, y: 40, size: 13, bold: true, right: menuWidth - pad)
        } else if let l = s.live {
            text("Decode", x: pad, y: 40, size: 13, bold: true)
            text("\(l.decodeTokensPerSecond != nil ? Fmt.number(l.decodeTokensPerSecond, 1) : "–") t/s", x: 0, y: 40, size: 13, bold: true, right: menuWidth - pad)
            bar(pad, 60, inner, 9, (l.generated ?? 0) / max(l.maxTokens ?? 1, 1), .systemBlue)
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

    // MARK: History

    /// The last 60 seconds in one graph, like a frame-rate graph: orange is the prefill speed (prompt tokens per second), blue the decode speed
    /// (generated tokens per second) and the label colour the total throughput (prompt plus generated tokens per second, the vLLM and
    /// OpenTelemetry GenAI meaning, see `SpeedHistory`). The total is a third curve in the same graph: it shares the axis, so it reads directly
    /// against its two parts, and it costs no height. The legend is one row: colour mark, name and the average over the 60 s (of the raw samples). The vertical axis is the adaptive scale
    /// of the history (0 at the baseline, its maximum labelled at the top); the horizontal one is the last 60 s, unlabelled. Not built or run on the Windows PC this was written on.
    func paintHistory() {
        guard let h = history else { return }
        let inner = menuWidth - 2 * pad
        let curves: [(HistoryCurve, String, NSColor)] = [(.prefill, "Prefill", .systemOrange), (.decode, "Decode", .systemBlue), (.total, "Total", .labelColor)]
        for (i, (curve, name, color)) in curves.enumerated() {
            let x = pad + [0, 0.3, 0.6][i] * inner      // the last name is the longest
            color.setFill(); NSRect(x: x, y: 14, width: 8, height: 3).fill()
            let nameWidth = NSAttributedString(string: name, attributes: [.font: font(12, bold: false)]).size().width
            text(name, x: x + 13, y: 8, size: 12, color: .secondaryLabelColor)
            let v = h.average(curve, now: Date().timeIntervalSince1970)     // the average of the raw samples of the window, not of the smoothed curve
            text(v.isNaN ? "–" : Fmt.number(v), x: x + 13 + nameWidth + 5, y: 8, size: 12, bold: true, color: curve == .total ? .labelColor : color)
        }
        let gutter: CGFloat = 30, left = pad + gutter, right = menuWidth - pad, top: CGFloat = 32, plotH: CGFloat = 100
        let scale = h.graphScale
        NSColor.labelColor.withAlphaComponent(0.06).setFill()
        NSRect(x: left, y: top, width: right - left, height: plotH).fill()
        NSColor.labelColor.withAlphaComponent(0.16).setFill()
        for f in [0.0, 0.5, 1.0] { NSRect(x: left, y: (top + plotH - plotH * CGFloat(f)).rounded() - 1, width: right - left, height: 1).fill() }
        text(Fmt.number(scale.max), x: 0, y: top - 6, size: 10, color: .secondaryLabelColor, right: left - 4)     // the top of the scale; 0 is the baseline

        NSGraphicsContext.saveGraphicsState()
        NSRect(x: left, y: top - 1, width: right - left, height: plotH + 2).clip()
        let nowSeconds = Date().timeIntervalSince1970     // the wall clock, as the samples are taken: the pinned demo clock would stack them
        for (curve, _, color) in curves {
            let path = NSBezierPath()
            path.lineWidth = curve == .total ? 2 : 1.5
            path.lineJoinStyle = .round
            var open = false
            for sample in h.readSmoothed(curve, damping: smoothing.damping, now: nowSeconds) {
                if sample.value.isNaN { open = false; continue }
                let p = NSPoint(x: right - CGFloat(sample.age / SpeedHistory.windowSeconds) * (right - left),
                                y: top + plotH - CGFloat(scale.fraction(sample.value)) * plotH)
                if open { path.line(to: p) } else { path.move(to: p); open = true }
            }
            color.setStroke(); path.stroke()
        }
        NSGraphicsContext.restoreGraphicsState()
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
        sparkbars(pad, 48, half, 30, pre, history?.prefillChartScale.max ?? max((pv.max() ?? 0) * 1.15, 100), .systemOrange)
        text("Avg \(pv.isEmpty ? "–" : Fmt.number(pv.reduce(0, +) / Double(pv.count)))", x: pad, y: 80, size: 10)
        text("Decode", x: xr, y: 30, size: 11, bold: true, color: .systemBlue)
        text("\(dv.last.map { Fmt.number($0, 1) } ?? "–") t/s", x: 0, y: 30, size: 11, right: menuWidth - pad)
        sparkbars(xr, 48, half, 30, dec, history?.decodeChartScale.max ?? max((dv.max() ?? 0) * 1.15, 10), .systemBlue)
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
        // An unreported value is "–" with an empty bar, never 0.
        if let used = sys.ramUsedBytes, let total = sys.ramTotalBytes, total > 0 {
            row(28, "RAM", "\(Fmt.gib(used)) of \(Fmt.gib(total)) GB", used / total, used / total > 0.9 ? .systemRed : .systemPurple)
        } else { row(28, "RAM", "–", 0, .systemPurple) }
        var gpu = sys.gpuPercent.map { "\(Fmt.number($0)) %" } ?? "–"
        if let t = sys.gpuTemperatureCelsius { gpu += " · \(Fmt.number(t)) °C" }
        if let p = sys.gpuPowerWatts { gpu += " · \(Fmt.number(p)) W" + (sys.gpuPowerLimitWatts.map { " of \(Fmt.number($0)) W" } ?? "") }
        row(54, "GPU", gpu, (sys.gpuPercent ?? 0) / 100, .systemGreen)
        row(80, "CPU", sys.cpuPercent.map { "\(Fmt.number($0)) %" } ?? "–", (sys.cpuPercent ?? 0) / 100, .systemIndigo)
        if let used = sys.gpuMemoryUsedBytes, let total = sys.gpuMemoryTotalBytes, total > 0 {
            row(106, "Video memory", "\(Fmt.gib(used)) of \(Fmt.gib(total)) GiB", used / total, used / total > 0.95 ? .systemRed : .systemTeal)
        }
    }

    // MARK: Engine (custom server)

    func paintEngine() {
        guard let s = snap, let i = s.info else { return }
        func n(_ v: Double?) -> String { v.map { Fmt.number($0) } ?? "–" }
        text("Engine", x: pad, y: 8, size: 12, bold: true)
        text(i.gpuName ?? "–", x: 0, y: 8, size: 12, right: menuWidth - pad)
        let busy = s.slots?.filter { $0.isActive }.count
        text("Slots \(busy.map { String($0) } ?? "–") of \(n(i.batchSlots)) · target \(n(i.targetSeqs))", x: pad, y: 28, size: 11)
        text("Draft \(i.draftWindow ?? "–") · KV \(i.kvFormat ?? "–")", x: 0, y: 28, size: 11, right: menuWidth - pad)
        text("Free KV pages \(n(i.kvFreePages)) · checkpoints \(n(i.prefixCheckpoints))", x: pad, y: 46, size: 11)
        text("Held back: \(i.admissionBlocked ?? "–")", x: 0, y: 46, size: 11, right: menuWidth - pad)
    }

    // MARK: Conversation cache

    func paintCacheHead() {
        guard let s = snap, let c = s.cache else { return }
        text(s.engine == CustomReader.brand ? "KV cache" : "Conversation cache", x: pad, y: 8, size: 12, bold: true)
        text("\(Fmt.gib(c.usedBytes)) of \(Fmt.gib(c.budgetBytes)) GiB", x: 0, y: 8, size: 12, right: menuWidth - pad)
        bar(pad, 28, menuWidth - 2 * pad, 9, c.budgetBytes > 0 ? c.usedBytes / c.budgetBytes : 0,
            c.usedBytes / max(c.budgetBytes, 1) > 0.9 ? .systemRed : .systemMint)
        if c.slots > 0 { text("\(c.parked) of \(c.slots) slots used", x: pad, y: 42, size: 11) }
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
