import Foundation
import SwiftUI
import AppKit

/// Observable view model backing the launcher UI. All work runs on the main actor;
/// network calls suspend rather than block, so the UI stays responsive.
@MainActor
final class AppState: ObservableObject {

    enum Status: Equatable {
        case unknown
        case checking
        case noRelease
        case missingAsset
        case notInstalled
        case upToDate
        case updateAvailable
        case error(String)
    }

    /// Result of a launcher self-update check.
    enum LauncherCheck {
        case upToDate(String)
        case available(current: String, latest: String)
        case unavailable(String)
    }

    struct Row: Identifiable {
        let app: CatalogApp
        var id: String { app.id }
        var latest: String?
        var latestAssetId: Int?
        var installed: String?
        var releases: [ReleaseInfo] = []
        var status: Status = .unknown
        var busy: Bool = false
        var progress: Double = 0
        /// The installed app's self-declared name (read from its bundle); overrides the catalog name.
        var resolvedName: String?
        /// Name to show: the installed app's own name when available, else the catalog name.
        var displayName: String { resolvedName ?? app.name }
    }

    @Published var rows: [Row] = []
    @Published var hasToken: Bool = TokenStore.exists()
    @Published var globalError: String?
    /// Set to the latest tag when a newer launcher release exists (drives the in-app banner).
    @Published var launcherUpdateAvailable: String?
    @Published var launcherDownloading = false
    /// Set after a self-update download (e.g. "Saved to Downloads — quit & replace.").
    @Published var launcherDownloadMessage: String?
    /// Drives the "after an update, macOS will ask for your password" explainer sheet.
    @Published var showKeychainExplainer = false

    private var selfInfo: SelfInfo?
    private var explainerContinuation: CheckedContinuation<Void, Never>?
    private static let codeIDKey = "theatre.lastKeychainCodeID"

