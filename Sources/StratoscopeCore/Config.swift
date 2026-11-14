import Foundation

/// Settings. Precedence: environment variable, then `~/.config/stratoscope.json`, then the default.
///
///     STRATOSCOPE_ENGINE      strata (default) | demo
///     STRATOSCOPE_URL         engine server (Strata default: http://127.0.0.1:8095)
///     STRATOSCOPE_ROUTER_URL  llama-swap style router in front of the engine ("none" disables)
///     STRATOSCOPE_LOG         engine log file (Strata default: newest *.log in ~/.local/share/strata/install)
///     STRATOSCOPE_WEBUI_URL   adds an "Open chat UI" link to the menu
///     STRATOSCOPE_COMPACT_AT  token count at which your client summarises the chat; drawn as a marker on the context bar
///                             (fallback for when no client config can be read)
///     STRATOSCOPE_CLIENT_MODEL    the model id your client config uses for this engine, if it differs from the name the
///                             engine reports (needed to match a client config when there is no router)
///     STRATOSCOPE_CLIENT_CONFIGS  "none" stops Stratoscope from reading the config files of clients such as Kilo and
///                             OpenCode to find that threshold
///     STRATOSCOPE_THEME       system (default) | light | dark: the look of the menu; the menu bar item always matches macOS
///     STRATOSCOPE_HISTORY_SMOOTHING  off | normal (default) | high | highest: the smoothing of the history graph in the menu
public struct Config {
    public var engine = "strata"
    public var url: String?
    public var router: String?
    public var log: String?
    public var webui: String?
    public var compactAt: Double?
    public var readClientConfigs = true
    public var clientModel: String?
    public var theme = Theme.system
    /// How strongly the history graph in the menu is smoothed: off, normal (default), high or highest.
    public var historySmoothing = HistorySmoothing.normal
    /// Somebody said where the server is: the settings or the environment name an engine, a URL or a router. Without it the app looks for the server.
    public private(set) var configured = false

    /// The look of the menu.
    public enum Theme: String { case system, light, dark }

    public init() {}

    /// The settings file: `~/.config/stratoscope.json`.
    public static func settingsPath(home: String = NSHomeDirectory()) -> String { home + "/.config/stratoscope.json" }

    /// Uses what the detection found: a router if there is one (it knows the model name and the loading state), else the engine.
    public mutating func use(_ server: DetectedServer) {
        if let r = server.router { router = r; url = nil } else if let u = server.url { url = u }
    }

    /// Things in the settings file that could not be used, in English, for the menu.
    public private(set) var problems: [String] = []

    /// The file "Open settings file" creates when there is none: every setting as a commented example. Nothing in it is active, so the app
    /// keeps looking for the server by itself until a line is switched on. The same conventions as the Windows file; comments are allowed
    /// (JSON5 reading).
    public static let template = """
    {
      // Stratoscope settings. To use a setting, delete the // in front of its line. The file is read again when you save it.

      // Where your engine is. Without a line here Stratoscope looks for it by itself.
      // "url": "http://127.0.0.1:8095",

      // A router in front of the engine (llama-swap): it knows the model name and says when a model is loading.
      // "router": "http://127.0.0.1:18080",

      // The look of the menu: "system" (default), "light" or "dark". The menu bar item always matches macOS.
      // "theme": "system",

      // How smooth the two minute history graph in the menu is: "off", "normal" (default), "high" or "highest".
      // "history_smoothing": "normal",

      // The token count at which your chat client summarises, drawn as a mark on the context bar. Only needed when Stratoscope cannot find it in the Kilo or OpenCode config.
      // "compact_at": 60000
    }

    """

    /// Writes the starter when there is no settings file yet, so the folder always holds a file that says what can be set. An existing file is
    /// never touched. A folder that cannot be written to is left alone: the app works without the file.
    public static func writeStarterIfMissing(home: String = NSHomeDirectory()) {
        let path = settingsPath(home: home)
        guard !FileManager.default.fileExists(atPath: path) else { return }
        try? FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
        try? template.write(toFile: path, atomically: true, encoding: .utf8)
    }

    /// The settings the file understands; anything else in it is named as a problem.
    private static let knownSettings: Set<String> = ["engine", "url", "router", "log", "webui", "client_model", "client_configs", "compact_at", "theme", "history_smoothing"]

