import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var state: AppState
    @Binding var appearance: AppAppearance
    @Binding var updateMode: UpdateCheckMode
    @Environment(\.dismiss) private var dismiss
    @State private var tokenField = ""
    @State private var checkingLauncher = false
    @State private var launcherResult: AppState.LauncherCheck?

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Settings").font(.title2).bold()

            GroupBox(label: Label("GitHub access token", systemImage: "key.fill")) {
                VStack(alignment: .leading, spacing: 8) {
                    Text(state.hasToken
                         ? "A token is saved in your Keychain."
                         : "No token saved — downloads are disabled until you add one.")
                        .font(.callout)
                        .foregroundStyle(state.hasToken ? Color.green : Color.secondary)

                    SecureField("Paste a fine-grained PAT…", text: $tokenField)
                        .textFieldStyle(.roundedBorder)

                    HStack {
                        Button("Save") {
                            state.setToken(tokenField)
                            tokenField = ""
                            Task { await state.refreshAll() }
                        }
                        .keyboardShortcut(.return, modifiers: [])
                        .disabled(tokenField.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)

                        if state.hasToken {
                            Button("Remove", role: .destructive) { state.clearToken() }
                        }
                        Spacer()
                    }

                    Link("Create a fine-grained token on GitHub →",
                         destination: URL(string: "https://github.com/settings/personal-access-tokens/new")!)
                        .font(.caption)
                    Text("Give it Contents: Read-only on your tool repos.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(8)
            }

            GroupBox(label: Label("Updates", systemImage: "arrow.triangle.2.circlepath")) {
                VStack(alignment: .leading, spacing: 10) {
                    Picker("Check for updates", selection: $updateMode) {
                        ForEach(UpdateCheckMode.allCases) { Text($0.label).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    Text(updateModeHint)
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)

                    Divider()

                    HStack {
                        Text("JB Theatre Tools v\(state.currentVersion)").font(.callout)
                        Spacer()
                        Button(checkingLauncher ? "Checking…" : "Check for Updates") {
                            Task {
                                checkingLauncher = true
                                launcherResult = await state.checkLauncherUpdate()
                                checkingLauncher = false
                            }
                        }
                        .disabled(checkingLauncher)
                    }
                    if let result = launcherResult { launcherResultView(result) }
                }
                .padding(8)
            }

            GroupBox(label: Label("Appearance", systemImage: "circle.lefthalf.filled")) {
                Picker("Appearance", selection: $appearance) {
                    ForEach(AppAppearance.allCases) { Text($0.label).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .padding(8)
            }

            HStack {
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 470)
    }

    private var updateModeHint: String {
        switch updateMode {
        case .everyLaunch: return "Checks all apps and the launcher each time it opens."
        case .manual: return "Only checks when you press Refresh or Check for Updates."
        case .never: return "Never checks automatically. You can still install or update from the buttons."
        }
    }

    @ViewBuilder
    private func launcherResultView(_ result: AppState.LauncherCheck) -> some View {
        switch result {
        case .upToDate(let v):
            Text("You're up to date (v\(v)).").font(.caption).foregroundStyle(.green)
        case .available(_, let latest):
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Text("v\(latest) is available.").font(.caption).foregroundStyle(.blue)
                    Button {
                        Task { await state.downloadLauncherUpdate() }
                    } label: {
                        if state.launcherDownloading { Text("Downloading…") } else { Text("Download Update") }
                    }
                    .font(.caption)
                    .disabled(state.launcherDownloading)
                }
                if let msg = state.launcherDownloadMessage {
                    Text(msg).font(.caption2).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        case .unavailable(let message):
            Text(message).font(.caption).foregroundStyle(.secondary)
        }
    }
}
