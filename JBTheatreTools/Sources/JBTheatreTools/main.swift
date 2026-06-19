import Foundation

// Custom entry point: a recognised `--…` command runs the headless CLI and exits;
// otherwise the SwiftUI app launches. (SwiftUI's `App` exposes a static `main()` we call.)
let arguments = Array(CommandLine.arguments.dropFirst())

if let first = arguments.first, CLI.commands.contains(first) {
    CLI.run(args: arguments)
    exit(0)
}

JBTheatreToolsApp.main()
