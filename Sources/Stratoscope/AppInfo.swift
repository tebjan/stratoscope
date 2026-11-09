import Foundation

enum AppInfo {
    static let name = "Stratoscope"
    static let releasesURL = "https://github.com/yosephyne/stratoscope/releases/latest"
    /// Version and commit come from Info.plist, which `scripts/build-app.sh` fills in.
    /// Outside an app bundle (`swift run`) they read "dev".
    static var version: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev" }
    static var commit: String { Bundle.main.object(forInfoDictionaryKey: "StratoscopeCommit") as? String ?? "" }
    static var label: String { "\(name) \(version)" + (commit.isEmpty ? "" : " · \(commit)") }
}
