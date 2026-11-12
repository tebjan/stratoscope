import Foundation

/// Tiny JSON-over-HTTP helper shared by the engine adapters.
public final class JSONFetcher {
    private let session: URLSession
    public init(timeout: TimeInterval = 1.5) {
        let c = URLSessionConfiguration.ephemeral
        c.timeoutIntervalForRequest = timeout; c.timeoutIntervalForResource = timeout + 0.5
        session = URLSession(configuration: c)
    }

    /// Calls `done` on the main queue with the decoded top-level object, or nil on any failure.
    public func get(_ url: String, _ done: @escaping ([String: Any]?) -> Void) {
        guard let u = URL(string: url) else { done(nil); return }
        session.dataTask(with: u) { data, _, _ in
            let obj = data.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            DispatchQueue.main.async { done(obj) }
        }.resume()
    }

    /// Calls `done` on the main queue with the HTTP status and the body as text (the custom server's Prometheus `/metrics` and its `/health`).
    /// Both are nil when nothing answered; the body is nil when it is not UTF-8.
    public func getText(_ url: String, _ done: @escaping (Int?, String?) -> Void) {
        guard let u = URL(string: url) else { done(nil, nil); return }
        session.dataTask(with: u) { data, response, _ in
            let status = (response as? HTTPURLResponse)?.statusCode
            let body = data.flatMap { String(data: $0, encoding: .utf8) }
            DispatchQueue.main.async { done(status, body) }
        }.resume()
    }

    /// Like `get`, for an answer that is a top-level array of objects (Basalt's `/slots`).
    public func getArray(_ url: String, _ done: @escaping ([[String: Any]]?) -> Void) {
        guard let u = URL(string: url) else { done(nil); return }
        session.dataTask(with: u) { data, _, _ in
            let rows = data.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [[String: Any]] }
            DispatchQueue.main.async { done(rows) }
        }.resume()
    }
}
