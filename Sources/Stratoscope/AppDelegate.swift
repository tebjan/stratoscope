import Cocoa
import StratoscopeCore

final class AppDelegate: NSObject, NSApplicationDelegate {
    private let env = ProcessInfo.processInfo.environment
    private let config = Config.load()
    private lazy var provider: EngineProvider = makeProvider()
    private lazy var panels = Panels(compactAt: config.compactAt)

    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let menu = NSMenu()
    private var blocks: [Block] = []
    private var layout: [String] = []        // ids of the current menu sections; a change means "rebuild"
    private var links: [EngineLink] = []
    private var snapshotDone = false

    private func makeProvider() -> EngineProvider {
        if config.engine.lowercased() == DemoProvider.id {
            switch env["STRATOSCOPE_SCENARIO"] {
            case "decode": return DemoProvider(scenario: .decode)
            case "idle": return DemoProvider(scenario: .idle)
            default: return DemoProvider(scenario: .prefill)
            }
        }
        return EngineRegistry.make(config)
    }

    func applicationDidFinishLaunching(_ note: Notification) {
        item.isVisible = false
        item.button?.imagePosition = .imageLeft
        item.button?.font = .monospacedDigitSystemFont(ofSize: 12, weight: .semibold)
        item.menu = menu
        let timer = Timer(timeInterval: 2, repeats: true) { [weak self] _ in self?.poll() }
        RunLoop.main.add(timer, forMode: .common)
        poll()
    }

    private func poll() {
        provider.poll { [weak self] snap in self?.apply(snap) }
    }

    // MARK: Updating the UI

    private func apply(_ snap: EngineSnapshot?) {
        panels.snap = snap
        guard let s = snap else { item.isVisible = false; return }   // engine not running: hide completely
        item.isVisible = true
        updateMenuBarItem(s)
        rebuildMenuIfNeeded(s)
        blocks.forEach { $0.needsDisplay = true; $0.displayIfNeeded() }
        if env["STRATOSCOPE_DUMP"] != nil { print("[\(menuBarTitle(s))] \(s.model)"); fflush(stdout) }
        if let path = env["STRATOSCOPE_SNAPSHOT"], !s.isLoading, !snapshotDone {
            snapshotDone = true
            Screenshot.write(to: path, blocks: blocks, groups: groupSizes, links: links, snap: s,
                             title: menuBarTitle(s), progress: progress(s), version: "\(AppInfo.name) \(AppInfo.version)", dark: env["STRATOSCOPE_DARK"] != nil)
            NSApp.terminate(nil)
        }
    }

    private func menuBarTitle(_ s: EngineSnapshot) -> String {
        switch s.status {
        case .loading: return "loading"
        case .idle: return "ready"
        case .prefill: return Fmt.speed(s.live?.prefillTokensPerSecond) + (s.queued > 0 ? " +\(s.queued)" : "")
        case .thinking, .decode: return Fmt.speed(s.live?.decodeTokensPerSecond) + (s.queued > 0 ? " +\(s.queued)" : "")
        }
    }

    /// While a prompt is read: (fraction, cache quality) for the little bar in the menu bar.
    private func progress(_ s: EngineSnapshot) -> (Double, CacheHit)? {
        guard s.isReading, let l = s.live else { return nil }
        return (Double(l.percent) / 100, CacheHit.classify(prompt: l.promptTotal, reused: l.reusedEstimate))
    }

    private func updateMenuBarItem(_ s: EngineSnapshot) {
        if let (frac, hit) = progress(s) {
            item.button?.image = progressImage(frac, hit)
        } else {
            item.button?.image = NSImage(systemSymbolName: "cpu", accessibilityDescription: s.engine)
        }
        item.button?.title = " " + menuBarTitle(s)
        item.button?.toolTip = "\(s.engine): " + menuBarTitle(s)
    }

    // MARK: Menu

    private var groupSizes: [Int] = []

    private func rebuildMenuIfNeeded(_ s: EngineSnapshot) {
        let groups = panels.sections(for: s)
        let ids = groups.flatMap { $0.map(\.id) } + s.links.map(\.title)
        guard ids != layout else { return }
        layout = ids; links = s.links
        menu.removeAllItems(); blocks = []; groupSizes = []
        for (gi, group) in groups.enumerated() {
            if gi > 0 { menu.addItem(.separator()) }
            for sec in group {
                let b = Block(id: sec.id, height: sec.height, painter: sec.paint)
                blocks.append(b)
                let mi = NSMenuItem(); mi.view = b; menu.addItem(mi)
            }
            groupSizes.append(group.count)
        }
        if !s.links.isEmpty { menu.addItem(.separator()) }
        for (i, link) in s.links.enumerated() {
            let mi = NSMenuItem(title: link.title, action: #selector(openLink(_:)), keyEquivalent: "")
            mi.target = self; mi.tag = i; menu.addItem(mi)
        }
        menu.addItem(.separator())
        let v = NSMenuItem(title: AppInfo.label, action: nil, keyEquivalent: ""); v.isEnabled = false
        menu.addItem(v)
        let q = NSMenuItem(title: "Quit Stratoscope", action: #selector(quit), keyEquivalent: "q")
        q.target = self; menu.addItem(q)
    }

    @objc private func openLink(_ sender: NSMenuItem) {
        guard sender.tag < links.count else { return }
        NSWorkspace.shared.open(links[sender.tag].url)
    }

    @objc private func quit() { NSApp.terminate(nil) }
}
