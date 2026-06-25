import Foundation

// Custom entry point: if a recognised `--…` command appears ANYWHERE in the args, run the headless
// CLI and exit; otherwise the SwiftUI app launches. (Checking only args[0] missed invocations like
// `--token X --install helo`, which then wrongly opened the GUI — see THEATRE-01.) We match on a real
// verb rather than "any dash-arg" so OS-supplied launch flags don't suppress the GUI.
let arguments = Array(CommandLine.arguments.dropFirst())

if arguments.contains(where: { CLI.commands.contains($0) }) {
    CLI.run(args: arguments)
    exit(0)
}

JBTheatreToolsApp.main()
