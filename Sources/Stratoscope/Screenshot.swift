import Cocoa
import StratoscopeCore

/// Renders the menu to an opaque PNG without opening it (`STRATOSCOPE_SNAPSHOT=out.png`).
/// Used for the README screenshot, so it never has to be taken by hand. The output has no alpha channel.
enum Screenshot {
    static let scale: CGFloat = 2

    static func write(to path: String, blocks: [Block], groups: [Int], links: [EngineLink],
                      snap: EngineSnapshot, title: String, progress: (Double, CacheHit)?, version: String, dark: Bool) {
        let appearance = NSAppearance(named: dark ? .darkAqua : .aqua)!
        let margin: CGFloat = 44, barH: CGFloat = 26, sepH: CGFloat = 9, rowH: CGFloat = 22
        let blocksH = blocks.reduce(0) { $0 + $1.frame.height }
        let panelH = blocksH + CGFloat(max(0, groups.count - 1)) * sepH
            + (links.isEmpty ? 0 : sepH + CGFloat(links.count) * rowH) + sepH + 2 * rowH + sepH + 2 * rowH + 12
        let W = menuWidth + 2 * margin, H = barH + 6 + panelH + margin
        let px = Int(W * scale), py = Int(H * scale)

        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: py, bitsPerSample: 8,
                                         samplesPerPixel: 3, hasAlpha: false, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 32),
              let ctx = NSGraphicsContext(bitmapImageRep: rep) else { return }
        rep.size = NSSize(width: W, height: H)
        let saved = NSGraphicsContext.current
        NSGraphicsContext.current = ctx
        ctx.cgContext.scaleBy(x: scale, y: scale)
        appearance.performAsCurrentDrawingAppearance {
            // Backdrop
            let top = dark ? NSColor(srgbRed: 0.10, green: 0.12, blue: 0.22, alpha: 1) : NSColor(srgbRed: 0.62, green: 0.70, blue: 0.95, alpha: 1)
            let bottom = dark ? NSColor(srgbRed: 0.20, green: 0.10, blue: 0.28, alpha: 1) : NSColor(srgbRed: 0.85, green: 0.78, blue: 0.95, alpha: 1)
            NSGradient(starting: top, ending: bottom)!.draw(in: NSRect(x: 0, y: 0, width: W, height: H), angle: -60)

            // Menu bar strip with the status item highlighted
            (dark ? NSColor(white: 0.08, alpha: 0.55) : NSColor(white: 1, alpha: 0.55)).setFill()
            NSRect(x: 0, y: H - barH, width: W, height: barH).fill()
            let fg: NSColor = dark ? .white : .black
            let f = NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .semibold)
            let label = NSAttributedString(string: " " + title, attributes: [.font: f, .foregroundColor: fg])
            let icon: NSImage = progress.map { progressImage($0.0, $0.1) } ?? tinted(NSImage(systemSymbolName: "cpu", accessibilityDescription: nil)!, fg)
            let itemW = icon.size.width + label.size().width + 14
            let itemRect = NSRect(x: W - margin - itemW + 10, y: H - barH + 3, width: itemW, height: barH - 6)
            (dark ? NSColor(white: 1, alpha: 0.22) : NSColor(white: 0, alpha: 0.18)).setFill()
            NSBezierPath(roundedRect: itemRect, xRadius: 5, yRadius: 5).fill()
            icon.draw(at: NSPoint(x: itemRect.minX + 6, y: itemRect.midY - icon.size.height / 2), from: .zero, operation: .sourceOver, fraction: 1)
            label.draw(at: NSPoint(x: itemRect.minX + 6 + icon.size.width, y: itemRect.midY - label.size().height / 2))

            // Menu panel with shadow
            let panel = NSRect(x: margin, y: margin, width: menuWidth, height: panelH)
            NSGraphicsContext.saveGraphicsState()
            let shadow = NSShadow(); shadow.shadowBlurRadius = 24; shadow.shadowOffset = NSSize(width: 0, height: -8)
            shadow.shadowColor = NSColor.black.withAlphaComponent(dark ? 0.55 : 0.28); shadow.set()
            (dark ? NSColor(white: 0.17, alpha: 1) : NSColor(white: 0.96, alpha: 1)).setFill()
            NSBezierPath(roundedRect: panel, xRadius: 10, yRadius: 10).fill()
            NSGraphicsContext.restoreGraphicsState()
            NSColor.labelColor.withAlphaComponent(0.12).setStroke()
            let outline = NSBezierPath(roundedRect: panel.insetBy(dx: 0.5, dy: 0.5), xRadius: 10, yRadius: 10); outline.lineWidth = 1; outline.stroke()

            // Sections
            var y = panel.maxY - 6
            func separator() {
                y -= sepH / 2
                NSColor.labelColor.withAlphaComponent(0.14).setFill()
                NSRect(x: panel.minX + 10, y: y, width: menuWidth - 20, height: 1).fill()
                y -= sepH / 2
            }
            var idx = 0
            for (gi, count) in groups.enumerated() {
                if gi > 0 { separator() }
                for _ in 0..<count {
                    let b = blocks[idx]; idx += 1
                    let h = b.frame.height
                    y -= h
                    // Paint straight into this context, flipped so the painters' y-down coordinates work.
                    let cg = ctx.cgContext
                    cg.saveGState()
                    cg.translateBy(x: panel.minX, y: y + h); cg.scaleBy(x: 1, y: -1)
                    NSGraphicsContext.current = NSGraphicsContext(cgContext: cg, flipped: true)
                    b.painter?(b.bounds)
                    cg.restoreGState()
                    NSGraphicsContext.current = ctx
                }
            }
            func row(_ title: String, shortcut: String? = nil, dimmed: Bool = false) {
                y -= rowH
                let a: [NSAttributedString.Key: Any] = [.font: NSFont.menuFont(ofSize: 13),
                                                        .foregroundColor: dimmed ? NSColor.tertiaryLabelColor : NSColor.labelColor]
                NSAttributedString(string: title, attributes: a).draw(at: NSPoint(x: panel.minX + 14, y: y + 3))
                if let sc = shortcut {
                    let s = NSAttributedString(string: sc, attributes: [.font: NSFont.menuFont(ofSize: 13), .foregroundColor: NSColor.secondaryLabelColor])
                    s.draw(at: NSPoint(x: panel.maxX - 14 - s.size().width, y: y + 3))
                }
            }
            if !links.isEmpty { separator(); links.forEach { row($0.title) } }
            separator()
            row("Settings", shortcut: "›")
            row("Check for updates")
            separator()
            row(version, dimmed: true)
            row("Quit Stratoscope", shortcut: "⌘Q")
        }
        NSGraphicsContext.current = saved
        if let png = rep.representation(using: .png, properties: [:]) { try? png.write(to: URL(fileURLWithPath: path)) }
    }

    private static func tinted(_ image: NSImage, _ color: NSColor) -> NSImage {
        let out = NSImage(size: image.size)
        out.lockFocus()
        image.draw(at: .zero, from: .zero, operation: .sourceOver, fraction: 1)
        color.set(); NSRect(origin: .zero, size: image.size).fill(using: .sourceAtop)
        out.unlockFocus()
        return out
    }
}
