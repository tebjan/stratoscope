import Foundation

// Where an AI client summarises ("compacts") the conversation is the client's decision, and no engine
// reports it. Some clients write what they need to work it out into their own config file: the
// address of the server they talk to and, per model, the context size and the output size.
// This file reads those numbers, read-only, and nothing else. It never touches API keys.

/// One model as a client config describes it.
public struct ClientModelLimit: Equatable {
    public var client: String      // "Kilo", "OpenCode"
    public var baseURL: String     // the server the client sends requests to
    public var model: String       // the model id the client uses
    public var context: Double
    public var output: Double

    public init(client: String, baseURL: String, model: String, context: Double, output: Double) {
        self.client = client; self.baseURL = baseURL; self.model = model
        self.context = context; self.output = output
    }

    /// Kilo and OpenCode summarise when the conversation no longer leaves room for an answer:
    /// context size minus output size. Observed with Kilo (65,536 for 90,112 and 24,576). Other
    /// clients may use another rule and need their own reader.
    public var compactAt: Double { context - output }
}

public enum ClientLimits {

    // MARK: Reading a config

    /// Reads the provider/model limits out of a Kilo or OpenCode config (JSON with comments allowed).
    /// Both layouts are understood: `provider.<id>.options.baseURL` and `providers.<id>.settings.baseURL`.
    public static func parse(_ text: String, client: String) -> [ClientModelLimit] {
        guard let data = stripJSONComments(text).data(using: .utf8),
              let root = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { return [] }
        var out: [ClientModelLimit] = []
        for key in ["provider", "providers"] {
            guard let providers = root[key] as? [String: Any] else { continue }
            for (_, value) in providers {
                guard let p = value as? [String: Any] else { continue }
                let opts = (p["options"] as? [String: Any]) ?? (p["settings"] as? [String: Any]) ?? [:]
                guard let base = (opts["baseURL"] ?? opts["baseUrl"]) as? String,
                      let models = p["models"] as? [String: Any] else { continue }
                for (name, mv) in models {
                    guard let m = mv as? [String: Any], let limit = m["limit"] as? [String: Any],
                          let context = (limit["context"] as? NSNumber)?.doubleValue,
                          let output = (limit["output"] as? NSNumber)?.doubleValue else { continue }
                    out.append(ClientModelLimit(client: client, baseURL: base, model: (m["modelID"] as? String) ?? name,
                                                context: context, output: output))
                }
            }
        }
        return out
    }

    // MARK: Matching

    /// The summarise threshold for the model that is running, or nil.
    /// Only configs that talk to this engine (same host and port) and name this model count. When
    /// several do and they disagree, or none does, nobody knows: nil, no marker.
    public static func compactAt(limits: [ClientModelLimit], endpoints: [String], model: String) -> Double? {
        let wanted = Set(endpoints.compactMap(hostAndPort))
        let values = Set(limits
            .filter { $0.model == model && hostAndPort($0.baseURL).map(wanted.contains) == true && $0.compactAt > 0 }
            .map(\.compactAt))
        return values.count == 1 ? values.first : nil
    }

    /// "127.0.0.1:8080" for any spelling of that address (localhost, default ports); nil if it is not a URL.
    static func hostAndPort(_ address: String) -> String? {
        guard let u = URL(string: address), var host = u.host?.lowercased() else { return nil }
        if host == "localhost" || host == "::1" { host = "127.0.0.1" }
        let port = u.port ?? (u.scheme == "https" ? 443 : 80)
        return "\(host):\(port)"
    }

    // MARK: JSON with comments

    /// Removes `//` and `/* */` comments and trailing commas, leaving strings (such as URLs) alone.
    static func stripJSONComments(_ text: String) -> String {
        let s = Array(text.unicodeScalars)
        var out = String.UnicodeScalarView()
        var i = 0
        while i < s.count {
            let c = s[i]
            if c == "\"" {                                   // a string: copy it as it is
                out.append(c); i += 1
                while i < s.count {
                    out.append(s[i])
                    if s[i] == "\\", i + 1 < s.count { out.append(s[i + 1]); i += 2; continue }
                    if s[i] == "\"" { i += 1; break }
                    i += 1
                }
            } else if c == "/", i + 1 < s.count, s[i + 1] == "/" {
                while i < s.count, s[i] != "\n" { i += 1 }
            } else if c == "/", i + 1 < s.count, s[i + 1] == "*" {
                i += 2
                while i + 1 < s.count, !(s[i] == "*" && s[i + 1] == "/") { i += 1 }
                i += 2
            } else if c == "," {                             // drop a comma that is followed by } or ]
                var j = i + 1
                while j < s.count, s[j] == " " || s[j] == "\n" || s[j] == "\t" || s[j] == "\r" { j += 1 }
                if j < s.count, s[j] == "}" || s[j] == "]" { i += 1 } else { out.append(c); i += 1 }
            } else {
                out.append(c); i += 1
            }
        }
        return String(out)
    }
}

/// Finds the config files of the clients we know and caches what they say until a file changes.
public final class ClientConfigReader {
    private let sources: [(client: String, path: String)]
    private var cache: [String: (modified: Date, limits: [ClientModelLimit])] = [:]

    /// `home` is only for tests.
    public init(home: String = NSHomeDirectory()) {
        sources = [
            ("Kilo", home + "/.config/kilo/kilo.jsonc"),
            ("Kilo", home + "/.config/kilo/kilo.json"),
            ("OpenCode", home + "/.config/opencode/opencode.json"),
            ("OpenCode", home + "/.config/opencode/opencode.jsonc"),
        ]
    }

    public func limits() -> [ClientModelLimit] {
        var all: [ClientModelLimit] = []
        for (client, path) in sources {
            guard let modified = (try? FileManager.default.attributesOfItem(atPath: path))?[.modificationDate] as? Date else {
                cache[path] = nil; continue
            }
            if let hit = cache[path], hit.modified == modified { all += hit.limits; continue }
            let text = (try? String(contentsOfFile: path, encoding: .utf8)) ?? ""
            let parsed = ClientLimits.parse(text, client: client)
            cache[path] = (modified, parsed)
            all += parsed
        }
        return all
    }

    public func compactAt(endpoints: [String], model: String) -> Double? {
        ClientLimits.compactAt(limits: limits(), endpoints: endpoints, model: model)
    }
}
