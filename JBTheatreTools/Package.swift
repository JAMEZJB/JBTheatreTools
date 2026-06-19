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
        )
    ]
)
