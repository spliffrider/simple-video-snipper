// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "SimpleVideoSnipper",
    platforms: [.macOS(.v15)],
    dependencies: [
        .package(url: "https://github.com/sindresorhus/KeyboardShortcuts", exact: "1.15.0"),
        .package(url: "https://github.com/apple/swift-nio", exact: "2.85.0"),
    ],
    targets: [
        .executableTarget(
            name: "SimpleVideoSnipper",
            dependencies: [
                "KeyboardShortcuts",
                .product(name: "NIOCore", package: "swift-nio"),
                .product(name: "NIOHTTP1", package: "swift-nio"),
                .product(name: "NIOPosix", package: "swift-nio"),
                .product(name: "NIOWebSocket", package: "swift-nio"),
            ]
        ),
    ],
    swiftLanguageModes: [.v5]
)
