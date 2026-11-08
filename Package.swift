// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "Stratoscope",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "Stratoscope", targets: ["Stratoscope"]),
    ],
    targets: [
        // Engine-neutral model, configuration, formatting and the engine adapters. No AppKit.
        .target(name: "StratoscopeCore", path: "Sources/StratoscopeCore"),
        // The menu bar app: AppKit drawing and the status item.
        .executableTarget(name: "Stratoscope", dependencies: ["StratoscopeCore"], path: "Sources/Stratoscope"),
        .testTarget(name: "StratoscopeCoreTests", dependencies: ["StratoscopeCore"], path: "Tests/StratoscopeCoreTests"),
    ]
)