    /// Sets one setting of the settings file, for the menu. The file is edited as text, so its comments stay: an active line gets the new value, a
    /// commented example is switched on, and otherwise a line is added at the top. The file is created from the commented starter when it is
    /// missing. Returns false, and leaves the file as it is, when it cannot be read as a JSON object, cannot be written, or the value is not a plain word.
    @discardableResult
    public static func setValue(_ value: String, for key: String, home: String = NSHomeDirectory()) -> Bool {
        guard plain(key), plain(value) else { return false }
        let path = settingsPath(home: home)
        var text = template
        if let data = FileManager.default.contents(atPath: path) {
            guard (try? JSONSerialization.jsonObject(with: data, options: [.json5Allowed])) is [String: Any],
                  let existing = String(data: data, encoding: .utf8) else { return false }
            text = existing
        }
        do {
            try FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            try edit(text, key: key, value: value).write(toFile: path, atomically: true, encoding: .utf8)
            return true
        } catch { return false }
    }

    private static func plain(_ text: String) -> Bool {
        !text.isEmpty && text.allSatisfy { $0.isLetter || $0.isNumber || $0 == "_" || $0 == "-" }
    }

    /// The text with the setting set. A trailing comma after an added line is fine: the file is read with JSON5 rules.
    static func edit(_ text: String, key: String, value: String) -> String {
        let name = NSRegularExpression.escapedPattern(for: key)
        let whole = NSRange(text.startIndex..., in: text)
        for (pattern, template) in [
            ("^([ \\t]*\"\(name)\"[ \\t]*:[ \\t]*)\"[^\"\\r\\n]*\"", "$1\"\(value)\""),                 // an active line gets the value
            ("^([ \\t]*)//[ \\t]*(\"\(name)\"[ \\t]*:[ \\t]*)\"[^\"\\r\\n]*\"", "$1$2\"\(value)\""),    // a commented example is switched on
        ] {
            if let re = try? NSRegularExpression(pattern: pattern, options: [.anchorsMatchLines]), let match = re.firstMatch(in: text, range: whole) {
                return (text as NSString).replacingCharacters(in: match.range, with: re.replacementString(for: match, in: text, offset: 0, template: template))
            }
        }
        if let re = try? NSRegularExpression(pattern: "^(?![ \\t]*//)[^\\r\\n]*?\\{", options: [.anchorsMatchLines]), let match = re.firstMatch(in: text, range: whole) {
            return (text as NSString).replacingCharacters(in: NSRange(location: match.range.location + match.range.length, length: 0), with: "\n  \"\(key)\": \"\(value)\",")
        }
        return text
    }

    public static func load(environment: [String: String] = ProcessInfo.processInfo.environment,
                            home: String = NSHomeDirectory()) -> Config {
        var json: [String: Any] = [:]
        var problems: [String] = []
        if let data = FileManager.default.contents(atPath: settingsPath(home: home)) {
            if let obj = (try? JSONSerialization.jsonObject(with: data, options: [.json5Allowed])) as? [String: Any] { json = obj }
            else if !data.isEmpty { problems.append("The settings file is not a JSON object: " + settingsPath(home: home)) }
        }
        problems += json.keys.sorted().filter { !knownSettings.contains($0) }.map { "Ignored \"\($0)\": there is no such setting." }
        func pick(_ envKeys: [String], _ key: String) -> String? {
            for k in envKeys { if let v = environment[k], !v.isEmpty { return v } }
            return json[key] as? String
        }
        var c = Config()
        if let e = pick(["STRATOSCOPE_ENGINE"], "engine") { c.engine = e }
        c.url = pick(["STRATOSCOPE_URL"], "url")
        c.router = pick(["STRATOSCOPE_ROUTER_URL"], "router")
        if c.router == "none" || c.router == "" { c.router = nil }
        c.log = pick(["STRATOSCOPE_LOG"], "log")
        c.webui = pick(["STRATOSCOPE_WEBUI_URL"], "webui")
        c.clientModel = pick(["STRATOSCOPE_CLIENT_MODEL"], "client_model")
        if pick(["STRATOSCOPE_CLIENT_CONFIGS"], "client_configs")?.lowercased() == "none" { c.readClientConfigs = false }
        if let k = pick(["STRATOSCOPE_COMPACT_AT"], "compact_at"), let v = Double(k) { c.compactAt = v }
        else if let v = (json["compact_at"] as? NSNumber)?.doubleValue { c.compactAt = v }
        if let t = pick(["STRATOSCOPE_THEME"], "theme")?.lowercased() { c.theme = t == "auto" ? .system : (Theme(rawValue: t) ?? .system) }
        if let h = pick(["STRATOSCOPE_HISTORY_SMOOTHING"], "history_smoothing")?.lowercased() {
            if let v = HistorySmoothing(rawValue: h) { c.historySmoothing = v }
            else { problems.append("Ignored history_smoothing \"\(h)\": use off, normal, high or highest.") }
        }
        c.configured = ["STRATOSCOPE_ENGINE", "STRATOSCOPE_URL", "STRATOSCOPE_ROUTER_URL"].contains { !(environment[$0] ?? "").isEmpty }
            || ["engine", "url", "router"].contains { json[$0] != nil }
        c.problems = problems
        return c
    }
}
