import SwiftUI

/// Shown right before the macOS Keychain password prompt that appears on the first token read after
/// an update — so non-technical users understand the (normal, expected) system dialog that follows.
struct KeychainExplainerView: View {
    let onContinue: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: "key.fill")
                    .font(.title)
                    .foregroundStyle(.tint)
                Text("After an update, macOS needs your permission again")
                    .font(.headline)
            }

            Text("Because this is a new version of JB Theatre Tools, macOS will now ask for your "
                 + "Mac login password so the app can use the GitHub token you already saved. "
                 + "This is normal and expected after every update.")
                .fixedSize(horizontal: false, vertical: true)

            Text("Tip: click **Always Allow** in the next dialog and it won't ask again until the "
                 + "next update.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            HStack {
                Spacer()
                Button("Continue") { onContinue() }
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 430)
    }
}
