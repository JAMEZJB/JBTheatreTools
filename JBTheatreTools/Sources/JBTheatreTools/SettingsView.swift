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
    @State private var versionClicks: [Date] = []
    /// Shown only while dev mode is ON, or right after seven quick clicks on the version in THIS Settings
    /// session. Nothing about the reveal is stored: switch dev mode off and the switch is gone next time.
    @State private var devRevealed = false
    @AppStorage(AppState.devChannelKey) private var devChannel = false
    @AppStorage(AppState.autoCheckKey) private var autoCheck = UpdatePolicy.defaultInterval
    @AppStorage(AppState.notifyKey) private var notifyUpdates = true
    @AppStorage(AppState.autoInstallKey) private var autoInstall = false
    @AppStorage(AppState.menuBarKey) private var showMenuBar = false
    @State private var storage: (installed: Int64, cache: Int64)?
    @State private var clearingCache = false
    @State private var diagnosticsCopied = false

    /// The kit's panel heading (`.panel > h2`): a 10.5/600 caps micro-label in the tertiary text tone.
    private func panelLabel(_ title: String, _ symbol: String) -> some View {
        Label(title.uppercased(), systemImage: symbol)
            .font(JBFont.label)
            .tracking(JBFont.labelTracking)
            .foregroundStyle(Color.jbText3)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("Settings").font(JBFont.title).foregroundStyle(Color.jbText)
            HStack(alignment: .top, spacing: 18) {
                VStack(alignment: .leading, spacing: 18) { primaryColumn }
                    .frame(width: 430)
                VStack(alignment: .leading, spacing: 18) { secondaryColumn }
                    .frame(width: 430)
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
        .frame(width: 922)
        .background(Color.jbGround)
        .tint(.jbAccent)
        .task { await loadStorage() }
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

    /// Access, update checks, appearance, window, install location, hidden apps.
    @ViewBuilder
    private var primaryColumn: some View {
        GroupBox(label: panelLabel("Download access", "key.fill")) {
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
                        .font(JBFont.body)
                        .foregroundStyle(state.hasToken ? Color.jbOk : Color.jbText2)

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
                        .font(JBFont.small)
                    Text("Give it Contents: Read-only. Only the repos this token can access appear in the list.")
                        .font(JBFont.small)
                        .foregroundStyle(Color.jbText2)
                        .fixedSize(horizontal: false, vertical: true)
                } else {
                    Text(state.hasServerAuth
                         ? "Passphrase saved in your Keychain."
                         : "Enter the suite passphrase (ask James) — downloads are disabled until you do.")
                        .font(JBFont.body)
                        .foregroundStyle(state.hasServerAuth ? Color.jbOk : Color.jbText2)

                    SecureField("Suite passphrase…", text: $serverPassField)
                        .textFieldStyle(.roundedBorder)

                    Text("Capitalisation and spaces don't matter — type the phrase however you like.")
                        .font(JBFont.small)
                        .foregroundStyle(Color.jbText2)
                        .fixedSize(horizontal: false, vertical: true)

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

                    // Surface the effective relay host read-only (audit F2), so a non-default override
                    // is visible rather than silent.
                    if let base = state.serverBase, let host = URL(string: base)?.host {
                        Text("Relay: \(host)")
                            .font(JBFont.small)
                            .foregroundStyle(Color.jbText2)
                            .textSelection(.enabled)
                    }
                }
            }
            .padding(8)
        }

        GroupBox(label: panelLabel("Updates", "arrow.triangle.2.circlepath")) {
            VStack(alignment: .leading, spacing: 10) {
                // House convention: "mode" selectors use the native dropdown (default style),
                // not a segmented control — segmented is reserved for Light/Dark/System-style switches.
                Picker("Check for updates", selection: $updateMode) {
                    ForEach(UpdateCheckMode.allCases) { Text($0.label).tag($0) }
                }
                .labelsHidden()
                .tint(.selectorBlue)   // house rule 21: dropdowns are slate-blue, not the purple accent
                Text(updateModeHint)
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)

                Divider()

                HStack {
                    Text("JB Theatre Tools v\(state.currentVersion)").font(JBFont.body)
                        // Hidden Dev channel: seven clicks reveal it (Android's developer-options gesture).
                        .onTapGesture {
                            let now = Date()
                            versionClicks = versionClicks.filter { now.timeIntervalSince($0) < 3 } + [now]
                            if versionClicks.count >= 7 { devRevealed = true }   // 7 clicks within 3 s
                        }
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
                if devRevealed || devChannel {
                    Divider()
                    Toggle("Development builds on this Mac", isOn: $devChannel)
                        .onChange(of: devChannel) { _ in Task { await state.refreshAll() } }
                    Text("Also offer pre-release development builds (marked “dev”). They're verified exactly like releases, and a proper release always replaces them.")
                        .font(JBFont.small).foregroundStyle(Color.jbText2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(8)
        }

        GroupBox(label: panelLabel("Appearance", "circle.lefthalf.filled")) {
            Picker("Appearance", selection: $appearance) {
                ForEach(AppAppearance.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(8)
        }

        GroupBox(label: panelLabel("When I close the window", "xmark.circle")) {
            VStack(alignment: .leading, spacing: 8) {
                Picker("Close behaviour", selection: $closeBehavior) {
                    ForEach(CloseBehavior.allCases) { Text($0.label).tag($0) }
                }
                .labelsHidden()
                .tint(.selectorBlue)   // house rule 21: dropdowns are slate-blue, not the purple accent
                Text(closeBehavior == .quit
                     ? "Closing the window quits JB Theatre Tools."
                     : "Closing the window keeps it running in the Dock — click the Dock icon to reopen it.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
        }

        GroupBox(label: panelLabel("Install location", "folder")) {
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Install apps to the Applications folder", isOn: $installToApplications)
                Text("Off: apps stay inside the launcher. On: each installed app is placed in your Applications folder, so you can also open it from Launchpad or Spotlight without this launcher.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
        }

        if state.hasHiddenApps {
            GroupBox(label: panelLabel("Hidden apps", "eye.slash")) {
                VStack(alignment: .leading, spacing: 8) {
                    ForEach(state.hiddenRows) { row in
                        HStack {
                            Text(row.displayName).font(JBFont.body)
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
    }

    /// Show lock, automatic checks, the menu bar, storage and support.
    @ViewBuilder
    private var secondaryColumn: some View {
        GroupBox(label: panelLabel("Show lock", "lock.fill")) {
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Show lock", isOn: Binding(get: { state.showLock }, set: { state.setShowLock($0) }))
                Text("During a show: installs, updates and removals are paused (and automatic updates wait). Launching still works. ⌘L turns it on and off.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
        }

        GroupBox(label: panelLabel("Automatic checks", "clock.arrow.2.circlepath")) {
            VStack(alignment: .leading, spacing: 8) {
                Picker("While open, check every", selection: $autoCheck) {
                    ForEach(UpdatePolicy.intervals, id: \.raw) { Text($0.label).tag($0.raw) }
                }
                .tint(.selectorBlue)   // house rule 21: dropdowns are slate-blue, not the purple accent
                .disabled(updateMode != .everyLaunch)
                if updateMode != .everyLaunch {
                    Text("Scheduled checks run when “Check for updates” is set to Every launch.")
                        .font(JBFont.small).foregroundStyle(Color.jbText2)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Toggle("Notify me when updates are available", isOn: $notifyUpdates)
                Text("A notification when a check finds a new update while JB Theatre Tools is in the background — once per version.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
                Toggle("Install updates automatically", isOn: $autoInstall)
                Text("After each check, updates install on their own — never for held apps, apps that are open, or while show lock is on.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
        }

        GroupBox(label: panelLabel("Quick launch", "menubar.rectangle")) {
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Show in the menu bar", isOn: $showMenuBar)
                Text("A menu-bar icon that opens any installed app directly, checks for updates, or brings this window back.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
        }

        GroupBox(label: panelLabel("Storage", "internaldrive")) {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Installed apps").font(JBFont.body)
                    Spacer()
                    Text(storage.map { ByteSize.format($0.installed) } ?? "Calculating…")
                        .font(JBFont.body).foregroundStyle(Color.jbText2)
                }
                HStack {
                    Text("Download cache").font(JBFont.body)
                    Spacer()
                    Text(storage.map { ByteSize.format($0.cache) } ?? "Calculating…")
                        .font(JBFont.body).foregroundStyle(Color.jbText2)
                    Button(clearingCache ? "Clearing…" : "Clear") {
                        Task {
                            clearingCache = true
                            await state.clearCache()
                            await loadStorage()
                            clearingCache = false
                        }
                    }
                    .tint(.selectorBlue)
                    .disabled(clearingCache || !state.canClearCache || (storage?.cache ?? 0) == 0)
                }
                Text("The cache holds leftover downloads; clearing it never touches installed apps.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
        }

        GroupBox(label: panelLabel("Support", "questionmark.circle")) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Button("Copy Diagnostics") {
                        state.copyDiagnostics()
                        diagnosticsCopied = true
                    }
                    .tint(.selectorBlue)
                    if diagnosticsCopied {
                        Text("Copied").font(JBFont.small).foregroundStyle(Color.jbOk)
                    }
                }
                Text("A plain-text report — versions, settings, each app's state and recent log lines — to paste into a message to James. It never includes your token or passphrase.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
        }
    }

    private func loadStorage() async {
        storage = await state.storageSizes()
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
            Text("You're up to date (v\(v)).").font(JBFont.small).foregroundStyle(Color.jbOk)
        case .available(_, let latest):
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Text("v\(latest) is available.").font(JBFont.small).foregroundStyle(Color.jbInfo)
                    Button {
                        Task { await state.downloadLauncherUpdate() }
                    } label: {
                        if state.launcherDownloading { Text("Downloading…") } else { Text("Download Update") }
                    }
                    .font(JBFont.small)
                    .disabled(state.launcherDownloading)
                }
                if let msg = state.launcherDownloadMessage {
                    Text(msg).font(JBFont.labelRegular).foregroundStyle(Color.jbText3)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        case .unavailable(let message):
            Text(message).font(JBFont.small).foregroundStyle(Color.jbText2)
        }
    }
}
