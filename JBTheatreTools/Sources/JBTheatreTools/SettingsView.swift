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
    @AppStorage(AppState.notifyKey) private var notifyUpdates = false
    @AppStorage(AppState.autoInstallKey) private var autoInstall = false
    @AppStorage(AppState.menuBarKey) private var showMenuBar = false
    @State private var storage: (installed: Int64, cache: Int64)?
    @State private var clearingCache = false
    @State private var diagnosticsCopied = false
    @State private var notificationsRefused = false

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
                .padding(.horizontal, 22)
            // The panels scroll only when the screen is too short for them (an older 13" laptop), so Done stays visible.
            // The scroll view spans the sheet edge to edge, so its scroller sits in the margin, not over the panels.
            ScrollView(.vertical) {
                HStack(alignment: .top, spacing: 18) {
                    VStack(alignment: .leading, spacing: 18) { primaryColumn }
                        .frame(width: 430)
                    VStack(alignment: .leading, spacing: 18) { secondaryColumn }
                        .frame(width: 430)
                }
                .padding(.horizontal, 22)
            }
            .frame(maxHeight: Self.panelsMaxHeight)
            .fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Open Log") { AppLog.shared.open() }
                    .tint(.selectorBlue)
                Button("Reset App Order") { state.resetAppOrder() }
                    .tint(.selectorBlue)
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            .padding(.horizontal, 22)
        }
        .padding(.vertical, 22)
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

    /// Access, update checks, appearance, window, the menu bar, hidden apps.
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
                         : "Enter the suite passphrase (ask whoever set up your access) — downloads are disabled until you do.")
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
            .frame(maxWidth: .infinity, alignment: .leading)
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
                if updateMode == .everyLaunch {
                    Picker("While open, check again", selection: $autoCheck) {
                        ForEach(UpdatePolicy.intervals, id: \.raw) { Text($0.label).tag($0.raw) }
                    }
                    .tint(.selectorBlue)   // house rule 21: dropdowns are slate-blue, not the purple accent
                }

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
            .frame(maxWidth: .infinity, alignment: .leading)
        }

        GroupBox(label: panelLabel("Appearance", "circle.lefthalf.filled")) {
            Picker("Appearance", selection: $appearance) {
                ForEach(AppAppearance.allCases) { Text($0.label).tag($0) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
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
            .frame(maxWidth: .infinity, alignment: .leading)
        }

        GroupBox(label: panelLabel("Quick launch", "menubar.rectangle")) {
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Show in the menu bar", isOn: $showMenuBar)
                Text(closeBehavior == .quit
                     ? "A menu-bar icon that opens any installed app directly, refreshes, or brings the window back — while JB Theatre Tools is running."
                     : "A menu-bar icon that opens any installed app directly, refreshes, or brings this window back — it stays after you close the window.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
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
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
    }

    /// Show lock, what happens when updates are found, install location, storage and support.
    @ViewBuilder
    private var secondaryColumn: some View {
        GroupBox(label: panelLabel("Show lock", "lock.fill")) {
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Show lock", isOn: Binding(get: { state.showLock }, set: { state.requestShowLock($0) }))
                Text("During a show: installs, updates and uninstalls are paused (and automatic updates wait). Launching still works. ⌘L turns it on and off.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
        }

        GroupBox(label: panelLabel("When updates are found", "bell")) {
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Notify me when updates are available", isOn: $notifyUpdates)
                    // Switching it on is the only thing that asks macOS for permission; if refused, it goes back off.
                    .onChange(of: notifyUpdates) { on in
                        guard on else { return }
                        Notifier.requestPermission { granted in
                            notificationsRefused = !granted
                            guard !granted else { return }
                            notifyUpdates = false
                        }
                    }
                Text("A notification when a check finds a new update while JB Theatre Tools is in the background — once per version.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
                if notificationsRefused {
                    Text("macOS has notifications turned off for JB Theatre Tools. Turn them on in System Settings → Notifications, then switch this on again.")
                        .font(JBFont.small).foregroundStyle(Color.jbWarn)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Toggle("Install updates automatically", isOn: $autoInstall)
                Text("After each check, updates install on their own — never for held apps, apps that are open, or while show lock is on.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
        }

        GroupBox(label: panelLabel("Install location", "folder")) {
            VStack(alignment: .leading, spacing: 8) {
                Toggle("Install apps to the Applications folder", isOn: $installToApplications)
                    .disabled(state.showLock)   // moving installed apps is paused under show lock
                if state.showLock {
                    Text("Paused while show lock is on.").font(JBFont.small).foregroundStyle(Color.jbInfo)
                }
                Text("Off: apps stay inside the launcher. On: each installed app is placed in your Applications folder, so you can also open it from Launchpad or Spotlight without this launcher.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
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
            .frame(maxWidth: .infinity, alignment: .leading)
        }

        GroupBox(label: panelLabel("Support", "questionmark.circle")) {
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 10) {
                    Button("Copy Diagnostics") {
                        state.copyDiagnostics()
                        diagnosticsCopied = true
                        DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { diagnosticsCopied = false }
                    }
                    .tint(.selectorBlue)
                    if diagnosticsCopied {
                        Text("Copied").font(JBFont.small).foregroundStyle(Color.jbOk)
                    }
                }
                Text("A plain-text report — versions, settings, each app's state and recent log lines — to paste into a message when you ask for help. It never includes your token or passphrase.")
                    .font(JBFont.small).foregroundStyle(Color.jbText2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(8)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    /// Room for the panels: the screen's usable height less the window title bar, the sheet's title, buttons and padding.
    private static var panelsMaxHeight: CGFloat {
        max(360, (NSScreen.main?.visibleFrame.height ?? 900) - 190)
    }

    private func loadStorage() async {
        storage = await state.storageSizes()
    }

    private var updateModeHint: String {
        switch updateMode {
        case .everyLaunch:
            return UpdatePolicy.interval(autoCheck) == nil
                ? "Checks all apps and the launcher each time it opens."
                : "Checks all apps and the launcher each time it opens, and again while it stays open."
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
                        Task { await state.updateLauncher() }
                    } label: {
                        if state.launcherDownloading { Text("Updating…") }
                        else { Text(state.launcherPendingRestart != nil ? "Restart" : "Update") }
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
