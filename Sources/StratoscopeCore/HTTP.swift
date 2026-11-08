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
}
