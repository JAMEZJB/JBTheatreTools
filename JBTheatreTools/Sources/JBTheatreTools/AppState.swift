import Foundation
import SwiftUI
import AppKit

/// Where downloads authenticate: a personal GitHub token (direct API access), or James's
/// download server (relay) with a shared suite passphrase — no GitHub token on the machine.
enum AuthMode: String, CaseIterable, Identifiable {
    case token, server
    var id: String { rawValue }
    var label: String {
        switch self {
        case .token: return "GitHub token"
        case .server: return "Download server"
        }
    }
}

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
        /// Which variant is installed on disk (for apps that ship variants); nil = single-variant/none.
        var installedVariant: String?
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
    /// True when the built-in download server is configured and a passphrase is saved. (Set in init,
    /// after the catalog — which carries the server URL — has loaded.)
    @Published var hasServerAuth: Bool = false
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
    /// App ids the user pinned to the top of the list (per-machine). Order within the pinned group
    /// comes from `rows`, so a Set is enough.
    @Published var pinnedIds: Set<String> = []
    /// App ids the user hid from the list (per-machine). Hidden apps stay in `rows` (so unhiding
    /// restores their position) but are filtered out of every display group.
    @Published var hiddenIds: Set<String> = []
    /// Per-app selected variant id (per-machine), for apps that ship variants (e.g. NDI Standard/Full).
    /// Absent → the app's default (first) variant.
    @Published var variantSelection: [String: String] = [:]

    private var selfInfo: SelfInfo?
    /// The download-relay base URL: an (invisible, settings-only) local override wins, else the
    /// catalog's built-in `downloadServer`. Nil only in a build whose catalog carries no server.
    private(set) var serverBase: String?
    /// App ids in catalog order — the baseline the user's saved ordering is applied over.
    private var catalogOrder: [String] = []
    private var explainerContinuation: CheckedContinuation<Void, Never>?
    private static let codeIDKey = "theatre.lastKeychainCodeID"
    private static let appOrderKey = "theatre.appOrder"
    private static let pinnedKey = "theatre.pinnedApps"
    private static let hiddenKey = "theatre.hiddenApps"
    private static let variantKey = "theatre.appVariants"

    var currentVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0.0"
    }

    init() {
        do {
            let catalog = try Catalog.load()
            selfInfo = catalog.selfInfo
            serverBase = Self.serverOverride ?? catalog.downloadServer
            rows = catalog.apps.map {
                let installed = InstallManager.shared.installedVersion($0.id)
                return Row(app: $0,
                           installed: installed,
                           status: installed != nil ? .installed : .unknown,
                           resolvedName: InstallManager.shared.installedDisplayName($0.id),
                           installedVariant: InstallManager.shared.installedVariant($0.id))
            }
            catalogOrder = catalog.apps.map(\.id)
            rows = Self.applyingSavedOrder(rows)
            let known = Set(catalogOrder)
            pinnedIds = Set(UserDefaults.standard.stringArray(forKey: Self.pinnedKey) ?? []).intersection(known)
            hiddenIds = Set(UserDefaults.standard.stringArray(forKey: Self.hiddenKey) ?? []).intersection(known)
            if let saved = UserDefaults.standard.dictionary(forKey: Self.variantKey) as? [String: String] {
                variantSelection = saved.filter { known.contains($0.key) }
            }
        } catch {
            globalError = "Could not load app catalog: \(error.localizedDescription)"
        }
        // First run of this build generation: materialise the default auth mode. Server (passphrase)
        // is the default for fresh installs, but a machine that already has a PAT saved stays in
        // token mode — updating must never silently break a working token setup.
        if UserDefaults.standard.string(forKey: "theatre.authMode") == nil {
            UserDefaults.standard.set(
                (TokenStore.exists() ? AuthMode.token : AuthMode.server).rawValue,
                forKey: "theatre.authMode")
        }
        hasServerAuth = serverBase != nil && ServerAuthStore.exists()
        AppLog.shared.log("launched v\(currentVersion)")
    }

    // MARK: - Auth mode

    /// The active download-auth mode. Server (passphrase) is the default; init materialises the
    /// stored value on first run so a machine with an existing PAT stays in token mode.
    /// (nonisolated: UserDefaults is thread-safe and the CLI reads this off the main actor.)
    nonisolated static var authMode: AuthMode {
        AuthMode(rawValue: UserDefaults.standard.string(forKey: "theatre.authMode") ?? "") ?? .server
    }

    /// User-invisible relay-URL override (defaults key only, no UI) — an escape hatch if the
    /// built-in catalog URL ever has to move for machines on an old build.
    nonisolated static var serverOverride: String? {
        let s = (UserDefaults.standard.string(forKey: "theatre.serverURL") ?? "")
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return s.isEmpty ? nil : s
    }

    /// Credentials for the ACTIVE mode are present — drives the "can download at all" UI state.
    var hasCredentials: Bool {
        Self.authMode == .token ? hasToken : hasServerAuth
    }

    /// The no-credentials call to action, worded for the active mode.
    var credentialsPrompt: String {
        Self.authMode == .token
            ? "Add a GitHub token to enable downloads"
            : "Enter the suite passphrase to enable downloads"
    }

    /// A client for the active mode, reading credentials from the Keychain (so it may trigger the
    /// macOS prompt — call `ensureKeychainExplained()` first). Nil when credentials are missing.
    private func activeClient() -> GitHubClient? {
        switch Self.authMode {
        case .token:
            guard let token = currentToken() else { return nil }
            return GitHubClient(token: token)
        case .server:
            guard let base = serverBase, let pass = currentServerPass() else { return nil }
            return GitHubClient(serverBase: base, passphrase: pass)
        }
    }

    /// A client that must never force a Keychain read (used by the self-update paths). Falls back to
    /// direct unauthenticated GitHub — fine there, because the launcher's own repo is public.
    private func cachedOnlyClient() -> GitHubClient {
        switch Self.authMode {
        case .token:
            return GitHubClient(token: TokenStore.cachedToken)
        case .server:
            if let base = serverBase, let pass = ServerAuthStore.cachedPassphrase {
                return GitHubClient(serverBase: base, passphrase: pass)
            }
            return GitHubClient(token: nil)
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
        resetRowsUnchecked()
    }

    // MARK: - Download-server auth

    func setServerPassphrase(_ passphrase: String) {
        let trimmed = passphrase.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        ServerAuthStore.save(trimmed)
        hasServerAuth = serverBase != nil
        stampCodeIdentity()   // this build just wrote the item — no prompt until the next update
    }

    func clearServerAuth() {
        ServerAuthStore.clear()
        hasServerAuth = false
        resetRowsUnchecked()
    }

    /// Called when the auth-mode picker changes: nothing carries over between modes, so drop cached
    /// release state and let the next refresh rebuild it under the new mode's credentials.
    func authModeChanged() {
        resetRowsUnchecked()
    }

    private func resetRowsUnchecked() {
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

    /// Server-mode twin of `currentToken()` — reads the passphrase (same Keychain-prompt behaviour).
    private func currentServerPass() -> String? {
        let pass = ServerAuthStore.load()
        if pass != nil { stampCodeIdentity() }
        return pass
    }

    private func stampCodeIdentity() {
        UserDefaults.standard.set(CodeIdentity.current(), forKey: Self.codeIDKey)
    }

    /// Call before the first Keychain read of a flow. If the active mode's secret is saved and the
    /// running build differs from the one that last accessed it (i.e. an update — so macOS WILL
    /// prompt), shows the explainer first and waits for the user to acknowledge it.
    func ensureKeychainExplained() async {
        switch Self.authMode {
        case .token:
            guard TokenStore.cachedToken == nil else { return }    // already read → no prompt coming
            guard TokenStore.exists() else { return }              // nothing saved → no read → no prompt
        case .server:
            guard ServerAuthStore.cachedPassphrase == nil else { return }
            guard ServerAuthStore.exists() else { return }
        }
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
        guard let client = activeClient() else { return }
        // Clear a stale network/credential error from a previous run (but keep a catalog-load error,
        // which leaves `rows` empty).
        if !rows.isEmpty { globalError = nil }
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

    // MARK: - Row ordering (per-machine, persisted)

    /// Reorders `rows` to the user's saved order: saved ids first (in saved order), then any ids the
    /// saved list doesn't know (e.g. apps added to the catalog since) in catalog position. Ids in the
    /// saved list that no longer exist are ignored.
    private static func applyingSavedOrder(_ rows: [Row]) -> [Row] {
        let saved = UserDefaults.standard.stringArray(forKey: appOrderKey) ?? []
        guard !saved.isEmpty else { return rows }
        var remaining = rows
        var ordered: [Row] = []
        for id in saved {
            if let i = remaining.firstIndex(where: { $0.id == id }) {
                ordered.append(remaining.remove(at: i))
            }
        }
        // Apps the saved list doesn't know (added since) follow at the end, in catalog order.
        ordered.append(contentsOf: remaining)
        return ordered
    }

    private func persistOrder() {
        UserDefaults.standard.set(rows.map(\.id), forKey: Self.appOrderKey)
    }

    // MARK: Display groups (pinned first, hidden filtered out)

    func isPinned(_ id: String) -> Bool { pinnedIds.contains(id) }
    func isHidden(_ id: String) -> Bool { hiddenIds.contains(id) }

    /// Rows the user pinned to the top — visible, not hidden, in `rows` order.
    var pinnedDisplayRows: [Row] { rows.filter { $0.isVisible && !hiddenIds.contains($0.id) && pinnedIds.contains($0.id) } }
    /// The unpinned rows below — visible, not hidden, in `rows` order.
    var mainDisplayRows: [Row] { rows.filter { $0.isVisible && !hiddenIds.contains($0.id) && !pinnedIds.contains($0.id) } }
    /// All hidden apps (for the Settings "Hidden apps" list — shown regardless of reachability).
    var hiddenRows: [Row] { rows.filter { hiddenIds.contains($0.id) } }
    var hasHiddenApps: Bool { !hiddenIds.isEmpty }
    /// True when at least one row is shown (drives the "everything's hidden" empty state).
    var hasVisibleRows: Bool { !pinnedDisplayRows.isEmpty || !mainDisplayRows.isEmpty }

    // MARK: Pin / hide toggles

    func togglePin(_ id: String) {
        if pinnedIds.contains(id) { pinnedIds.remove(id) } else { pinnedIds.insert(id) }
        UserDefaults.standard.set(Array(pinnedIds), forKey: Self.pinnedKey)
        AppLog.shared.log("\(pinnedIds.contains(id) ? "pinned" : "unpinned") \(id)")
    }

    func setHidden(_ id: String, _ hidden: Bool) {
        if hidden { hiddenIds.insert(id) } else { hiddenIds.remove(id) }
        UserDefaults.standard.set(Array(hiddenIds), forKey: Self.hiddenKey)
        AppLog.shared.log("\(hidden ? "hid" : "unhid") \(id)")
    }

    func showAllHidden() {
        hiddenIds.removeAll()
        UserDefaults.standard.set([String](), forKey: Self.hiddenKey)
        AppLog.shared.log("unhid all apps")
    }

    // MARK: Variants (apps that ship more than one download, e.g. NDI Standard/Full)

    /// The selected variant id for an app (persisted), defaulting to its first variant. Nil if the app
    /// ships no variants.
    func selectedVariantId(_ app: CatalogApp) -> String? {
        guard app.hasVariants else { return nil }
        return variantSelection[app.id] ?? app.variants?.first?.id
    }

    /// The macOS asset name for an app, honouring the selected variant (and this Mac's architecture).
    func macAsset(for app: CatalogApp) -> String? {
        app.macAssetName(variantId: selectedVariantId(app))
    }

    /// Label of the variant the user has SELECTED for an app (for the toggle / version line).
    func selectedVariantLabel(_ app: CatalogApp) -> String? {
        guard let vid = selectedVariantId(app) else { return nil }
        return app.variants?.first { $0.id == vid }?.label
    }

    /// Label of the variant currently INSTALLED for a row (for the version line), or nil.
    func installedVariantLabel(_ row: Row) -> String? {
        guard row.app.hasVariants, let vid = row.installedVariant ?? row.app.variants?.first?.id else { return nil }
        return row.app.variants?.first { $0.id == vid }?.label
    }

    /// True when the app ships variants and the SELECTED one isn't the INSTALLED one — so the user can
    /// install/switch to the selected build even when the installed version is otherwise "up to date".
    func variantSwitchAvailable(_ row: Row) -> Bool {
        guard row.app.hasVariants, row.installed != nil else { return false }
        return (row.installedVariant ?? row.app.variants?.first?.id) != selectedVariantId(row.app)
    }

    /// Changes the selected variant for an app, persists it, and re-resolves that row's status/asset.
    func setVariant(_ appId: String, _ variantId: String) {
        variantSelection[appId] = variantId
        UserDefaults.standard.set(variantSelection, forKey: Self.variantKey)
        if let i = rows.firstIndex(where: { $0.id == appId }) { recomputeRow(i) }
        AppLog.shared.log("variant for \(appId) → \(variantId)")
    }

    /// Re-derives latestAssetId + status for a row from its cached releases (after a variant change).
    private func recomputeRow(_ i: Int) {
        guard rows.indices.contains(i), let latest = Self.latest(from: rows[i].releases) else { return }
        let assetId = latest.assets.first { $0.name == macAsset(for: rows[i].app) }?.id
        rows[i].latestAssetId = assetId
        rows[i].status = Self.status(installed: rows[i].installed, latest: latest.tagName, hasAsset: assetId != nil)
    }

    // MARK: Reorder — drag (per group) and Move Up/Down

    /// Drag reorder within one display group (pinned or main). Reassigns the group members among the
    /// slots they already occupy in `rows`, so non-group rows stay put and render stays pinned-first.
    func moveInList(pinned: Bool, from source: IndexSet, to destination: Int) {
        var groupIds = (pinned ? pinnedDisplayRows : mainDisplayRows).map(\.id)
        guard !groupIds.isEmpty else { return }
        groupIds.move(fromOffsets: source, toOffset: destination)
        let byId = Dictionary(uniqueKeysWithValues: rows.map { ($0.id, $0) })
        let slots = rows.indices.filter { pinned ? pinnedIds.contains(rows[$0].id) && rows[$0].isVisible && !hiddenIds.contains(rows[$0].id)
                                                 : !pinnedIds.contains(rows[$0].id) && rows[$0].isVisible && !hiddenIds.contains(rows[$0].id) }
        for (k, slot) in slots.enumerated() where k < groupIds.count {
            if let row = byId[groupIds[k]] { rows[slot] = row }
        }
        persistOrder()
        AppLog.shared.log("reordered \(pinned ? "pinned" : "main") list")
    }

    /// Whether the row can move up/down within its own display group (pinned/main), skipping hidden.
    func canMove(_ id: String, up: Bool) -> Bool { groupNeighbour(of: id, up: up) != nil }

    /// Moves the row past its nearest same-group neighbour and persists the new order.
    func moveRow(_ id: String, up: Bool) {
        guard let i = rows.firstIndex(where: { $0.id == id }),
              let j = groupNeighbour(of: id, up: up) else { return }
        rows.swapAt(i, j)
        persistOrder()
        AppLog.shared.log("moved \(id) \(up ? "up" : "down")")
    }

    /// Restores the catalog's default order (Settings → Reset App Order). Pins/hides are left as-is.
    func resetAppOrder() {
        UserDefaults.standard.removeObject(forKey: Self.appOrderKey)
        let index = Dictionary(uniqueKeysWithValues: catalogOrder.enumerated().map { ($1, $0) })
        rows.sort { (index[$0.id] ?? .max) < (index[$1.id] ?? .max) }
        AppLog.shared.log("app order reset to catalog default")
    }

    /// Nearest neighbour in the given direction that is visible, not hidden, and in the SAME pin group.
    private func groupNeighbour(of id: String, up: Bool) -> Int? {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return nil }
        let pinned = pinnedIds.contains(id)
        let range = up ? Array((0..<i).reversed()) : Array((i + 1)..<rows.count)
        return range.first {
            rows[$0].isVisible && !hiddenIds.contains(rows[$0].id) && pinnedIds.contains(rows[$0].id) == pinned
        }
    }

    // MARK: - Desktop alias & Dock pin (per-app, from the row menu; best-effort like win shortcuts)

    func hasDesktopAlias(_ id: String) -> Bool { InstallManager.shared.hasDesktopAlias(id) }

    func isDockPinned(_ id: String) -> Bool {
        guard let path = InstallManager.shared.installedPath(id)?.path else { return false }
        return Dock.isPinned(path)
    }

    func toggleDesktopAlias(_ id: String) {
        if hasDesktopAlias(id) {
            InstallManager.shared.removeDesktopAlias(id)
            AppLog.shared.log("removed desktop alias for \(id)")
        } else {
            do {
                try InstallManager.shared.addDesktopAlias(id)
                AppLog.shared.log("added desktop alias for \(id)")
            } catch {
                AppLog.shared.log("desktop alias for \(id) FAILED: \(error.localizedDescription)")
            }
        }
    }

    func toggleDockPin(_ id: String) {
        guard let path = InstallManager.shared.installedPath(id)?.path else { return }
        if Dock.isPinned(path) {
            Dock.unpin(path)
            AppLog.shared.log("unpinned \(id) from Dock")
        } else {
            Dock.pin(path)
            AppLog.shared.log("pinned \(id) to Dock")
        }
    }

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
        rows[index].installedVariant = InstallManager.shared.installedVariant(app.id)
        do {
            let all = try await client.releases(owner: app.owner, repo: app.repo)
            rows[index].releases = all
            guard let latest = Self.latest(from: all) else {
                rows[index].latest = nil
                rows[index].latestAssetId = nil
                rows[index].status = .noRelease
                return
            }
            let assetId = latest.assets.first { $0.name == macAsset(for: app) }?.id
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
            // The credential itself is bad — surface one clear message instead of 10 broken rows.
            rows[index].status = .noAccess
            globalError = Self.authMode == .token
                ? "Your GitHub token is invalid or expired. Open Settings to paste a new one."
                : "The download server rejected the passphrase. Check it in Settings."
            AppLog.shared.log("refresh: credentials rejected (\(Self.authMode.rawValue) mode)")
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
        guard let client = activeClient() else { return }
        let app = rows[i].app
        rows[i].busy = true
        rows[i].progress = 0
        defer { rows[i].busy = false }
        if rows[i].releases.isEmpty {
            do { rows[i].releases = try await client.releases(owner: app.owner, repo: app.repo) }
            catch { rows[i].status = .error(error.localizedDescription); return }
        }

        let release = tag != nil
            ? rows[i].releases.first { $0.tagName == tag }
            : Self.latest(from: rows[i].releases)
        guard let rel = release else { rows[i].status = .error("Version \(tag ?? "latest") not found."); return }
        let variantId = selectedVariantId(app)
        guard let asset = rel.assets.first(where: { $0.name == macAsset(for: app) }) else {
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
            // Strict for current releases: a "latest" install (tag == nil) MUST checksum-verify — every
            // current release ships a correct SHA256SUMS, so a missing/incomplete manifest here is
            // anomalous (e.g. tampering) → abort. Explicit older-tag installs (the version picker) may
            // predate the manifest, so they stay verify-if-present. A hash MISMATCH always aborts (it
            // throws from verifyDownload) regardless of tag; size is always checked too.
            if tag == nil, verification != .verified {
                try? FileManager.default.removeItem(at: zipDest)
                let reason = verification == .noManifest
                    ? "this release publishes no SHA256SUMS checksums"
                    : "“\(asset.name)” isn’t listed in this release’s SHA256SUMS"
                AppLog.shared.log("install \(app.id) \(rel.tagName): BLOCKED (strict) — \(reason)")
                throw InstallError.unverified(reason: reason)
            }
            switch verification {
            case .verified:       AppLog.shared.log("verified \(app.id) \(rel.tagName) (sha256)")
            case .noManifest:     AppLog.shared.log("install \(app.id) \(rel.tagName): unverified older tag (no SHA256SUMS)")
            case .assetNotListed: AppLog.shared.log("install \(app.id) \(rel.tagName): unverified older tag (asset not in SHA256SUMS)")
            }
            let toApps = UserDefaults.standard.bool(forKey: "theatre.installToApplications")
            try InstallManager.shared.install(app: app, version: rel.tagName, downloadedZip: zipDest, toApplications: toApps, variant: variantId)
            try? FileManager.default.removeItem(at: zipDest)
            rows[i].installed = rel.tagName
            rows[i].installedVariant = variantId
            rows[i].resolvedName = InstallManager.shared.installedDisplayName(app.id)
            rows[i].status = Self.status(installed: rel.tagName, latest: rows[i].latest ?? rel.tagName, hasAsset: true)
            AppLog.shared.log("installed \(app.id) \(rel.tagName)\(variantId.map { " [\($0)]" } ?? "")\(toApps ? " (Applications)" : "")")
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
            rows[i].installedVariant = nil
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
        // The launcher repo is public — use credentials only if already in memory; never force a
        // Keychain read here (would trigger the prompt for a check that doesn't need auth).
        let client = cachedOnlyClient()
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

        let client = cachedOnlyClient()   // public repo; no forced Keychain read
        do {
            let info = try await client.latestRelease(owner: s.owner, repo: s.repo)
            guard let asset = info.assets.first(where: { $0.name == s.macAssetName }) else {
                launcherDownloadMessage = "No macOS asset in \(info.tagName)."
                return
            }
            let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
            let dest = downloads.appendingPathComponent(asset.name)
            try await client.downloadAsset(owner: s.owner, repo: s.repo, assetId: asset.id, to: dest)
            // Strict verify: the launcher's own release always ships SHA256SUMS, so require a clean match
            // before telling the user to run the new build. (A hash mismatch throws; no-manifest /
            // not-listed are treated as a verification failure too — this is a current release.)
            guard let sumsAsset = info.assets.first(where: { $0.name == "SHA256SUMS" }) else {
                try? FileManager.default.removeItem(at: dest)
                launcherDownloadMessage = "Couldn't verify the update (no checksums published) — not saved."
                return
            }
            let sumsURL = dest.deletingLastPathComponent().appendingPathComponent("JBTheatreTools-SHA256SUMS.txt")
            try await client.downloadAsset(owner: s.owner, repo: s.repo, assetId: sumsAsset.id, to: sumsURL)
            let text = (try? String(contentsOf: sumsURL, encoding: .utf8)) ?? ""
            try? FileManager.default.removeItem(at: sumsURL)
            let verified: Bool
            do { verified = try InstallManager.verify(file: dest, assetName: asset.name, sums: text) }
            catch { try? FileManager.default.removeItem(at: dest); throw error }   // hash mismatch
            guard verified else {
                try? FileManager.default.removeItem(at: dest)
                launcherDownloadMessage = "Couldn't verify the update against its SHA256SUMS — not saved."
                return
            }
            NSWorkspace.shared.activateFileViewerSelecting([dest])
            launcherDownloadMessage = "Saved \(asset.name) to your Downloads folder — quit JB Theatre Tools and replace it."
        } catch {
            launcherDownloadMessage = "Download failed: \(error.localizedDescription)"
        }
    }
}
