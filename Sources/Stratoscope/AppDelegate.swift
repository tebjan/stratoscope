import Cocoa
import ServiceManagement
import StratoscopeCore

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let env = ProcessInfo.processInfo.environment
    private var config = Config.load()
    private lazy var provider: EngineProvider = makeProvider()
    private let finder = JSONFetcher(timeout: 1.0)
    private var lastDetection: Date?
    private var detecting = false
    private var offlineSince: Date?          // when the first "No engine found" was shown; the item hides a minute later
    private var settingsStamp: Date?
    private var loginItem: NSMenuItem?
    private var themeItems: [NSMenuItem] = []
    private var smoothingItems: [NSMenuItem] = []
    /// The last 60 seconds of speed and the adaptive scales: one sample per poll.
    private var history = SpeedHistory()
    private lazy var panels = config.engine.lowercased() == DemoProvider.id ? Panels(now: { DemoProvider.referenceDate }) : Panels()

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
        menu.delegate = self
        applyTheme()
        settingsStamp = settingsModified()
        let timer = Timer(timeInterval: 2, repeats: true) { [weak self] _ in self?.poll() }
        RunLoop.main.add(timer, forMode: .common)
        poll()
    }

    private func poll() {
        // An edit of the settings file is picked up like the Reload entry does it.
        if settingsModified() != settingsStamp { reload(); return }
        provider.poll { [weak self] snap in self?.apply(snap) }
    }

    // MARK: Settings

    private func settingsModified() -> Date? {
        (try? FileManager.default.attributesOfItem(atPath: Config.settingsPath()))?[.modificationDate] as? Date
    }

    /// Reads the settings again, starts the engine adapter over, and looks for the server again when none is named.
    private func reload() {
        config = Config.load()
        settingsStamp = settingsModified()
        provider = makeProvider()
        lastDetection = nil
        offlineSince = nil
        layout = []
        applyTheme()
        poll()
    }

    /// The choice is for the menu; the menu bar item always matches macOS.
    private func applyTheme() {
        switch config.theme {
        case .system: NSApp.appearance = nil
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        }
    }

    // MARK: Finding the server

    /// Nobody said where the server is: look for it, quietly, every half minute until it is found.
    private func detectIfDue() {
        guard !detecting, lastDetection.map({ Date().timeIntervalSince($0) > 30 }) ?? true else { return }
        detecting = true
        lastDetection = Date()
        let reader = config.readClientConfigs ? ClientConfigReader(configHome: env["XDG_CONFIG_HOME"]) : nil
        ServerDetection.find(fetcher: finder, clients: reader) { [weak self] found in
            guard let self = self else { return }
            self.detecting = false
            guard let first = found.first else { return }
            self.config.use(first)
            self.provider = self.makeProvider()
            self.poll()
        }
    }

    /// With nothing named and nothing found the item stays for the first minute, dimmed and saying so, so the settings can be reached; then it
    /// hides like any item without an engine, and the search goes on. A named server that does not answer hides it at once, as before.
    private func showOffline() {
        guard !config.configured else { item.isVisible = false; return }
        detectIfDue()
        let since = offlineSince ?? Date()
        offlineSince = since
        guard Date().timeIntervalSince(since) < 60 else { item.isVisible = false; return }
        item.isVisible = true
        item.button?.image = NSImage(systemSymbolName: "cpu", accessibilityDescription: AppInfo.name)
        item.button?.title = ""
        item.button?.toolTip = "No engine found"
        item.button?.appearsDisabled = true
        rebuildOfflineMenu()
    }

    // MARK: Updating the UI

    private func apply(_ snap: EngineSnapshot?) {
        panels.snap = snap
        history.add(now: Date(), snap)
        panels.history = history
        panels.smoothing = config.historySmoothing
        guard let s = snap else { showOffline(); return }
        offlineSince = nil
        item.isVisible = true
        item.button?.appearsDisabled = false
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
        case .loading: return "Loading"
        case .idle: return "Ready"
        case .prefill: return Fmt.speed(s.live?.prefillTokensPerSecond) + (s.queued > 0 ? " +\(s.queued)" : "")
        case .thinking, .decode: return Fmt.speed(s.live?.decodeTokensPerSecond) + (s.queued > 0 ? " +\(s.queued)" : "")
        case .running: return "running" + (s.queued > 0 ? " +\(s.queued)" : "")
        }
    }

    /// While a prompt is read: (fraction, cache quality) for the little bar in the menu bar.
    private func progress(_ s: EngineSnapshot) -> (Double, CacheHit)? {
        guard s.isReading, let l = s.live, let percent = l.percent else { return nil }
        return (Double(percent) / 100, CacheHit.classify(prompt: l.promptTotal ?? 0, reused: l.reusedEstimate ?? 0))
    }

    private func updateMenuBarItem(_ s: EngineSnapshot) {
        // One request that reads keeps the progress bar as it always was; every other running request is a bar of its own (see requestBarsImage).
        let bars = RequestBars.from(s)
        if bars.count == 1, bars[0].reading, let (frac, hit) = progress(s) {
            item.button?.image = progressImage(frac, hit)
        } else if !bars.isEmpty {
            let hit = s.live.flatMap { l in l.promptTotal.map { CacheHit.classify(prompt: $0, reused: l.reusedEstimate ?? 0) } }
            item.button?.image = requestBarsImage(bars, hit: hit, scaleMax: history.requestScale.max)
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
        addSettingsAndQuit()
    }

    private func rebuildOfflineMenu() {
        guard layout != ["offline"] else { return }
        layout = ["offline"]; links = []
        menu.removeAllItems(); blocks = []; groupSizes = []
        for line in ["No engine found", "Start Strata or your router, Stratoscope finds it by itself."] {
            let mi = NSMenuItem(title: line, action: nil, keyEquivalent: ""); mi.isEnabled = false
            menu.addItem(mi)
        }
        addSettingsAndQuit()
    }

    /// The end of every menu: the settings, the update check, the version and Quit.
    private func addSettingsAndQuit() {
        menu.addItem(.separator())
        let settings = NSMenuItem(title: "Settings", action: nil, keyEquivalent: "")
        let sub = NSMenu(title: "Settings")
        for problem in config.problems {
            let note = NSMenuItem(title: problem, action: nil, keyEquivalent: ""); note.isEnabled = false
            sub.addItem(note)
        }
        if !config.problems.isEmpty { sub.addItem(.separator()) }
        let login = NSMenuItem(title: "Start at login", action: #selector(toggleLoginItem), keyEquivalent: "")
        login.target = self; loginItem = login
        sub.addItem(login)
        let theme = NSMenuItem(title: "Theme", action: nil, keyEquivalent: "")
        let themes = NSMenu(title: "Theme")
        themeItems = []
        let choices: [(String, Config.Theme)] = [("System", .system), ("Light", .light), ("Dark", .dark)]
        for (title, choice) in choices {
            let mi = NSMenuItem(title: title, action: #selector(pickTheme(_:)), keyEquivalent: "")
            mi.target = self; mi.representedObject = choice.rawValue
            themes.addItem(mi); themeItems.append(mi)
        }
        theme.submenu = themes
        sub.addItem(theme)
        let smoothing = NSMenuItem(title: "History smoothing", action: nil, keyEquivalent: "")
        let smoothings = NSMenu(title: "History smoothing")
        smoothingItems = []
        for choice in HistorySmoothing.allCases {
            let mi = NSMenuItem(title: choice.title, action: #selector(pickSmoothing(_:)), keyEquivalent: "")
            mi.target = self; mi.representedObject = choice.rawValue
            smoothings.addItem(mi); smoothingItems.append(mi)
        }
        smoothing.submenu = smoothings
        sub.addItem(smoothing)
        sub.addItem(.separator())
        let open = NSMenuItem(title: "Open settings file", action: #selector(openSettingsFile), keyEquivalent: "")
        open.target = self; sub.addItem(open)
        let again = NSMenuItem(title: "Reload settings", action: #selector(reloadSettings), keyEquivalent: "")
        again.target = self; sub.addItem(again)
        settings.submenu = sub
        menu.addItem(settings)
        let updates = NSMenuItem(title: "Check for updates", action: #selector(checkForUpdates), keyEquivalent: "")
        updates.target = self; menu.addItem(updates)
        menu.addItem(.separator())
        let v = NSMenuItem(title: AppInfo.label, action: nil, keyEquivalent: ""); v.isEnabled = false
        menu.addItem(v)
        let q = NSMenuItem(title: "Quit Stratoscope", action: #selector(quit), keyEquivalent: "q")
        q.target = self; menu.addItem(q)
    }

    /// The ticks are read when the menu opens, so they are right even after a change made elsewhere.
    func menuNeedsUpdate(_ menu: NSMenu) {
        loginItem?.state = SMAppService.mainApp.status == .enabled ? .on : .off
        for mi in themeItems { mi.state = (mi.representedObject as? String) == config.theme.rawValue ? .on : .off }
        for mi in smoothingItems { mi.state = (mi.representedObject as? String) == config.historySmoothing.rawValue ? .on : .off }
    }

    @objc private func toggleLoginItem() {
        let service = SMAppService.mainApp
        do {
            if service.status == .enabled { try service.unregister() } else { try service.register() }
        } catch {
            tell("macOS did not let Stratoscope change its login item.", error.localizedDescription)
        }
    }

    @objc private func pickTheme(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String else { return }
        if Config.setValue(raw, for: "theme") { reload() }
        else { tell("Stratoscope could not change the theme in \(Config.settingsPath()).", "Is the file valid JSON?") }
    }

    @objc private func pickSmoothing(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String else { return }
        if Config.setValue(raw, for: "history_smoothing") { reload() }
        else { tell("Stratoscope could not change the history smoothing in \(Config.settingsPath()).", "Is the file valid JSON?") }
    }

    @objc private func openSettingsFile() {
        Config.writeStarterIfMissing()
        let path = Config.settingsPath()
        let url = URL(fileURLWithPath: path)
        if !NSWorkspace.shared.open(url) { NSWorkspace.shared.activateFileViewerSelecting([url]) }
    }

    @objc private func reloadSettings() { reload() }

    @objc private func checkForUpdates() {
        if let url = URL(string: AppInfo.releasesURL) { NSWorkspace.shared.open(url) }
    }

    private func tell(_ message: String, _ detail: String) {
        let alert = NSAlert()
        alert.messageText = message; alert.informativeText = detail
        alert.runModal()
    }

    @objc private func openLink(_ sender: NSMenuItem) {
        guard sender.tag < links.count else { return }
        NSWorkspace.shared.open(links[sender.tag].url)
    }

    @objc private func quit() { NSApp.terminate(nil) }
}
