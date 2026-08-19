import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var state: AppState
    @Binding var appearance: AppAppearance
    @Binding var updateMode: UpdateCheckMode
    @Binding var closeBehavior: CloseBehavior
    @Binding var installToApplications: Bool
    @Binding var authMode: AuthMode
    @Environment(\.dismiss) private var dismiss
    @State private var tokenField = ""
    @State private var serverPassField = ""
    @State private var checkingLauncher = false
    @State private var launcherResult: AppState.LauncherCheck?
    @State private var confirmingTokenRemove = false
    @State private var confirmingServerRemove = false

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Settings").font(.title2).bold()

            GroupBox(label: Label("Download access", systemImage: "key.fill")) {
                VStack(alignment: .leading, spacing: 8) {
                    // House convention: "mode" selectors use the native dropdown (default style).
                    Picker("Downloads via", selection: $authMode) {
                        ForEach(AuthMode.allCases) { Text($0.label).tag($0) }
                    }
                    .tint(.selectorBlue)   // house rule 21: dropdowns are slate-blue, not the purple accent

                    if authMode == .token {
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
                                Button("Remove", role: .destructive) { confirmingTokenRemove = true }
                            }
                            Spacer()
                        }

                        Link("Create a fine-grained token on GitHub →",
                             destination: URL(string: "https://github.com/settings/personal-access-tokens/new")!)
                            .font(.caption)
                        Text("Give it Contents: Read-only. Only the repos this token can access appear in the list.")
                            .font(.caption)
                            .foregroundStyle(.secondary)
                            .fixedSize(horizontal: false, vertical: true)
                    } else {
                        Text(state.hasServerAuth
                             ? "Passphrase saved in your Keychain."
                             : "Enter the suite passphrase (ask James) — downloads are disabled until you do.")
                            .font(.callout)
                            .foregroundStyle(state.hasServerAuth ? Color.green : Color.secondary)

                        SecureField("Suite passphrase…", text: $serverPassField)
                            .textFieldStyle(.roundedBorder)

                        HStack {
                            Button("Save") {
                                state.setServerPassphrase(serverPassField)
                                serverPassField = ""
                                Task { await state.refreshAll() }
                            }
                            .keyboardShortcut(.return, modifiers: [])
                            .disabled(serverPassField.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                      || state.serverBase == nil)

                            if state.hasServerAuth {
                                Button("Remove", role: .destructive) { confirmingServerRemove = true }
                            }
                            Spacer()
                        }
                    }
                }
                .padding(8)
            }

            GroupBox(label: Label("Updates", systemImage: "arrow.triangle.2.circlepath")) {
                VStack(alignment: .leading, spacing: 10) {
                    // House convention: "mode" selectors use the native dropdown (default style),
                    // not a segmented control — segmented is reserved for Light/Dark/System-style switches.
                    Picker("Check for updates", selection: $updateMode) {
                        ForEach(UpdateCheckMode.allCases) { Text($0.label).tag($0) }
                    }
                    .labelsHidden()
                    .tint(.selectorBlue)   // house rule 21: dropdowns are slate-blue, not the purple accent
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

            GroupBox(label: Label("When I close the window", systemImage: "xmark.circle")) {
                VStack(alignment: .leading, spacing: 8) {
                    Picker("Close behaviour", selection: $closeBehavior) {
                        ForEach(CloseBehavior.allCases) { Text($0.label).tag($0) }
                    }
                    .labelsHidden()
                    .tint(.selectorBlue)   // house rule 21: dropdowns are slate-blue, not the purple accent
                    Text(closeBehavior == .quit
                         ? "Closing the window quits JB Theatre Tools."
                         : "Closing the window keeps it running in the Dock — click the Dock icon to reopen it.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(8)
            }

            GroupBox(label: Label("Install location", systemImage: "folder")) {
                VStack(alignment: .leading, spacing: 8) {
                    Toggle("Install apps to the Applications folder", isOn: $installToApplications)
                    Text("Off: apps stay inside the launcher. On: each installed app is placed in your Applications folder, so you can also open it from Launchpad or Spotlight without this launcher.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(8)
            }

            if state.hasHiddenApps {
                GroupBox(label: Label("Hidden apps", systemImage: "eye.slash")) {
                    VStack(alignment: .leading, spacing: 8) {
                        ForEach(state.hiddenRows) { row in
                            HStack {
                                Text(row.displayName).font(.callout)
                                Spacer()
                                Button("Show") { state.setHidden(row.id, false) }
                                    .tint(.selectorBlue)
                            }
                        }
                        HStack {
                            Spacer()
                            Button("Show All") { state.showAllHidden() }
                                .tint(.selectorBlue)
                        }
                    }
                    .padding(8)
                }
            }

            HStack {
                Button("Open Log") { AppLog.shared.open() }
                    .tint(.selectorBlue)
                Button("Reset App Order") { state.resetAppOrder() }
                    .tint(.selectorBlue)
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(22)
        .frame(width: 470)
        .tint(.jbAccent)
        // When the setting changes, offer to move already-installed apps so they don't end up split
        // across both locations. (single-param onChange for macOS 13 compatibility)
        .onChange(of: installToApplications) { newValue in
            state.installLocationChanged(toApplications: newValue)
        }
        .alert("Move installed apps?", isPresented: Binding(
            get: { state.relocationPrompt != nil },
            set: { if !$0 { state.cancelRelocation() } }
        )) {
            Button("Move") { Task { await state.performRelocation(toApplications: installToApplications) } }
            Button("Not now", role: .cancel) { state.cancelRelocation() }
        } message: {
            if let p = state.relocationPrompt {
                Text("You have \(p.count) installed app\(p.count == 1 ? "" : "s") in \(p.toApplications ? "the launcher" : "the Applications folder"). Move \(p.count == 1 ? "it" : "them") to \(p.toApplications ? "the Applications folder" : "the launcher") now? (New installs already go there.)")
            }
        }
        .alert("Some apps couldn’t move", isPresented: Binding(
            get: { state.relocationNote != nil },
            set: { if !$0 { state.relocationNote = nil } }
        )) {
            Button("OK", role: .cancel) { state.relocationNote = nil }
        } message: {
            Text(state.relocationNote ?? "")
        }
        .confirmationDialog("Remove saved token?", isPresented: $confirmingTokenRemove, titleVisibility: .visible) {
            Button("Remove", role: .destructive) { state.clearToken() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("You'll need to paste a token again before you can install or update apps.")
        }
        .confirmationDialog("Remove server passphrase?", isPresented: $confirmingServerRemove, titleVisibility: .visible) {
            Button("Remove", role: .destructive) { state.clearServerAuth() }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("You'll need to enter the passphrase again before you can install or update apps.")
        }
        // Switching modes invalidates cached release state (different credentials see different repos).
        // (single-param onChange for macOS 13 compatibility)
        .onChange(of: authMode) { _ in
            state.authModeChanged()
        }
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
