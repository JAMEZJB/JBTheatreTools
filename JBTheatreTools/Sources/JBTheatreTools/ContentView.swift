import SwiftUI

/// User-selectable window appearance. `.system` follows macOS.
enum AppAppearance: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }

    var label: String {
        switch self {
        case .system: return "System"
        case .light: return "Light"
        case .dark: return "Dark"
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}

/// When the launcher automatically checks for updates.
enum UpdateCheckMode: String, CaseIterable, Identifiable {
    case everyLaunch, manual, never
    var id: String { rawValue }

    var label: String {
        switch self {
        case .everyLaunch: return "Every launch"
        case .manual: return "Manual only"
        case .never: return "Never"
        }
    }
}

struct ContentView: View {
    @EnvironmentObject var state: AppState
    @AppStorage("theatre.appearance") private var appearance: AppAppearance = .system
    @AppStorage("theatre.updateMode") private var updateMode: UpdateCheckMode = .everyLaunch
    @State private var showSettings = false
    @State private var refreshing = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
            Divider()
            credit
        }
        .frame(minWidth: 600, minHeight: 440)
        .preferredColorScheme(appearance.colorScheme)
        .sheet(isPresented: $showSettings) {
            SettingsView(appearance: $appearance, updateMode: $updateMode).environmentObject(state)
        }
        .task { await firstRefresh() }
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "theatermasks.fill")
                .font(.system(size: 26))
                .foregroundStyle(.tint)
            VStack(alignment: .leading, spacing: 1) {
                Text("JB Theatre Tools").font(.headline)
                Text("Install, update & launch the JB tool suite")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button {
                Task { await refreshAll() }
            } label: {
                if refreshing { ProgressView().controlSize(.small) }
                else { Label("Refresh", systemImage: "arrow.clockwise") }
            }
            .disabled(refreshing || !state.hasToken)
            Button { showSettings = true } label: {
                Label("Settings", systemImage: "gearshape")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    @ViewBuilder
    private var content: some View {
        if let err = state.globalError {
            banner(err, systemImage: "exclamationmark.triangle.fill", tint: .red)
            Spacer()
        } else {
            if let v = state.launcherUpdateAvailable { launcherBanner(v) }
            if !state.hasToken { tokenBanner }
            List {
                ForEach($state.rows) { $row in
                    AppRowView(row: $row)
                    if row.id != state.rows.last?.id { Divider() }
                }
            }
            .listStyle(.plain)
        }
    }

    private func launcherBanner(_ version: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "arrow.down.circle.fill").foregroundStyle(.blue)
            VStack(alignment: .leading, spacing: 1) {
                Text("JB Theatre Tools \(version) is available").font(.callout).bold()
                Text(state.launcherDownloadMessage ?? "You're running v\(state.currentVersion).")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button {
                Task { await state.downloadLauncherUpdate() }
            } label: {
                if state.launcherDownloading { ProgressView().controlSize(.small) }
                else { Text("Download Update") }
            }
            .disabled(state.launcherDownloading)
        }
        .padding(12)
        .background(Color.blue.opacity(0.12))
    }

    private var tokenBanner: some View {
        HStack(spacing: 10) {
            Image(systemName: "key.fill").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 1) {
                Text("Add a GitHub token to enable downloads").font(.callout).bold()
                Text("Settings → paste a fine-grained PAT (Contents: read).")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Open Settings") { showSettings = true }
        }
        .padding(12)
        .background(Color.orange.opacity(0.12))
    }

    private var credit: some View {
        Text("Created by: James Breedon & Claude Code")
            .font(.caption2)
            .foregroundColor(.secondary)
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 6)
    }

    private func banner(_ text: String, systemImage: String, tint: Color) -> some View {
        HStack(spacing: 10) {
            Image(systemName: systemImage).foregroundStyle(tint)
            Text(text).font(.callout)
            Spacer()
        }
        .padding(12)
        .background(tint.opacity(0.12))
    }

    private func firstRefresh() async {
        guard updateMode == .everyLaunch, !refreshing else { return }
        if state.hasToken { await refreshAll() }
        await state.checkLauncherUpdate()
    }

    private func refreshAll() async {
        refreshing = true
        await state.refreshAll()
        refreshing = false
    }
}

/// A single catalog row: name, blurb, version line, status badge, and action buttons.
struct AppRowView: View {
    @EnvironmentObject var state: AppState
    @Binding var row: AppState.Row

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text(row.displayName).font(.body).bold()
                Text(row.app.blurb).font(.caption).foregroundStyle(.secondary)
                versionLine
                if row.busy {
                    ProgressView(value: row.progress)
                        .frame(maxWidth: 240)
                        .controlSize(.small)
                }
            }
            Spacer()
            statusBadge
            actions
        }
        .padding(.vertical, 10)
        .padding(.horizontal, 4)
    }

    private var versionLine: some View {
        HStack(spacing: 6) {
            Text("Installed: \(row.installed ?? "—")")
            Text("·").foregroundStyle(.secondary)
            Text("Latest: \(row.latest ?? "—")")
        }
        .font(.caption2)
        .foregroundStyle(.secondary)
    }

    @ViewBuilder
    private var statusBadge: some View {
        switch row.status {
        case .checking:
            ProgressView().controlSize(.small)
        case .upToDate:
            badge("Up to date", color: .green)
        case .updateAvailable:
            badge("Update", color: .blue)
        case .notInstalled:
            badge("Not installed", color: .secondary)
        case .noRelease:
            badge("No release", color: .secondary)
        case .missingAsset:
            badge("No macOS build", color: .orange)
        case .error(let msg):
            badge("Error", color: .red).help(msg)
        case .unknown:
            EmptyView()
        }
    }

    private func badge(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2).bold()
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background(color.opacity(0.15))
            .foregroundStyle(color)
            .clipShape(Capsule())
    }

    @ViewBuilder
    private var actions: some View {
        HStack(spacing: 8) {
            switch row.status {
            case .notInstalled:
                installButton(title: "Install")
            case .updateAvailable:
                installButton(title: "Update")
                launchButton
            case .upToDate:
                launchButton
            case .error:
                installButton(title: row.installed == nil ? "Install" : "Retry")
                if row.installed != nil { launchButton }
            default:
                EmptyView()
            }
            if !row.releases.isEmpty || row.installed != nil { rowMenu }
        }
    }

    /// Overflow menu: install a specific (older) version, or uninstall.
    private var rowMenu: some View {
        Menu {
            if !row.releases.isEmpty {
                Section("Install version") {
                    ForEach(row.releases) { rel in
                        Button { Task { await state.install(row.id, tag: rel.tagName) } }
                            label: { Text(versionLabel(rel)) }
                    }
                }
            }
            if row.installed != nil {
                Divider()
                Button("Uninstall \(row.displayName)", role: .destructive) {
                    state.uninstall(row.id)
                }
            }
        } label: {
            Image(systemName: "ellipsis.circle")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .disabled(row.busy)
        .help("More versions & uninstall")
    }

    private func versionLabel(_ rel: ReleaseInfo) -> String {
        var s = rel.tagName
        if rel.prerelease { s += " (pre-release)" }
        if rel.tagName == row.installed { s += "  ✓ installed" }
        return s
    }

    private func installButton(title: String) -> some View {
        Button(title) {
            Task { await state.install(row.id) }
        }
        .buttonStyle(.borderedProminent)
        .disabled(row.busy || row.latestAssetId == nil)
    }

    private var launchButton: some View {
        Button("Launch") { state.launch(row.id) }
            .disabled(row.busy)
    }
}
