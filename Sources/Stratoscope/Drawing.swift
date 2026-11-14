import Cocoa
import StratoscopeCore

// Small drawing helpers. Everything in the menu is drawn by hand so the bars, dots and sparklines
// match the macOS look while staying cheap to update every two seconds.

let menuWidth: CGFloat = 352
let pad: CGFloat = 14

extension CacheHit {
    var color: NSColor {
        switch self {
        case .hit: return .systemGreen
        case .partial: return .systemYellow
        case .miss: return .systemOrange
        case .small: return .systemGray
        }
    }
}

/// One row of the menu. Flipped so y grows downwards, like in the layout code.
final class Block: NSView {
    let id: String
    var painter: ((NSRect) -> Void)?
    override var isFlipped: Bool { true }
    init(id: String, height: CGFloat, painter: @escaping (NSRect) -> Void) {
        self.id = id
        super.init(frame: NSRect(x: 0, y: 0, width: menuWidth, height: height)); self.painter = painter
    }
    required init?(coder: NSCoder) { fatalError() }
    override func draw(_ r: NSRect) { painter?(bounds) }
}

func font(_ size: CGFloat, bold: Bool) -> NSFont {
    NSFont.monospacedDigitSystemFont(ofSize: size, weight: bold ? .semibold : .regular)
}

func text(_ s: String, x: CGFloat, y: CGFloat, size: CGFloat = 12, bold: Bool = false,
          color: NSColor = .labelColor, right: CGFloat? = nil) {
    let attrs: [NSAttributedString.Key: Any] = [.font: font(size, bold: bold), .foregroundColor: color]
    let str = NSAttributedString(string: s, attributes: attrs)
    str.draw(at: NSPoint(x: right != nil ? right! - str.size().width : x, y: y))
}

func bar(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat, _ frac: Double, _ color: NSColor, tick: Double? = nil) {
    NSColor.labelColor.withAlphaComponent(0.16).setFill()
    NSBezierPath(roundedRect: NSRect(x: x, y: y, width: w, height: h), xRadius: h / 2, yRadius: h / 2).fill()
    let f = max(0, min(1, frac.isFinite ? frac : 0))
    if f > 0 {
        color.setFill()
        NSBezierPath(roundedRect: NSRect(x: x, y: y, width: max(h, w * CGFloat(f)), height: h), xRadius: h / 2, yRadius: h / 2).fill()
    }
    if let t = tick {
        NSColor.labelColor.setFill()
        NSRect(x: x + w * CGFloat(t) - 0.75, y: y - 2, width: 1.5, height: h + 4).fill()
    }
}

func sparkbars(_ x: CGFloat, _ y: CGFloat, _ w: CGFloat, _ h: CGFloat, _ vals: [Double?], _ maxV: Double, _ color: NSColor) {
    let n = max(vals.count, 1), gap: CGFloat = 2
    let bw = (w - gap * CGFloat(n - 1)) / CGFloat(n)
    for (i, v) in vals.enumerated() {
        let bx = x + CGFloat(i) * (bw + gap)
        NSColor.labelColor.withAlphaComponent(0.10).setFill()
        NSRect(x: bx, y: y, width: bw, height: h).fill()
        if let v = v, v > 0 {
            let bh = max(2, h * CGFloat(min(1, v / maxV)))
            (i == vals.count - 1 ? color : color.withAlphaComponent(0.65)).setFill()
            NSRect(x: bx, y: y + h - bh, width: bw, height: bh).fill()
        }
    }
}

func dot(_ x: CGFloat, _ y: CGFloat, _ color: NSColor) {
    color.setFill(); NSBezierPath(ovalIn: NSRect(x: x, y: y, width: 10, height: 10)).fill()
}

/// The small progress bar shown in the menu bar while a prompt is being read.
func progressImage(_ frac: Double, _ hit: CacheHit) -> NSImage {
    let w: CGFloat = 46, h: CGFloat = 9
    return NSImage(size: NSSize(width: w + 2, height: 18), flipped: false) { _ in
        let box = NSRect(x: 1, y: (18 - h) / 2, width: w, height: h)
        NSColor.labelColor.withAlphaComponent(0.25).setFill()
        NSBezierPath(roundedRect: box, xRadius: 4, yRadius: 4).fill()
        hit.color.withAlphaComponent(0.85).setFill()
        NSBezierPath(roundedRect: NSRect(x: 1, y: box.minY, width: w * CGFloat(max(0.04, min(1, frac))), height: h),
                     xRadius: 4, yRadius: 4).fill()
        NSColor.labelColor.withAlphaComponent(0.8).setStroke()
        let o = NSBezierPath(roundedRect: box.insetBy(dx: 0.5, dy: 0.5), xRadius: 4, yRadius: 4)
        o.lineWidth = 1; o.stroke()
        return true
    }
}

/// The menu bar bars of the running requests: one bar per request, stacked in the 9 pt the single progress bar takes, each as wide as that bar
/// (46 pt). A request that reads shows its prompt progress (cache colour while it is the only request, orange otherwise); one that writes shows its
/// own speed against the adaptive scale 0 to `scaleMax` tokens per second, in blue. A writing request without a reported speed is an empty track.
/// One request keeps the look of `progressImage` (the outline, the height of 9 pt). Not built or run on the Windows PC this was written on.
func requestBarsImage(_ bars: [RequestBar], hit: CacheHit?, scaleMax: Double) -> NSImage {
    let w: CGFloat = 46, h: CGFloat = 9
    let rows = RequestBars.rows(count: bars.count, singleHeight: Double(h), stack: Double(h))
    let barH = CGFloat(rows.bar), gap = CGFloat(rows.gap), total = CGFloat(rows.total)
    return NSImage(size: NSSize(width: w + 2, height: 18), flipped: false) { _ in
        let top = (18 + total) / 2          // flipped: false, so the first bar is the topmost
        for (i, b) in bars.enumerated() {
            let box = NSRect(x: 1, y: top - CGFloat(i + 1) * barH - CGFloat(i) * gap, width: w, height: barH)
            let radius = barH >= 3 ? barH / 2 : 0
            NSColor.labelColor.withAlphaComponent(0.25).setFill()
            NSBezierPath(roundedRect: box, xRadius: radius, yRadius: radius).fill()
            var fraction: Double? = nil
            var color = NSColor.systemBlue
            if b.reading {
                fraction = max(0.04, min(1, b.fraction))
                color = bars.count == 1 ? (hit?.color ?? .systemOrange) : .systemOrange
            } else if let speed = b.tokensPerSecond, speed.isFinite, speed > 0 {
                fraction = min(1, speed / max(1, scaleMax))
            }
            if let f = fraction {
                color.withAlphaComponent(0.85).setFill()
                NSBezierPath(roundedRect: NSRect(x: box.minX, y: box.minY, width: max(barH, w * CGFloat(f)), height: barH),
                             xRadius: radius, yRadius: radius).fill()
            }
            if bars.count == 1 {
                NSColor.labelColor.withAlphaComponent(0.8).setStroke()
                let o = NSBezierPath(roundedRect: box.insetBy(dx: 0.5, dy: 0.5), xRadius: 4, yRadius: 4)
                o.lineWidth = 1; o.stroke()
            }
        }
        return true
    }
}
