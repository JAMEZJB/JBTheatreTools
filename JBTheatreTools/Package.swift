// swift-tools-version:5.7
import PackageDescription

let package = Package(
    name: "JBTheatreTools",
    platforms: [
        .macOS(.v13)
    ],
    targets: [
        .executableTarget(
            name: "JBTheatreTools",
            path: "Sources/JBTheatreTools"
        ),
        // Unit tests for the pure logic that gates security decisions (signature verification,
        // BLAKE2b, SHA256SUMS parsing, version comparison, catalog/variant resolution). Run: `swift test`.
        // Fixtures/ holds the REAL signed manifest from the public v1.15.0 release.
        .testTarget(
            name: "JBTheatreToolsTests",
            dependencies: ["JBTheatreTools"],
            path: "Tests/JBTheatreToolsTests",
            resources: [.copy("Fixtures")]
        )
    ]
)
