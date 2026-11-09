import Foundation
import Testing
@testable import StratoscopeCore

/// Answers from a table, at once: what the servers say, without a network.
private final class FakeFetcher: JSONGetting {
    var answers: [String: [String: Any]] = [:]
    func get(_ url: String, _ done: @escaping ([String: Any]?) -> Void) { done(answers[url]) }
}

private func temporaryHome() throws -> String {
    let home = NSTemporaryDirectory() + "stratoscope-test-" + UUID().uuidString
    try FileManager.default.createDirectory(atPath: home + "/.config/kilo", withIntermediateDirectories: true)
    return home
}

@Suite struct ServerDetectionTests {
    private let router = "http://127.0.0.1:18080"
    private let engine = "http://127.0.0.1:8095"
    private let running: [String: Any] = ["running": [[String: Any]]()]
    private let metrics: [String: Any] = ["engine": ["model": "Demo"]]

    @Test func testLocalAndPrivateAddressesOnly() {
        for host in ["localhost", "127.0.0.1", "::1", "10.1.2.3", "172.16.0.1", "172.31.255.255", "192.168.1.5", "100.64.0.1", "100.127.1.1"] {
            #expect(ServerDetection.isLocalNetwork(host: host), "\(host)")
        }
        for host in ["api.openai.com", "8.8.8.8", "172.32.0.1", "100.128.0.1", "192.169.0.1", "fcdn.example.com", "1.2.3"] {
            #expect(!ServerDetection.isLocalNetwork(host: host), "\(host)")
        }
    }

    @Test func testClientAddressesComeFirstOncePerServerAndOnlyLocalOnes() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try """
        {"provider": {"local": {"options": {"baseURL": "http://127.0.0.1:18080/v1"}, "models": {"qwen": {"limit": {"context": 131072, "output": 8192}}}},
                      "cloud": {"options": {"baseURL": "https://api.openai.com/v1"}, "models": {"gpt": {"limit": {"context": 1, "output": 1}}}}}}
        """.write(toFile: home + "/.config/kilo/kilo.jsonc", atomically: true, encoding: .utf8)
        let candidates = ServerDetection.candidates(clients: ClientConfigReader(home: home))
        #expect(candidates.first == router)
        #expect(!candidates.contains { $0.contains("openai") })
        #expect(candidates.count == Set(candidates).count)
        #expect(candidates.contains(engine))
    }

    @Test func testARouterWinsOverAnEngine() {
        let fetcher = FakeFetcher()
        fetcher.answers[router + "/running"] = running
        fetcher.answers[engine + "/metrics"] = metrics
        var found: [DetectedServer] = []
        ServerDetection.find(fetcher: fetcher, clients: nil) { found = $0 }
        #expect(found == [DetectedServer(router: router)])
    }

    @Test func testAnEngineAloneIsFoundAndNothingGivesAnEmptyList() {
        let fetcher = FakeFetcher()
        var found: [DetectedServer]? = nil
        ServerDetection.find(fetcher: fetcher, clients: nil) { found = $0 }
        #expect(found?.isEmpty == true)
        fetcher.answers[engine + "/metrics"] = metrics
        ServerDetection.find(fetcher: fetcher, clients: nil) { found = $0 }
        #expect(found == [DetectedServer(url: engine)])
    }

    @Test func testTheFoundServerReplacesTheDefaultOne() {
        var config = Config()
        config.use(DetectedServer(router: router))
        #expect(config.router == router)
        config.use(DetectedServer(url: engine))
        #expect(config.url == engine)
    }
}

