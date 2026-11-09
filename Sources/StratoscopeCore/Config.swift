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

    /// Sets one key of the settings file, creating the file when it is missing. Everything else in the file stays. Returns false, and leaves
    /// the file as it is, when it cannot be read as a JSON object or cannot be written.
    @discardableResult
    public static func setValue(_ value: String, for key: String, home: String = NSHomeDirectory()) -> Bool {
        let path = settingsPath(home: home)
        var json: [String: Any] = [:]
        if let data = FileManager.default.contents(atPath: path) {
            guard let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return false }
            json = object
        }
        json[key] = value
        guard let out = try? JSONSerialization.data(withJSONObject: json, options: [.prettyPrinted, .sortedKeys]) else { return false }
        do {
            try FileManager.default.createDirectory(atPath: (path as NSString).deletingLastPathComponent, withIntermediateDirectories: true)
            try out.write(to: URL(fileURLWithPath: path), options: .atomic)
            return true
        } catch { return false }
    }

    public static func load(environment: [String: String] = ProcessInfo.processInfo.environment,
                            home: String = NSHomeDirectory()) -> Config {
        var json: [String: Any] = [:]
        if let data = FileManager.default.contents(atPath: settingsPath(home: home)),
           let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { json = obj }
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
        c.configured = ["STRATOSCOPE_ENGINE", "STRATOSCOPE_URL", "STRATOSCOPE_ROUTER_URL"].contains { !(environment[$0] ?? "").isEmpty }
            || ["servers", "engine", "url", "router"].contains { json[$0] != nil }
        return c
    }
}
