import Foundation

/// The Prometheus text format (`name value` or `name{label="x"} value` per line, `#` comments), read into a table. Port of PrometheusText.cs. Shared by the llama.cpp and the custom readers.
enum PrometheusText {
    /// The finite values of every series whose name starts with `prefix`, keyed by the series text without the prefix and with its
    /// labels as sent (`admission_blocked{reason="kv"}`). A value that is not a finite number is left out (unknown).
    static func parse(_ text: String?, prefix: String) -> [String: Double] {
        var values: [String: Double] = [:]
        guard let text = text else { return values }
        for raw in text.components(separatedBy: .newlines) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            guard line.hasPrefix(prefix) else { continue }
            // The series ends at the closing brace of its labels, else at the first space; the value follows (then an optional timestamp).
            let split: String.Index
            if let close = line.firstIndex(of: "}") { split = line.index(after: close) }
            else if let space = line.firstIndex(of: " ") { split = space }
            else { continue }
            let nameStart = line.index(line.startIndex, offsetBy: prefix.count)
            guard split > nameStart else { continue }
            guard let first = line[split...].split(separator: " ").first, let value = Double(first), value.isFinite else { continue }
            values[String(line[nameStart..<split])] = value
        }
        return values
    }
}