@Suite struct SettingsFileTests {
    @Test func testSystemIsTheDefaultThemeAndNothingNamedMeansNotConfigured() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        let config = Config.load(environment: [:], home: home)
        #expect(config.theme == .system)
        #expect(!config.configured)
    }

    @Test func testThemeIsReadFromTheFileAndTheEnvironmentWins() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try #"{"theme": "dark"}"#.write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        #expect(Config.load(environment: [:], home: home).theme == .dark)
        #expect(Config.load(environment: ["STRATOSCOPE_THEME": "light"], home: home).theme == .light)
        #expect(Config.load(environment: ["STRATOSCOPE_THEME": "auto"], home: home).theme == .system)
        #expect(Config.load(environment: ["STRATOSCOPE_THEME": "blue"], home: home).theme == .system)
    }

    @Test func testAFileWithOnlyAThemeStillLetsTheAppLookForTheServer() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try #"{"theme": "dark"}"#.write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        #expect(!Config.load(environment: [:], home: home).configured)
        try #"{"theme": "dark", "url": "http://127.0.0.1:9"}"#.write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        #expect(Config.load(environment: [:], home: home).configured)
        #expect(Config.load(environment: ["STRATOSCOPE_ROUTER_URL": "http://127.0.0.1:18080"], home: temporaryHomeOrEmpty()).configured)
    }

    @Test func testSettingOneKeyKeepsTheRestAndCreatesAMissingFile() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try #"{"url": "http://127.0.0.1:9", "compact_at": 60000}"#.write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        #expect(Config.setValue("dark", for: "theme", home: home))
        let config = Config.load(environment: [:], home: home)
        #expect(config.theme == .dark)
        #expect(config.url == "http://127.0.0.1:9")
        #expect(config.compactAt == 60000)

        let fresh = NSTemporaryDirectory() + "stratoscope-test-" + UUID().uuidString
        defer { try? FileManager.default.removeItem(atPath: fresh) }
        #expect(Config.setValue("light", for: "theme", home: fresh))
        #expect(Config.load(environment: [:], home: fresh).theme == .light)
    }

    @Test func testAFileThatIsNotAnObjectIsLeftAlone() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try "[1, 2]".write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        #expect(!Config.setValue("dark", for: "theme", home: home))
        #expect(try String(contentsOfFile: Config.settingsPath(home: home), encoding: .utf8) == "[1, 2]")
    }

    @Test func testTheStarterChangesNothingAndKeepsTheServerSearchOn() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try Config.template.write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        let config = Config.load(environment: [:], home: home)
        #expect(config.problems.isEmpty)
        #expect(!config.configured)
        #expect(config.theme == .system)
    }

    @Test func testTheStarterIsWrittenOnceAndAnExistingFileIsNeverTouched() throws {
        let home = NSTemporaryDirectory() + "stratoscope-test-" + UUID().uuidString
        defer { try? FileManager.default.removeItem(atPath: home) }
        Config.writeStarterIfMissing(home: home)
        #expect(try String(contentsOfFile: Config.settingsPath(home: home), encoding: .utf8) == Config.template)
        try #"{"url": "http://127.0.0.1:9"}"#.write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        Config.writeStarterIfMissing(home: home)
        #expect(try String(contentsOfFile: Config.settingsPath(home: home), encoding: .utf8) == #"{"url": "http://127.0.0.1:9"}"#)
    }

    @Test func testCommentsAndTrailingCommasAreAllowed() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try "{ // a note\n  \"url\": \"http://127.0.0.1:9\", // another\n  \"theme\": \"dark\",\n}".write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        let config = Config.load(environment: [:], home: home)
        #expect(config.url == "http://127.0.0.1:9")
        #expect(config.theme == .dark)
        #expect(config.problems.isEmpty)
    }

    @Test func testASettingThatDoesNotExistIsNamed() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try #"{"display": "both", "theme": "dark"}"#.write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        let config = Config.load(environment: [:], home: home)
        #expect(config.problems.contains { $0.contains("display") })
        #expect(!config.problems.contains { $0.contains("theme") })
        #expect(config.theme == .dark)
    }

    @Test func testChoosingTheThemeSwitchesOnItsExampleAndKeepsEveryComment() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        #expect(Config.setValue("dark", for: "theme", home: home))
        var text = try String(contentsOfFile: Config.settingsPath(home: home), encoding: .utf8)
        #expect(text.contains("// Stratoscope settings"))
        #expect(text.contains("  \"theme\": \"dark\","))
        #expect(!text.contains("// \"theme\""))
        #expect(text.contains("// \"url\""))
        let config = Config.load(environment: [:], home: home)
        #expect(config.theme == .dark)
        #expect(config.problems.isEmpty)
        #expect(!config.configured)

        #expect(Config.setValue("light", for: "theme", home: home))
        text = try String(contentsOfFile: Config.settingsPath(home: home), encoding: .utf8)
        #expect(text.contains("  \"theme\": \"light\","))
        #expect(Config.load(environment: [:], home: home).theme == .light)
    }

    @Test func testWithoutAnExampleALineIsAddedAtTheTopAndTheCommentsStay() throws {
        let home = try temporaryHome()
        defer { try? FileManager.default.removeItem(atPath: home) }
        try "{ // my own note\n  \"url\": \"http://127.0.0.1:9\" // and another\n}".write(toFile: Config.settingsPath(home: home), atomically: true, encoding: .utf8)
        #expect(Config.setValue("dark", for: "theme", home: home))
        let text = try String(contentsOfFile: Config.settingsPath(home: home), encoding: .utf8)
        #expect(text.contains("// my own note"))
        #expect(text.contains("// and another"))
        let config = Config.load(environment: [:], home: home)
        #expect(config.theme == .dark)
        #expect(config.url == "http://127.0.0.1:9")
    }

    private func temporaryHomeOrEmpty() -> String { NSTemporaryDirectory() + "stratoscope-none-" + UUID().uuidString }
}
