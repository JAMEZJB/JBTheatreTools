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
        /// The saved token can't see this app's repo — the row is hidden from the list.
        case noAccess
        /// Installed locally but not yet checked for updates (e.g. before the first refresh).
        case installed
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
        /// Shown once we know a row is relevant: anything installed locally, or any app whose repo
        /// the token is confirmed to reach. Not-yet-checked / inaccessible not-installed rows stay
        /// hidden, so inaccessible apps never flash into view and back out during a refresh.
        var isVisible: Bool {
            if installed != nil { return true }
            switch status {
            case .unknown, .checking, .noAccess: return false
            default: return true
            }
        }
    }

    /// Drives the "move your installed apps?" confirmation when the install-location setting changes.
    struct RelocationPrompt: Identifiable {
        let id = UUID()
        let toApplications: Bool
        let count: Int
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
    /// True once at least one full refresh has completed (gates the "no apps for this token" state).
    @Published var hasRefreshed = false
    /// Set when the install-location setting changes and some installed apps need moving → shows a
    /// confirmation. A short note is shown afterwards if any app couldn't be moved (e.g. it was open).
    @Published var relocationPrompt: RelocationPrompt?
    @Published var relocationNote: String?

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
                let installed = InstallManager.shared.installedVersion($0.id)
                return Row(app: $0,
                           installed: installed,
                           status: installed != nil ? .installed : .unknown,
                           resolvedName: InstallManager.shared.installedDisplayName($0.id))
            }
        } catch {
            globalError = "Could not load app catalog: \(error.localizedDescription)"
        }
        AppLog.shared.log("launched v\(currentVersion)")
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
        hasRefreshed = false
        for i in rows.indices {
            // Keep installed apps visible & launchable; everything else reverts to "unchecked".
            rows[i].status = rows[i].installed != nil ? .installed : .unknown
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
        // Clear a stale network/token error from a previous run (but keep a catalog-load error,
        // which leaves `rows` empty).
        if !rows.isEmpty { globalError = nil }
        let client = GitHubClient(token: token)
        for i in rows.indices {
            await refresh(index: i, client: client)
        }
        hasRefreshed = true
    }

    /// True once a refresh has run and the token reached no apps (and nothing is installed locally) —
    /// drives the "this token can't access any apps" empty state.
    var noAppsAccessible: Bool {
        hasRefreshed && !rows.isEmpty && rows.allSatisfy { !$0.isVisible }
    }

    /// Number of installed apps with an update available — drives the header "Update All" button.
    var updatesAvailable: Int { rows.filter { $0.status == .updateAvailable }.count }

    /// Updates every app that currently has an update available, one at a time.
    func updateAll() async {
        let ids = rows.filter { $0.status == .updateAvailable }.map(\.id)
        AppLog.shared.log("update all: \(ids.count) app(s)")
        for id in ids { await install(id) }
    }

    private func refresh(index: Int, client: GitHubClient) async {
        let app = rows[index].app
        rows[index].status = .checking
        rows[index].installed = InstallManager.shared.installedVersion(app.id)
        rows[index].resolvedName = InstallManager.shared.installedDisplayName(app.id)
        do {
            let all = try await client.releases(owner: app.owner, repo: app.repo)
            rows[index].releases = all
            guard let latest = Self.latest(from: all) else {
                rows[index].latest = nil
                rows[index].latestAssetId = nil
                rows[index].status = .noRelease
                return
            }
            let assetId = latest.assets.first { $0.name == app.macAssetName }?.id
            rows[index].latest = latest.tagName
            rows[index].latestAssetId = assetId
            rows[index].status = Self.status(installed: rows[index].installed, latest: latest.tagName, hasAsset: assetId != nil)
        } catch GitHubError.notAccessible {
            // Token can't see this repo → hide the row from the list.
            rows[index].latest = nil
            rows[index].latestAssetId = nil
            rows[index].releases = []
            rows[index].status = .noAccess
        } catch GitHubError.unauthorized {
            // The token itself is bad — surface one clear message instead of 4 broken rows.
            rows[index].status = .noAccess
            globalError = "Your GitHub token is invalid or expired. Open Settings to paste a new one."
            AppLog.shared.log("refresh: token invalid or expired")
        } catch GitHubError.noRelease {
            rows[index].latest = nil
            rows[index].releases = []
            rows[index].status = .noRelease
        } catch {
            rows[index].status = .error(error.localizedDescription)
            AppLog.shared.log("refresh \(app.id) error: \(error.localizedDescription)")
        }
    }

    private static func status(installed: String?, latest: String, hasAsset: Bool) -> Status {
        guard hasAsset else { return .missingAsset }
        guard let installed = installed else { return .notInstalled }
        // Up-to-date ⇔ the latest release is NOT strictly newer than what's installed. Using the numeric
        // comparator (`versionIsNewer`) rather than string equality fixes two defects: `1.2` vs `1.2.0`
        // (and any differing segment count) no longer reads as a perpetual "Update available", and a
        // republished OLDER release is never offered as an "update" that would silently downgrade.
        return versionIsNewer(latest, than: installed) ? .updateAvailable : .upToDate
    }

    /// Picks the release to treat as "latest": the highest **semver** among non-prereleases (falling
    /// back to the highest among all releases if every one is a prerelease). GitHub's list endpoint is
    /// ordered by creation date, so a backport/hotfix published *after* a newer release would otherwise
    /// be mis-selected as "latest" (and then offered as a downgrade) — we sort by version instead. This
    /// also matches GitHub's own semver-aware `releases/latest`, which the self-update check uses.
    nonisolated static func latest(from releases: [ReleaseInfo]) -> ReleaseInfo? {
        let stable = releases.filter { !$0.prerelease }
        let pool = stable.isEmpty ? releases : stable
        return pool.max { versionIsNewer($1.tagName, than: $0.tagName) }
    }

    nonisolated private static func norm(_ s: String) -> String {
        var t = s.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("v") || t.hasPrefix("V") { t.removeFirst() }
        return t
    }

    /// True if `a` is a strictly newer version string than `b` (component-wise numeric compare).
    nonisolated static func versionIsNewer(_ a: String, than b: String) -> Bool {
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

    // MARK: - Download integrity

    /// Integrity-checks a freshly downloaded asset before it's installed/launched. The file size must
    /// match the release's declared size, and — when the release publishes a `SHA256SUMS` manifest —
    /// its SHA-256 must match the listed value. A mismatch deletes the file and throws. Returns
    /// `.verified` on a checksum match, or (verify-if-present) `.noManifest` / `.assetNotListed` when
    /// there's nothing to check against — the caller proceeds but should report it as unverified.
    nonisolated static func verifyDownload(_ file: URL, asset: ReleaseAsset, release: ReleaseInfo,
                                           app: CatalogApp, client: GitHubClient) async throws -> VerifyResult {
        let fm = FileManager.default
        if asset.size > 0,
           let attrs = try? fm.attributesOfItem(atPath: file.path),
           let size = (attrs[.size] as? NSNumber)?.intValue, size != asset.size {
            try? fm.removeItem(at: file)
            throw InstallError.sizeMismatch(expected: asset.size, got: size)
        }
        guard let sumsAsset = release.assets.first(where: { $0.name == "SHA256SUMS" }) else { return .noManifest }
        let sumsURL = InstallManager.shared.cacheDir.appendingPathComponent("\(app.id)-\(release.tagName)-SHA256SUMS")
        try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: sumsAsset.id, to: sumsURL)
        let text = (try? String(contentsOf: sumsURL, encoding: .utf8)) ?? ""
        try? fm.removeItem(at: sumsURL)
        do {
            return try InstallManager.verify(file: file, assetName: asset.name, sums: text) ? .verified : .assetNotListed
        } catch {
            try? fm.removeItem(at: file)
            throw error
        }
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
            : Self.latest(from: rows[i].releases)
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
            let verification = try await Self.verifyDownload(zipDest, asset: asset, release: rel, app: app, client: client)
            switch verification {
            case .verified:       AppLog.shared.log("verified \(app.id) \(rel.tagName) (sha256)")
            case .noManifest:     AppLog.shared.log("install \(app.id) \(rel.tagName): unverified (release publishes no SHA256SUMS)")
            case .assetNotListed: AppLog.shared.log("install \(app.id) \(rel.tagName): unverified (asset not listed in SHA256SUMS)")
            }
            let toApps = UserDefaults.standard.bool(forKey: "theatre.installToApplications")
            try InstallManager.shared.install(app: app, version: rel.tagName, downloadedZip: zipDest, toApplications: toApps)
            try? FileManager.default.removeItem(at: zipDest)
            rows[i].installed = rel.tagName
            rows[i].resolvedName = InstallManager.shared.installedDisplayName(app.id)
            rows[i].status = Self.status(installed: rel.tagName, latest: rows[i].latest ?? rel.tagName, hasAsset: true)
            AppLog.shared.log("installed \(app.id) \(rel.tagName)\(toApps ? " (Applications)" : "")")
        } catch {
            rows[i].status = .error(error.localizedDescription)
            AppLog.shared.log("install \(app.id) FAILED: \(error.localizedDescription)")
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
            AppLog.shared.log("uninstalled \(id)")
        } catch {
            rows[i].status = .error(error.localizedDescription)
            AppLog.shared.log("uninstall \(id) FAILED: \(error.localizedDescription)")
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

    // MARK: - Install-location relocation

    /// Called when the "install to Applications" setting changes. If some installed apps are still in
    /// the other location, raises a confirmation to move them all so the setting stays truthful.
    func installLocationChanged(toApplications: Bool) {
        let count = rows.filter {
            $0.installed != nil && InstallManager.shared.needsRelocation($0.id, toApplications: toApplications)
        }.count
        relocationPrompt = count > 0 ? RelocationPrompt(toApplications: toApplications, count: count) : nil
    }

    /// Moves every installed app to match the chosen location, collecting any that couldn't move
    /// (e.g. because they're currently open) to report back to the user.
    ///
    /// `toApplications` is passed in explicitly rather than read from `relocationPrompt`: dismissing the
    /// confirmation alert clears `relocationPrompt` (via its `isPresented` binding) on the same tap that
    /// fires this async task, so reading it here would race and usually find nil — which is exactly why
    /// "Move" appeared to do nothing.
    func performRelocation(toApplications: Bool) async {
        relocationPrompt = nil
        var moved = 0
        var failed: [String] = []
        for i in rows.indices where rows[i].installed != nil {
            let needed = InstallManager.shared.needsRelocation(rows[i].id, toApplications: toApplications)
            do {
                try InstallManager.shared.relocate(rows[i].id, toApplications: toApplications)
                if needed { moved += 1 }
            } catch {
                failed.append(rows[i].displayName)
            }
            rows[i].installed = InstallManager.shared.installedVersion(rows[i].id)
            rows[i].resolvedName = InstallManager.shared.installedDisplayName(rows[i].id)
        }
        AppLog.shared.log("relocate → \(toApplications ? "Applications" : "launcher"): moved \(moved), failed \(failed.count)")
        if !failed.isEmpty {
            let target = toApplications ? "the Applications folder" : "the launcher"
            let them = failed.count == 1 ? "it" : "them"
            relocationNote = "Couldn't move \(failed.joined(separator: ", ")) to \(target) — \(failed.count == 1 ? "it may be" : "they may be") open. Close \(them) and toggle the setting again."
        }
    }

    func cancelRelocation() { relocationPrompt = nil }

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
            // Integrity-check the launcher download too (the launcher repo publishes SHA256SUMS).
            if let sumsAsset = info.assets.first(where: { $0.name == "SHA256SUMS" }) {
                let sumsURL = dest.deletingLastPathComponent().appendingPathComponent("JBTheatreTools-SHA256SUMS.txt")
                try await client.downloadAsset(owner: s.owner, repo: s.repo, assetId: sumsAsset.id, to: sumsURL)
                let text = (try? String(contentsOf: sumsURL, encoding: .utf8)) ?? ""
                try? FileManager.default.removeItem(at: sumsURL)
                do { _ = try InstallManager.verify(file: dest, assetName: asset.name, sums: text) }
                catch { try? FileManager.default.removeItem(at: dest); throw error }
            }
            NSWorkspace.shared.activateFileViewerSelecting([dest])
            launcherDownloadMessage = "Saved \(asset.name) to your Downloads folder — quit JB Theatre Tools and replace it."
        } catch {
            launcherDownloadMessage = "Download failed: \(error.localizedDescription)"
        }
    }
}
