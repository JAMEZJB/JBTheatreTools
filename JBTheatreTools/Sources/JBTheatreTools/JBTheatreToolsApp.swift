import SwiftUI

struct JBTheatreToolsApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        WindowGroup("JB Theatre Tools") {
            ContentView()
                .environmentObject(state)
        }
        .windowResizability(.contentSize)
    }
}
