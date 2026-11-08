import Foundation

public enum Fmt {
    private static let locale = Locale(identifier: "en_US")

    public static func number(_ v: Double?, _ digits: Int = 0) -> String {
        guard let v = v, v.isFinite else { return "–" }
        let f = NumberFormatter(); f.locale = locale; f.numberStyle = .decimal
        f.minimumFractionDigits = digits; f.maximumFractionDigits = digits
        return f.string(from: NSNumber(value: v)) ?? "–"
    }

    /// Speed for the menu bar title: always at least three digits wide (figure spaces) and "0 t/s"
    /// instead of a dash, so the title does not jump when the speed goes from 0 to 222.
    public static func speed(_ v: Double?) -> String {
        let n = number(max(0, v ?? 0))
        return String(repeating: "\u{2007}", count: max(0, 3 - n.count)) + n + " t/s"
    }

    public static func gib(_ bytes: Double?) -> String {
        bytes == nil ? "–" : number(bytes! / 1_073_741_824, 1)
    }

    public static func duration(_ seconds: Double) -> String {
        let t = max(0, Int(seconds)); let h = t / 3600, m = (t % 3600) / 60, s = t % 60
        if h > 0 { return "\(h) h \(m) min" }
        return m > 0 ? "\(m) min \(s) s" : "\(s) s"
    }
}
