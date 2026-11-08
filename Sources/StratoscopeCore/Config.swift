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
///     STRATOSCOPE_CLIENT_CONFIGS  "none" stops Stratoscope from reading the config files of clients such as Kilo and
///                             OpenCode to find that threshold
public struct Config {
    public var engine = "strata"
    public var url: String?
    public var router: String?
    public var log: String?
    public var webui: String?
    public var compactAt: Double?
    public var readClientConfigs = true

    public init() {}

    public static func load(environment: [String: String] = ProcessInfo.processInfo.environment,
                            home: String = NSHomeDirectory()) -> Config {
        var json: [String: Any] = [:]
        if let data = FileManager.default.contents(atPath: home + "/.config/stratoscope.json"),
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
        if pick(["STRATOSCOPE_CLIENT_CONFIGS"], "client_configs")?.lowercased() == "none" { c.readClientConfigs = false }
        if let k = pick(["STRATOSCOPE_COMPACT_AT"], "compact_at"), let v = Double(k) { c.compactAt = v }
        else if let v = (json["compact_at"] as? NSNumber)?.doubleValue { c.compactAt = v }
        return c
    }
}