    var currentVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0.0"
    }

    init() {
        do {
            let catalog = try Catalog.load()
            selfInfo = catalog.selfInfo
            rows = catalog.apps.map {
                Row(app: $0,
                    installed: InstallManager.shared.installedVersion($0.id),
                    resolvedName: InstallManager.shared.installedDisplayName($0.id))
            }
        } catch {
            globalError = "Could not load app catalog: \(error.localizedDescription)"
        }
    }

    // MARK: - Token

    func setToken(_ token: String) {
        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        TokenStore.save(trimmed)
        hasToken = true
        // The current build just created the Keychain item, so it can read it without prompting —
        // record this identity so the explainer doesn't fire until the next update.
        stampCodeIdentity()
    }

    func clearToken() {
        TokenStore.clear()
        hasToken = false
        for i in rows.indices {
            rows[i].status = .unknown
            rows[i].latest = nil
            rows[i].latestAssetId = nil
            rows[i].releases = []
        }
    }

    // MARK: - Keychain access explainer (macOS only)

    /// Reads the token, recording the current code identity on success so the explainer won't
    /// re-fire for this build. Returns nil if absent or the user denied the Keychain prompt.
    private func currentToken() -> String? {
        let token = TokenStore.load()
        if token != nil { stampCodeIdentity() }
        return token
    }

    private func stampCodeIdentity() {
        UserDefaults.standard.set(CodeIdentity.current(), forKey: Self.codeIDKey)
    }

    /// Call before the first token read of a flow. If a token is saved and the running build differs
    /// from the one that last accessed it (i.e. an update — so macOS WILL prompt), shows the
    /// explainer first and waits for the user to acknowledge it.
    func ensureKeychainExplained() async {
        guard TokenStore.cachedToken == nil else { return }   // already read this session → no prompt coming
        guard TokenStore.exists() else { return }             // nothing saved → no read → no prompt
        let last = UserDefaults.standard.string(forKey: Self.codeIDKey)
        guard last != CodeIdentity.current() else { return }  // same build → OS won't prompt
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            explainerContinuation = cont
            showKeychainExplainer = true
        }
    }

    /// Invoked when the user dismisses/acknowledges the explainer — resumes the waiting flow.
    func acknowledgeKeychainExplainer() {
        showKeychainExplainer = false
        explainerContinuation?.resume()
        explainerContinuation = nil
    }

    // MARK: - Refresh

    func refreshAll() async {
        await ensureKeychainExplained()
        guard let token = currentToken() else { return }
        let client = GitHubClient(token: token)
        for i in rows.indices {
            await refresh(index: i, client: client)
        }
    }

    private func refresh(index: Int, client: GitHubClient) async {
        let app = rows[index].app
        rows[index].status = .checking
        rows[index].installed = InstallManager.shared.installedVersion(app.id)
        rows[index].resolvedName = InstallManager.shared.installedDisplayName(app.id)
        do {
            let all = try await client.releases(owner: app.owner, repo: app.repo)
            rows[index].releases = all
            guard let latest = all.first(where: { !$0.prerelease }) ?? all.first else {
                rows[index].latest = nil
                rows[index].latestAssetId = nil
                rows[index].status = .noRelease
                return
            }
            let assetId = latest.assets.first { $0.name == app.macAssetName }?.id
            rows[index].latest = latest.tagName
            rows[index].latestAssetId = assetId
            rows[index].status = Self.status(installed: rows[index].installed, latest: latest.tagName, hasAsset: assetId != nil)
        } catch GitHubError.noRelease {
            rows[index].latest = nil
            rows[index].releases = []
            rows[index].status = .noRelease
        } catch {
            rows[index].status = .error(error.localizedDescription)
        }
    }

    private static func status(installed: String?, latest: String, hasAsset: Bool) -> Status {
        guard hasAsset else { return .missingAsset }
        guard let installed = installed else { return .notInstalled }
        return versionsEqual(installed, latest) ? .upToDate : .updateAvailable
    }

    private static func versionsEqual(_ a: String, _ b: String) -> Bool {
        norm(a) == norm(b)
    }

    private static func norm(_ s: String) -> String {
        var t = s.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("v") || t.hasPrefix("V") { t.removeFirst() }
        return t
    }

    /// True if `a` is a strictly newer version string than `b` (component-wise numeric compare).
    static func versionIsNewer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] {
            norm(s).split(separator: ".").map { Int($0.prefix { $0.isNumber }) ?? 0 }
        }
        let pa = parts(a), pb = parts(b)
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    // MARK: - Install / update / uninstall / launch

    /// Installs an app — the latest release, or a specific `tag` (for installing older versions).
    func install(_ id: String, tag: String? = nil) async {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return }
        await ensureKeychainExplained()
        guard let token = currentToken() else { return }
        let app = rows[i].app
        rows[i].busy = true
        rows[i].progress = 0
        defer { rows[i].busy = false }

        let client = GitHubClient(token: token)
        if rows[i].releases.isEmpty {
            do { rows[i].releases = try await client.releases(owner: app.owner, repo: app.repo) }
            catch { rows[i].status = .error(error.localizedDescription); return }
        }

        let release = tag != nil
            ? rows[i].releases.first { $0.tagName == tag }
            : (rows[i].releases.first { !$0.prerelease } ?? rows[i].releases.first)
        guard let rel = release else { rows[i].status = .error("Version \(tag ?? "latest") not found."); return }
        guard let asset = rel.assets.first(where: { $0.name == app.macAssetName }) else {
            rows[i].status = .error("No macOS asset in \(rel.tagName)."); return
        }

        let zipDest = InstallManager.shared.cacheDir.appendingPathComponent("\(app.id)-\(rel.tagName).zip")
        let appId = id
        do {
            try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: asset.id, to: zipDest) { p in
                Task { @MainActor [weak self] in
                    guard let self = self,
                          let j = self.rows.firstIndex(where: { $0.id == appId }) else { return }
                    self.rows[j].progress = p
                }
            }
            try InstallManager.shared.install(app: app, version: rel.tagName, downloadedZip: zipDest)
            try? FileManager.default.removeItem(at: zipDest)
            rows[i].installed = rel.tagName
            rows[i].resolvedName = InstallManager.shared.installedDisplayName(app.id)
            rows[i].status = Self.status(installed: rel.tagName, latest: rows[i].latest ?? rel.tagName, hasAsset: true)
        } catch {
            rows[i].status = .error(error.localizedDescription)
        }
    }

    func uninstall(_ id: String) {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return }
        do {
            try InstallManager.shared.uninstall(id)
            rows[i].installed = nil
            rows[i].resolvedName = nil
            if rows[i].latestAssetId != nil {
                rows[i].status = .notInstalled
            } else if rows[i].latest == nil {
                rows[i].status = .unknown
            } else {
                rows[i].status = .missingAsset
            }
        } catch {
            rows[i].status = .error(error.localizedDescription)
        }
    }

    func launch(_ id: String) {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return }
        do {
            try InstallManager.shared.launch(app: rows[i].app)
        } catch {
            rows[i].status = .error(error.localizedDescription)
        }
    }

    // MARK: - Launcher self-update

    /// Checks JBTheatreTools' own latest release against the running version.
    @discardableResult
    func checkLauncherUpdate() async -> LauncherCheck {
        guard let s = selfInfo else { return .unavailable("No self-update info in catalog.") }
        // The launcher repo is public — use the token only if already in memory; never force a
        // Keychain read here (would trigger the prompt for a check that doesn't need auth).
        let client = GitHubClient(token: TokenStore.cachedToken)
        do {
            let info = try await client.latestRelease(owner: s.owner, repo: s.repo)
            let newer = Self.versionIsNewer(info.tagName, than: currentVersion)
            launcherUpdateAvailable = newer ? info.tagName : nil
            return newer ? .available(current: currentVersion, latest: info.tagName) : .upToDate(currentVersion)
        } catch GitHubError.noRelease {
            return .unavailable("No launcher release published yet.")
        } catch {
            return .unavailable(error.localizedDescription)
        }
    }

    /// Downloads the launcher's latest build to ~/Downloads and reveals it in Finder.
    /// (We don't self-replace a running .app — the user quits and swaps it in.)
    func downloadLauncherUpdate() async {
        guard let s = selfInfo else { return }
        launcherDownloading = true
        launcherDownloadMessage = nil
        defer { launcherDownloading = false }

        let client = GitHubClient(token: TokenStore.cachedToken)   // public repo; no forced Keychain read
        do {
            let info = try await client.latestRelease(owner: s.owner, repo: s.repo)
            guard let asset = info.assets.first(where: { $0.name == s.macAssetName }) else {
                launcherDownloadMessage = "No macOS asset in \(info.tagName)."
                return
            }
            let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
            let dest = downloads.appendingPathComponent(asset.name)
            try await client.downloadAsset(owner: s.owner, repo: s.repo, assetId: asset.id, to: dest)
            NSWorkspace.shared.activateFileViewerSelecting([dest])
            launcherDownloadMessage = "Saved \(asset.name) to your Downloads folder — quit JB Theatre Tools and replace it."
        } catch {
            launcherDownloadMessage = "Download failed: \(error.localizedDescription)"
        }
    }
}
