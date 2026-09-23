import Foundation
import SwiftUI
import AppKit
import Combine

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

/// The Sendable result of one app's concurrent update check — categorised inside the child task (off the
/// main actor) so only value data crosses back to the main actor (`any Error` isn't Sendable, so the error
/// case carries its message string).
enum FetchOutcome: Sendable {
    case releases([ReleaseInfo])
    case noAccess
    case unauthorized
    case noRelease
    case error(String)
}

/// Download progress, published SEPARATELY from `AppState.rows`. A progress tick used to mutate the whole
/// `rows` array (`@Published`), which re-rendered every row AND the header ~100 times per download — across a
/// Download All that's thousands of full-list renders. Only the small progress bars observe this object.
@MainActor
final class ProgressHub: ObservableObject {
    @Published private(set) var progress: [String: Double] = [:]
    private var lastPublish: [String: CFAbsoluteTime] = [:]
    /// Publishes on ≥1% steps (audit F12) AND at most ~12× a second per app: every publish is a SwiftUI
    /// render plus a CoreAnimation commit, and AppKit's per-commit work (a WindowServer display-timing
    /// round-trip, a walk of the window's whole view tree) costs a few milliseconds regardless of how
    /// little changed. On a fast connection a 1%-only throttle still meant ~100 commits a second per
    /// download — most of the main thread — for a bar nobody can see move that fast.
    func set(_ id: String, _ p: Double) {
        if p >= 1 { progress[id] = 1; lastPublish[id] = nil; return }   // hold full until `clear` (end of slot)
        let now = CFAbsoluteTimeGetCurrent()
        guard abs((progress[id] ?? -1) - p) >= 0.01, now - (lastPublish[id] ?? 0) >= 0.08 else { return }
        lastPublish[id] = now
        progress[id] = p
    }
    func clear(_ id: String) { progress[id] = nil; lastPublish[id] = nil }
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

    /// One catalog app's live state. A CLASS with its own `@Published` fields — NOT a value in the published
    /// `rows` array — so a row's busy/status/progress change re-renders THAT row only. When every row was a
    /// struct inside `@Published var rows`, each install's ~3 mutations republished the array and re-ran all
    /// 21 row bodies (each hosting AppKit-backed Menu/Picker/Progress controls whose Auto Layout was re-solved):
    /// measured ~65 ms per publish on a fast Mac, ~3 s of main-thread time per Download All. `rows` itself now
    /// publishes only on STRUCTURAL change (order, insert/remove); visibility flips and header counts are
    /// signalled via `rowsGen`.
    final class Row: ObservableObject, Identifiable {
        let app: CatalogApp
        var id: String { app.id }
        @Published var latest: String?
        @Published var latestAssetId: Int?
        @Published var installed: String?
        @Published var releases: [ReleaseInfo] = []
        /// The semver-picked latest of `releases`, computed ONCE when releases change. Everything that used to
        /// call `latest(from:)` per render (the header's update/download counts, slot checks) reads this instead —
        /// re-sorting 21 release lists on every render was a hidden per-frame cost.
        @Published var latestRelease: ReleaseInfo?
        @Published var status: Status = .unknown
        @Published var busy: Bool = false
        /// Slots of THIS app currently in flight (a pipelined Download All can have the Full edition downloading
        /// while the Light edition is still extracting). `busy` mirrors `busyCount > 0`.
        var busyCount: Int = 0
        /// The installed app's self-declared bundle name (kept for diagnostics only — NOT shown; see displayName).
        @Published var resolvedName: String?
        /// Suffix for the selected non-default variant (e.g. " (Full)"), appended to the curated name so a Full
        /// install reads consistently regardless of what the app calls its own bundle.
        @Published var variantSuffix: String = ""
        /// The "New in" line actually shown: the catalog's, overlaid by the relay's editable notes
        /// (`WhatsNewNotes`) when the relay has one for this app.
        @Published var whatsNew: String?
        @Published var whatsNewVersion: String?
        /// Name to show: the launcher's CURATED catalog name (James's naming) + the variant suffix. We do NOT
        /// use the installed bundle's self-name — several bundles diverge from the curated name (e.g. the
        /// Convert app calls itself "Convert to it!", Network Port Map's bundle is "Build Port Map", Show
        /// Dashboard's is "ShowDashboard"), and the catalog name is what James curates for the suite.
        var displayName: String { app.name + variantSuffix }
        /// The error text when the row is in `.error`, else nil (the row shows it as a tooltip).
        var errorMessage: String? { if case .error(let m) = status { return m } else { return nil } }
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

        init(app: CatalogApp, installed: String? = nil, status: Status = .unknown, resolvedName: String? = nil,
             variantSuffix: String = "") {
            self.app = app; self.installed = installed; self.status = status
            self.resolvedName = resolvedName; self.variantSuffix = variantSuffix
            self.whatsNew = app.whatsNew; self.whatsNewVersion = app.whatsNewVersion
        }
    }

    /// Drives the "move your installed apps?" confirmation when the install-location setting changes.
    struct RelocationPrompt: Identifiable {
        let id = UUID()
        let toApplications: Bool
        let count: Int
    }

    @Published var rows: [Row] = []
    /// Bumped when a row's VISIBILITY flips or a slot/refresh completes — the list/header re-render (cheaply: rows
    /// whose inputs didn't change skip their bodies) without `rows` itself having to republish.
    @Published private(set) var rowsGen = 0
    func bumpRows() { LoopWatch.mark("bumpRows"); rowsGen &+= 1 }
    /// Per-download progress, published separately so ticks never re-render the list (see `ProgressHub`).
    let progressHub = ProgressHub()
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
    /// Category section keys the user collapsed (per-machine) — a collapsed section shows only its header.
    /// Holds category names (and, if collapsed, the pinned sentinel); stale keys are harmless.
    @Published var collapsedGroups: Set<String> = []
    /// Per-app selected variant id (per-machine), for apps that ship variants (e.g. NDI Light/Full).
    /// Absent → the app's default (first) variant.
    @Published var variantSelection: [String: String] = [:]

    private var selfInfo: SelfInfo?
    /// The download-relay base URL: an (invisible, settings-only) local override wins, else the
    /// catalog's built-in `downloadServer`. Nil only in a build whose catalog carries no server.
    private(set) var serverBase: String?
    /// App ids in catalog order — the baseline the user's saved ordering is applied over.
    private var catalogOrder: [String] = []
    /// Category section order from the catalog (drives the launcher's grouped list/grid).
    private(set) var catalogCategories: [String] = []
    /// Per-machine category section order — overrides the catalog order once the user drags a section.
    /// Empty until the user reorders; categories not in it fall back to catalog order, then alphabetical.
    private var categoryOrder: [String] = []
    private var explainerContinuation: CheckedContinuation<Void, Never>?
    private static let codeIDKey = "theatre.lastKeychainCodeID"
    private static let appOrderKey = "theatre.appOrder"
    private static let pinnedKey = "theatre.pinnedApps"
    private static let hiddenKey = "theatre.hiddenApps"
    private static let variantKey = "theatre.appVariants"
    private static let categoryOrderKey = "theatre.categoryOrder"
    private static let collapsedKey = "theatre.collapsedCategories"

    var currentVersion: String {
        (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0.0"
    }

    private var devTap: AnyCancellable?
    init() {
        if ProcessInfo.processInfo.environment["JBTT_LOOP_WATCH"] == "1" {
            devTap = objectWillChange.sink { LoopWatch.mark("AppState.willChange") }
        }
        do {
            let catalog = try Catalog.load()
            selfInfo = catalog.selfInfo
            serverBase = Self.serverOverride ?? catalog.downloadServer
            InstallManager.shared.migrateVariantSlots(catalog.apps)   // v1.15.0 → per-variant install slots
            let known = Set(catalog.apps.map(\.id))
            // Load the per-app variant choice BEFORE building rows, so each row reads its selected slot.
            if let saved = UserDefaults.standard.dictionary(forKey: Self.variantKey) as? [String: String] {
                variantSelection = saved.filter { known.contains($0.key) }
            }
            rows = catalog.apps.map {
                let key = installKey(for: $0)
                let installed = InstallManager.shared.installedVersion(key)
                return Row(app: $0,
                           installed: installed,
                           status: installed != nil ? .installed : .unknown,
                           resolvedName: nil,   // diagnostic-only; not parsed at boot (was a plist read per app)
                           variantSuffix: $0.variantSuffix(selectedVariantId($0)))
            }
            catalogOrder = catalog.apps.map(\.id)
            catalogCategories = catalog.categories ?? []
            categoryOrder = UserDefaults.standard.stringArray(forKey: Self.categoryOrderKey) ?? []
            collapsedGroups = Set(UserDefaults.standard.stringArray(forKey: Self.collapsedKey) ?? [])
            rows = Self.applyingSavedOrder(rows)
            // Pre-decode + downscale every row icon OFF the main thread, so the first frame doesn't stall on
            // NSWorkspace.icon(forFile:) / PNG decode for 21 rows.
            let prewarmPaths = rows.compactMap { InstallManager.shared.installedPath($0.id)?.path }
            let prewarmIds = rows.map(\.id)
            Task.detached(priority: .utility) { AppIconImage.prewarm(paths: prewarmPaths, ids: prewarmIds) }
            pinnedIds = Set(UserDefaults.standard.stringArray(forKey: Self.pinnedKey) ?? []).intersection(known)
            hiddenIds = Set(UserDefaults.standard.stringArray(forKey: Self.hiddenKey) ?? []).intersection(known)
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
        if let cached = WhatsNewNotes.loadCached() { applyWhatsNew(cached) }   // last relay copy, for offline starts
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
    /// User-invisible relay-URL override (defaults key only, no UI) — an escape hatch if the built-in
    /// catalog URL ever has to move for machines on an old build. Validated (https + a jamesbreedon.com
    /// host) so a stray defaults write can't redirect the passphrase to an attacker (audit F2).
    nonisolated static var serverOverride: String? {
        RelayPolicy.validatedOverride(UserDefaults.standard.string(forKey: "theatre.serverURL"))
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
        // Write only on change: this runs on every client build (each download and install phase), and ANY
        // UserDefaults write re-fires every `@AppStorage` in ContentView — a whole extra list re-render per
        // slot for a value that changes once per launcher update.
        let current = CodeIdentity.current()
        if UserDefaults.standard.string(forKey: Self.codeIDKey) != current {
            UserDefaults.standard.set(current, forKey: Self.codeIDKey)
        }
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

        // Mark every row "checking" in a SINGLE publish — mutate a local copy and assign `rows` once — so we
        // don't re-render the whole list 20× just to show the spinners (that churn was the boot-time lag).
        for row in rows {
            row.status = .checking
            row.installed = InstallManager.shared.installedVersion(installKey(for: row.app))
        }
        bumpRows()

        // Check all apps CONCURRENTLY instead of one-at-a-time (URLSession caps ~6 connections per host, so
        // this self-throttles). The sequential version took ~one network round-trip × 20 and re-rendered the
        // list on each result — seconds of churn on boot. Results now land close together and SwiftUI
        // coalesces them into far fewer renders. Each result addresses its row BY ID (audit F3, reorder-safe).
        let apps = rows.map { (id: $0.id, owner: $0.app.owner, repo: $0.app.repo) }
        // The relay's editable "New in" lines ride along with the update check (server mode only; nil = keep
        // what we have). Applied after the release results so the rows re-render once for both.
        async let notesFetch = client.whatsNewNotes()
        await withTaskGroup(of: (String, FetchOutcome).self) { group in
            for a in apps {
                group.addTask {
                    do { return (a.id, .releases(try await client.releases(owner: a.owner, repo: a.repo))) }
                    catch GitHubError.notAccessible { return (a.id, .noAccess) }
                    catch GitHubError.unauthorized { return (a.id, .unauthorized) }
                    catch GitHubError.noRelease { return (a.id, .noRelease) }
                    catch { return (a.id, .error(error.localizedDescription)) }
                }
            }
            for await (id, outcome) in group { applyRefreshOutcome(id: id, outcome: outcome) }
        }
        if let (notes, data) = await notesFetch {
            applyWhatsNew(notes)
            WhatsNewNotes.cache(data)
            AppLog.shared.log("what's-new: applied \(notes.notes.count) relay line(s)")
        }
        hasRefreshed = true
    }

    /// Overlays the relay's "New in" lines on every row (catalog line where the relay has none).
    func applyWhatsNew(_ notes: WhatsNewNotes) {
        for row in rows {
            let (line, version) = notes.resolved(for: row.app)
            if row.whatsNew != line { row.whatsNew = line }
            if row.whatsNewVersion != version { row.whatsNewVersion = version }
        }
    }

    /// Applies one concurrent check's outcome to its row (on the main actor), re-finding the row by id.
    private func applyRefreshOutcome(id: String, outcome: FetchOutcome) {
        guard let app = rows.first(where: { $0.id == id })?.app else { return }
        let slot = installKey(for: app)
        switch outcome {
        case .releases(let all):
            guard let latest = Self.latest(from: all) else {
                update(id) { $0.releases = all; $0.latestRelease = nil; $0.latest = nil; $0.latestAssetId = nil; $0.status = .noRelease }
                return
            }
            let assetId = latest.assets.first { $0.name == macAsset(for: app) }?.id
            let installedNow = InstallManager.shared.installedVersion(slot)
            update(id) {
                $0.releases = all
                $0.latestRelease = latest
                $0.latest = latest.tagName
                $0.latestAssetId = assetId
                $0.status = Self.status(installed: installedNow, latest: latest.tagName, hasAsset: assetId != nil)
            }
        case .noAccess:
            // Token can't see this repo → hide the row from the list.
            update(id) { $0.latest = nil; $0.latestAssetId = nil; $0.releases = []; $0.latestRelease = nil; $0.status = .noAccess }
        case .unauthorized:
            // The credential itself is bad — surface one clear message instead of N broken rows.
            update(id) { $0.status = .noAccess }
            globalError = Self.authMode == .token
                ? "Your GitHub token is invalid or expired. Open Settings to paste a new one."
                : "The download server rejected the passphrase. Check it in Settings."
            AppLog.shared.log("refresh: credentials rejected (\(Self.authMode.rawValue) mode)")
        case .noRelease:
            update(id) { $0.latest = nil; $0.releases = []; $0.latestRelease = nil; $0.status = .noRelease }
        case .error(let msg):
            update(id) { $0.status = .error(msg) }
            AppLog.shared.log("refresh \(id) error: \(msg)")
        }
    }

    /// Applies `body` to the row with this id, re-finding it each call — the reorder-safe way to write a
    /// row after an `await` (a concurrent drag/Move may have permuted `rows`). See `Self.write` for the
    /// pure, unit-tested core.
    private func update(_ id: String, _ body: (inout Row) -> Void) {
        // Read `rows` (never `&rows` — an inout access to a @Published array publishes it even when nothing
        // structural changed) and mutate the row OBJECT, so only that row re-renders. A visibility flip is the
        // one per-row change the list must know about.
        guard let row = rows.first(where: { $0.id == id }) else { return }
        let wasVisible = row.isVisible
        var ref = row
        body(&ref)
        if row.isVisible != wasVisible { bumpRows() }
    }

    /// Writes to the row with `id` in `rows` (no-op if absent). Pure + nonisolated so a regression test
    /// can prove it hits the right row even after `rows` is reordered (audit F3).
    nonisolated static func write(into rows: inout [Row], id: String, _ body: (inout Row) -> Void) {
        if let j = rows.firstIndex(where: { $0.id == id }) { body(&rows[j]) }
    }

    /// True once a refresh has run and the token reached no apps (and nothing is installed locally) —
    /// drives the "this token can't access any apps" empty state.
    var noAppsAccessible: Bool {
        hasRefreshed && !rows.isEmpty && rows.allSatisfy { !$0.isVisible }
    }

    /// Number of INSTALLED slots (default + Full editions) with an update available — drives the header
    /// "Update All (N)" label. Counts slots, not rows, so an installed Full edition that's out of date is
    /// included even when the row's toggle is showing the Light edition.
    var updatesAvailable: Int { rows.reduce(0) { $0 + slotsToUpdate($1.app).count } }

    /// Installed slots of an app (default + every Full edition) whose latest release is newer than what's on
    /// disk. Update All used to act on `row.status == .updateAvailable`, which reflects only the SELECTED
    /// variant — so an installed Full edition was never updated unless its toggle happened to be on.
    private func slotsToUpdate(_ app: CatalogApp) -> [String?] {
        guard let row = rows.first(where: { $0.id == app.id }),
              let latest = row.latestRelease else { return [] }
        var variants: [String?] = [app.hasVariants ? app.variants?.first?.id : nil]
        if let vs = app.variants { variants += vs.dropFirst().map { $0.id } }
        return variants.filter { vid in
            guard let name = app.macAssetName(variantId: vid),
                  latest.assets.contains(where: { $0.name == name }),
                  let installed = InstallManager.shared.installedVersion(app.installKey(variantId: vid)) else { return false }
            return Self.versionIsNewer(latest.tagName, than: installed)
        }
    }

    /// True when any catalog app ships a Full edition (a second variant) — gates the "incl. Full" option.
    var hasFullVariants: Bool { rows.contains { ($0.app.variants?.count ?? 0) > 1 } }

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

    // MARK: Category grouping (Pinned floats to the top; the rest group under their catalog category)

    /// Sentinel key for the Pinned group (can't collide with a real category name).
    static let pinnedGroupKey = "\u{1}pinned"
    private static let uncategorised = "Other"

    /// The category a row belongs to for grouping (falls back to "Other" for an app with no category).
    func categoryOf(_ row: Row) -> String {
        let c = row.app.category?.trimmingCharacters(in: .whitespaces) ?? ""
        return c.isEmpty ? Self.uncategorised : c
    }

    /// The display GROUP a row is in: the Pinned group when pinned, else its category. Reorder/drag stay
    /// within one group (moving an app between categories isn't a thing — category is a catalog property).
    func groupKey(_ row: Row) -> String { pinnedIds.contains(row.id) ? Self.pinnedGroupKey : categoryOf(row) }
    func groupKey(of id: String) -> String { rows.first { $0.id == id }.map { groupKey($0) } ?? "" }

    /// One rendered section: a key (pinned sentinel or category name), its heading, and its rows in order.
    struct DisplayGroup: Identifiable {
        let key: String
        let title: String
        let rows: [Row]
        var id: String { key }
    }

    /// The launcher's sections in render order: Pinned first (if any), then each category in the effective
    /// order — the user's saved section order (if they've reordered), then the catalog's `categories` order
    /// for any not covered, then any leftover category alphabetically. Empty categories are dropped.
    var displayGroups: [DisplayGroup] {
        var groups: [DisplayGroup] = []
        let pinned = pinnedDisplayRows
        if !pinned.isEmpty { groups.append(DisplayGroup(key: Self.pinnedGroupKey, title: "Pinned", rows: pinned)) }
        let main = mainDisplayRows
        var order: [String] = []
        for c in categoryOrder where !order.contains(c) { order.append(c) }          // user's saved order first
        for c in catalogCategories where !order.contains(c) { order.append(c) }       // then catalog order
        for c in Set(main.map { categoryOf($0) }).sorted() where !order.contains(c) { order.append(c) }
        for cat in order {
            let rows = main.filter { categoryOf($0) == cat }
            if !rows.isEmpty { groups.append(DisplayGroup(key: cat, title: cat, rows: rows)) }
        }
        return groups
    }

    /// Commits a drag reorder of ONE group (Pinned or a category): reassigns that group's visible rows to
    /// follow `orderedIds`, among the slots they occupy in `rows`, then persists. Called once, on drop.
    func applyGroupOrder(key: String, orderedIds: [String]) {
        let byId = Dictionary(uniqueKeysWithValues: rows.map { ($0.id, $0) })
        let slots = rows.indices.filter { groupKey(rows[$0]) == key && rows[$0].isVisible && !hiddenIds.contains(rows[$0].id) }
        for (k, slot) in slots.enumerated() where k < orderedIds.count {
            if let r = byId[orderedIds[k]] { rows[slot] = r }
        }
        persistOrder()
        AppLog.shared.log("reordered a group by drag")
    }

    // MARK: Collapse / reorder whole category sections (per-machine)

    func isCollapsed(_ key: String) -> Bool { collapsedGroups.contains(key) }

    /// Toggles a section's collapsed state (call inside `withAnimation` for the fold to animate) and persists.
    func toggleCollapsed(_ key: String) {
        if collapsedGroups.contains(key) { collapsedGroups.remove(key) } else { collapsedGroups.insert(key) }
        UserDefaults.standard.set(Array(collapsedGroups), forKey: Self.collapsedKey)
        AppLog.shared.log("\(collapsedGroups.contains(key) ? "collapsed" : "expanded") section \(key)")
    }

    /// The category section keys in their current display order (excluding Pinned) — the baseline a live
    /// section drag reorders.
    var categoryOrderKeys: [String] { displayGroups.map(\.key).filter { $0 != Self.pinnedGroupKey } }

    /// Rows (ids) that render under a given section key — used by the list's live section drag to find each
    /// section's vertical extent.
    func rowIds(inSection key: String) -> [String] {
        displayGroups.first { $0.key == key }?.rows.map(\.id) ?? []
    }

    /// Commits a full category-section order (from the list's live section drag), persisting it per-machine.
    func setCategoryOrder(_ order: [String]) {
        categoryOrder = order
        UserDefaults.standard.set(order, forKey: Self.categoryOrderKey)
        AppLog.shared.log("reordered category sections (drag)")
    }

    /// Reorders the CATEGORY sections: moves section `key` next to `target` (drag forward lands after, back
    /// lands before — the same feel as a row drop), persisting the full new order. Pinned is never part of
    /// this — it always floats to the top; a move touching it is a no-op.
    func moveCategory(_ key: String, onto target: String) {
        guard key != target, key != Self.pinnedGroupKey, target != Self.pinnedGroupKey else { return }
        var order = displayGroups.map(\.key).filter { $0 != Self.pinnedGroupKey }   // current full category order
        guard let from = order.firstIndex(of: key), let to = order.firstIndex(of: target) else { return }
        let movedForward = from < to
        order.remove(at: from)
        guard let ti = order.firstIndex(of: target) else { return }
        order.insert(key, at: movedForward ? ti + 1 : ti)
        categoryOrder = order
        UserDefaults.standard.set(order, forKey: Self.categoryOrderKey)
        AppLog.shared.log("reordered category sections")
    }

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

    // MARK: Variants (apps that ship more than one download, e.g. NDI Light/Full)

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

    /// The install-manifest key of the row's SELECTED variant slot. Every variant is its own slot, so
    /// Light and Full can both be installed; the toggle just chooses which slot the row shows.
    func installKey(for app: CatalogApp) -> String {
        app.installKey(variantId: selectedVariantId(app))
    }

    /// The selected-slot install key for a row id (used by the alias/Dock/launch/uninstall actions).
    private func slotKey(_ rowId: String) -> String? {
        rows.first { $0.id == rowId }.map { installKey(for: $0.app) }
    }

    /// Changes the selected variant for an app, persists it, and switches the row to that variant's
    /// install slot: re-reads what's installed there and re-resolves the latest asset + status.
    func setVariant(_ appId: String, _ variantId: String) {
        variantSelection[appId] = variantId
        UserDefaults.standard.set(variantSelection, forKey: Self.variantKey)
        if let i = rows.firstIndex(where: { $0.id == appId }) {
            let key = installKey(for: rows[i].app)
            rows[i].installed = InstallManager.shared.installedVersion(key)
            rows[i].variantSuffix = rows[i].app.variantSuffix(variantId)
            recomputeRow(i)
        }
        AppLog.shared.log("variant for \(appId) → \(variantId)")
    }

    /// Re-derives latestAssetId + status for a row from its cached releases (after a variant change).
    /// With no cached releases yet (pre-refresh), the row simply reflects whether the slot is installed.
    private func recomputeRow(_ i: Int) {
        guard rows.indices.contains(i) else { return }
        guard let latest = rows[i].latestRelease else {
            rows[i].status = rows[i].installed != nil ? .installed : .unknown
            return
        }
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

    /// Pure in-place drag-reorder (list & grid): moves `draggedId` next to `targetId` within the same pin
    /// group, WITHOUT persisting or logging. The live drag calls this on every row/tile the cursor passes
    /// (so the dashed slot tracks where it'll land), then `commitReorder()` once when the drop completes.
    /// Moves within the same pin group only (change groups via Pin/Unpin). Dragging forward lands after the
    /// target, dragging backward before — so a drop feels like "put it here". Returns whether it moved.
    @discardableResult
    func reorder(_ draggedId: String, onto targetId: String) -> Bool {
        guard draggedId != targetId,
              groupKey(of: draggedId) == groupKey(of: targetId),   // same group only (Pinned, or one category)
              let di = rows.firstIndex(where: { $0.id == draggedId }),
              let tiOrig = rows.firstIndex(where: { $0.id == targetId }) else { return false }
        let draggedWasBefore = di < tiOrig
        let moved = rows.remove(at: di)
        guard let ti = rows.firstIndex(where: { $0.id == targetId }) else {
            rows.insert(moved, at: min(di, rows.count)); return false   // target vanished mid-drag: put it back
        }
        rows.insert(moved, at: draggedWasBefore ? ti + 1 : ti)
        return true
    }

    /// Live drag-move: place `id` at `index` within its own display group (pinned or main), reassigning the
    /// group members among the slots they occupy in `rows` — WITHOUT persisting. The gesture calls this
    /// every frame the target slot changes; `commitReorder()` persists once on drop. Returns whether it
    /// actually moved (so a frame where nothing changes is a no-op and triggers no re-render).
    @discardableResult
    func dragMove(_ id: String, toGroupIndex index: Int) -> Bool {
        let pinned = pinnedIds.contains(id)
        var groupIds = (pinned ? pinnedDisplayRows : mainDisplayRows).map(\.id)
        guard let cur = groupIds.firstIndex(of: id) else { return false }
        let clamped = max(0, min(index, groupIds.count - 1))
        guard clamped != cur else { return false }
        groupIds.move(fromOffsets: IndexSet(integer: cur), toOffset: clamped > cur ? clamped + 1 : clamped)
        let byId = Dictionary(uniqueKeysWithValues: rows.map { ($0.id, $0) })
        let slots = rows.indices.filter {
            let rowPinned = pinnedIds.contains(rows[$0].id)
            return rowPinned == pinned && rows[$0].isVisible && !hiddenIds.contains(rows[$0].id)
        }
        for (k, slot) in slots.enumerated() where k < groupIds.count {
            if let row = byId[groupIds[k]] { rows[slot] = row }
        }
        return true
    }

    /// Persists the row order after a drag reorder — called once when the drop lands.
    func commitReorder() {
        persistOrder()
        AppLog.shared.log("reordered apps by drag")
    }

    /// Commits a full drag reorder (called once, on drop): reassigns the visible pinned rows to follow
    /// `pinnedOrder` and the visible main rows to follow `mainOrder`, among the slots they occupy in `rows`,
    /// then persists. The drag itself reorders a LOCAL copy of these arrays and never touches `rows`, so the
    /// app doesn't re-render mid-drag — this applies the result once at the end.
    func applyDragOrder(pinnedOrder: [String], mainOrder: [String]) {
        applyGroupOrder(pinnedOrder, pinned: true)
        applyGroupOrder(mainOrder, pinned: false)
        persistOrder()
        AppLog.shared.log("reordered apps by drag")
    }

    private func applyGroupOrder(_ orderIds: [String], pinned: Bool) {
        let byId = Dictionary(uniqueKeysWithValues: rows.map { ($0.id, $0) })
        let slots = rows.indices.filter {
            pinnedIds.contains(rows[$0].id) == pinned && rows[$0].isVisible && !hiddenIds.contains(rows[$0].id)
        }
        for (k, slot) in slots.enumerated() where k < orderIds.count {
            if let r = byId[orderIds[k]] { rows[slot] = r }
        }
    }

    /// One-shot drag-reorder where a single drop is the whole gesture: reorder + persist together. Returns
    /// whether it actually moved — the grid drop returns this so an invalid drop (a different section) lets
    /// the system float the drag image back home, instead of silently accepting a no-op.
    @discardableResult
    func moveRow(_ draggedId: String, onto targetId: String) -> Bool {
        if reorder(draggedId, onto: targetId) { commitReorder(); return true }
        return false
    }

    /// Restores the catalog's default order — both the app order within sections AND the section order
    /// (Settings → Reset App Order). Pins, hides and collapsed sections are left as-is.
    func resetAppOrder() {
        UserDefaults.standard.removeObject(forKey: Self.appOrderKey)
        UserDefaults.standard.removeObject(forKey: Self.categoryOrderKey)
        categoryOrder = []
        let index = Dictionary(uniqueKeysWithValues: catalogOrder.enumerated().map { ($1, $0) })
        rows.sort { (index[$0.id] ?? .max) < (index[$1.id] ?? .max) }
        AppLog.shared.log("app + section order reset to catalog default")
    }

    /// Nearest neighbour in the given direction that is visible, not hidden, and in the SAME group
    /// (Pinned, or the same category) — so Move Up/Down never jumps a row across a section boundary.
    private func groupNeighbour(of id: String, up: Bool) -> Int? {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return nil }
        let key = groupKey(of: id)
        let range = up ? Array((0..<i).reversed()) : Array((i + 1)..<rows.count)
        return range.first {
            rows[$0].isVisible && !hiddenIds.contains(rows[$0].id) && groupKey(rows[$0]) == key
        }
    }

    // MARK: - Desktop alias & Dock pin (per-app, from the row menu; best-effort like win shortcuts)

    func hasDesktopAlias(_ id: String) -> Bool {
        guard let key = slotKey(id) else { return false }
        return InstallManager.shared.hasDesktopAlias(key)
    }

    func isDockPinned(_ id: String) -> Bool {
        guard let key = slotKey(id), let path = InstallManager.shared.installedPath(key)?.path else { return false }
        return Dock.isPinned(path)
    }

    func toggleDesktopAlias(_ id: String) {
        guard let key = slotKey(id) else { return }
        if hasDesktopAlias(id) {
            InstallManager.shared.removeDesktopAlias(key)
            AppLog.shared.log("removed desktop alias for \(key)")
        } else {
            do {
                try InstallManager.shared.addDesktopAlias(key)
                AppLog.shared.log("added desktop alias for \(key)")
            } catch {
                AppLog.shared.log("desktop alias for \(key) FAILED: \(error.localizedDescription)")
            }
        }
    }

    func toggleDockPin(_ id: String) {
        guard let key = slotKey(id), let path = InstallManager.shared.installedPath(key)?.path else { return }
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
        let work = orderedSlots(rows.flatMap { row in slotsToUpdate(row.app).map { (row.id, $0) } })
        AppLog.shared.log("update all: \(work.count) slot(s)")
        await runSlots(work, label: "update all")
    }

    /// Default editions first, then Full editions — so consecutive slots are (almost always) different apps and
    /// the pipelined runner overlaps two apps rather than two slots of one row.
    private func orderedSlots(_ slots: [(String, String?)]) -> [(String, String?)] {
        func isDefault(_ s: (String, String?)) -> Bool {
            rows.first { $0.id == s.0 }.map { $0.app.isDefaultVariant(s.1) } ?? true
        }
        return slots.filter { isDefault($0) } + slots.filter { !isDefault($0) }
    }

    /// Runs install slots with ONE download of lookahead: while slot N verifies + extracts (CPU/disk), slot
    /// N+1 is already downloading (network). Serially, the network sat idle through every hash + extract and
    /// the disk idle through every download — minutes per Download All on a slow machine. Downloads
    /// themselves stay serial (venue Wi-Fi is the bottleneck; two at once would just split it) and at most two
    /// downloaded zips exist at any moment (the one being installed + the one arriving).
    private func runSlots(_ work: [(String, String?)], label: String) async {
        var next: Task<Downloaded?, Never>? = nil
        for (i, slot) in work.enumerated() {
            let current = next ?? Task { await self.downloadPhase(slot.0, tag: nil, variantOverride: slot.1) }
            let d = await current.value                       // download N done (or failed + recorded)
            next = nil
            if i + 1 < work.count {
                let n = work[i + 1]                           // start download N+1 now…
                next = Task { await self.downloadPhase(n.0, tag: nil, variantOverride: n.1) }
            }
            if let d { await installPhase(d, id: slot.0) }    // …and verify + extract N meanwhile
        }
        AppLog.shared.log("\(label) complete")
    }

    /// True if any app has something to fetch — a not-installed or updatable slot (drives the Download All
    /// button's enabled state). Checks the default variant, and Full editions when `includeFull`.
    func hasAnyToDownload(includeFull: Bool) -> Bool {
        rows.contains { row in
            slotsToDownload(row.app, includeFull: includeFull).isEmpty == false
        }
    }

    /// Which variant slots of an app need fetching now: a slot whose asset exists in the latest release
    /// (so it's reachable on this OS/arch) and that isn't already installed at the latest version.
    private func slotsToDownload(_ app: CatalogApp, includeFull: Bool) -> [String?] {
        guard let row = rows.first(where: { $0.id == app.id }),
              let latest = row.latestRelease else { return [] }
        var variants: [String?] = [app.hasVariants ? app.variants?.first?.id : nil]
        if includeFull, let vs = app.variants { variants += vs.dropFirst().map { $0.id } }
        return variants.filter { vid in
            guard let name = app.macAssetName(variantId: vid),
                  latest.assets.contains(where: { $0.name == name }) else { return false }   // no asset for this arch → skip
            let installed = InstallManager.shared.installedVersion(app.installKey(variantId: vid))
            return installed == nil || Self.versionIsNewer(latest.tagName, than: installed!)
        }
    }

    /// Download All: install/update every app. `includeFull` also fetches Full editions (their own slots,
    /// so Light and Full end up installed side by side). Skips anything already current or with no
    /// asset for this OS/arch.
    func downloadAll(includeFull: Bool) async {
        let work = orderedSlots(rows.flatMap { row in slotsToDownload(row.app, includeFull: includeFull).map { (row.id, $0) } })
        AppLog.shared.log("download all\(includeFull ? " (incl. Full)" : ""): \(work.count) slot(s)")
        await runSlots(work, label: "download all\(includeFull ? " (incl. Full)" : "")")
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
        // First contiguous digit run per segment: skip leading non-digits, take the digits, stop at the next
        // non-digit. Handles date-style tags like "build-20260912" (→ 20260912) for a rolling app such as
        // Convert, while staying identical for ordinary semver segments ("2", "0-rc1" → 0).
        func firstNumber(_ seg: Substring) -> Int {
            var run = ""
            for ch in seg {
                if ch.isNumber { run.append(ch) }
                else if !run.isEmpty { break }
            }
            return Int(run) ?? 0
        }
        func parts(_ s: String) -> [Int] {
            norm(s).split(separator: ".").map(firstNumber)
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
        let sumsURL = InstallManager.shared.cacheDir.appendingPathComponent("\(app.id)-\(PathSafe.component(release.tagName))-SHA256SUMS")
        try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: sumsAsset.id, to: sumsURL)
        let sumsData = (try? Data(contentsOf: sumsURL)) ?? Data()
        try? fm.removeItem(at: sumsURL)

        // Authenticity (audit F1): the manifest is trusted only if the controller's OFFLINE minisign
        // signature over it verifies with the embedded suite key, and its signed trusted comment names
        // THIS release ("<repo> <tag>") so a manifest from another release can't be replayed. A present
        // but invalid signature is always fatal; an absent one downgrades the outcome to `.unsigned`.
        var signed = false
        if let sigAsset = release.assets.first(where: { $0.name == "SHA256SUMS.minisig" }) {
            let sigURL = InstallManager.shared.cacheDir.appendingPathComponent("\(app.id)-\(PathSafe.component(release.tagName))-SHA256SUMS.minisig")
            try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: sigAsset.id, to: sigURL)
            let sigText = (try? String(contentsOf: sigURL, encoding: .utf8)) ?? ""
            try? fm.removeItem(at: sigURL)
            do {
                try Minisign.verify(manifest: sumsData, minisig: sigText,
                                    expectedTrustedComment: "\(app.repo) \(release.tagName)")
            } catch {
                try? fm.removeItem(at: file)
                throw error
            }
            signed = true
        }

        let text = String(decoding: sumsData, as: UTF8.self)
        do {
            guard try InstallManager.verify(file: file, assetName: asset.name, sums: text) else { return .assetNotListed }
            return signed ? .verified : .unsigned
        } catch {
            try? fm.removeItem(at: file)
            throw error
        }
    }

    /// Why a non-`.verified` outcome blocks a strict (current-release) install — shared by the GUI
    /// install, the launcher self-update and the CLI so the wording stays consistent.
    nonisolated static func strictFailureReason(_ result: VerifyResult, assetName: String) -> String {
        switch result {
        case .verified:       return "verified"
        case .noManifest:     return "this release publishes no SHA256SUMS checksums"
        case .assetNotListed: return "“\(assetName)” isn’t listed in this release’s SHA256SUMS"
        case .unsigned:       return "this release’s SHA256SUMS isn’t signed with the suite key"
        }
    }

    // MARK: - Install / update / uninstall / launch

    /// Installs an app — the latest release, or a specific `tag` (for installing older versions).
    /// `variantOverride` installs a specific variant slot regardless of the row's on-screen selection
    /// (used by Download All to fetch Light and/or Full); nil = the row's selected variant.
    /// Everything phase 1 hands to phase 2: the resolved release/asset and the verified-later zip in the cache.
    struct Downloaded: Sendable {
        let app: CatalogApp; let rel: ReleaseInfo; let asset: ReleaseAsset; let zip: URL
        let variantId: String?; let tag: String?
    }

    private func beginBusy(_ id: String) { update(id) { $0.busyCount += 1; $0.busy = true } }
    private func endBusy(_ id: String) {
        update(id) { $0.busyCount = max(0, $0.busyCount - 1); $0.busy = $0.busyCount > 0 }
        progressHub.clear(id)
    }

    /// Installs an app — the latest release, or a specific `tag` (for installing older versions).
    /// `variantOverride` installs a specific variant slot regardless of the row's on-screen selection
    /// (used by Download All to fetch Light and/or Full); nil = the row's selected variant.
    /// Two phases so Download All can overlap them (see `runSlots`): phase 1 is network-bound, phase 2 is
    /// CPU/disk-bound. A single interactive install just runs them back to back.
    func install(_ id: String, tag: String? = nil, variantOverride: String? = nil) async {
        guard let d = await downloadPhase(id, tag: tag, variantOverride: variantOverride) else { return }
        await installPhase(d, id: id)
    }

    /// Phase 1 — resolve the release + asset and download it into the cache. Marks the row busy for the whole
    /// slot; on failure it records the error, ends busy and returns nil. Nothing here touches the disk beyond
    /// the download itself.
    private func downloadPhase(_ id: String, tag: String?, variantOverride: String?) async -> Downloaded? {
        guard let app = rows.first(where: { $0.id == id })?.app else { return nil }
        await ensureKeychainExplained()
        guard let client = activeClient() else { return nil }
        // Address the row BY ID after every `await` — a concurrent drag/Move can permute `rows` while
        // this runs, so a captured index would write to the wrong app (audit F3).
        beginBusy(id)
        progressHub.set(id, 0)
        var releases = rows.first(where: { $0.id == id })?.releases ?? []
        if releases.isEmpty {
            do { releases = try await client.releases(owner: app.owner, repo: app.repo) }
            catch { update(id) { $0.status = .error(error.localizedDescription) }; endBusy(id); return nil }
            let picked = Self.latest(from: releases)
            update(id) { $0.releases = releases; $0.latestRelease = picked }
        }

        let release = tag != nil ? releases.first { $0.tagName == tag }
                                 : (rows.first(where: { $0.id == id })?.latestRelease ?? Self.latest(from: releases))
        guard let rel = release else {
            update(id) { $0.status = .error("Version \(tag ?? "latest") not found.") }; endBusy(id); return nil
        }
        let variantId = variantOverride ?? selectedVariantId(app)
        guard let asset = rel.assets.first(where: { $0.name == app.macAssetName(variantId: variantId) }) else {
            update(id) { $0.status = .error("No macOS asset in \(rel.tagName).") }; endBusy(id); return nil
        }

        let zipDest = InstallManager.shared.cacheDir.appendingPathComponent("\(app.id)-\(PathSafe.component(rel.tagName)).zip")
        let appId = id
        do {
            try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: asset.id, to: zipDest) { p in
                // Progress goes to the hub, NOT `rows`: only the row's progress bar re-renders (≥1% steps).
                Task { @MainActor [weak self] in self?.progressHub.set(appId, p) }
            }
            return Downloaded(app: app, rel: rel, asset: asset, zip: zipDest, variantId: variantId, tag: tag)
        } catch {
            update(id) { $0.status = .error(error.localizedDescription) }
            AppLog.shared.log("install \(app.id) FAILED: \(error.localizedDescription)")
            endBusy(id)
            return nil
        }
    }

    /// Phase 2 — verify (size + signed SHA256SUMS + hash), then extract + install OFF the main actor, then
    /// reflect it in the row. Always ends the row's busy state.
    private func installPhase(_ d: Downloaded, id: String) async {
        let (app, rel, asset, zipDest, variantId, tag) = (d.app, d.rel, d.asset, d.zip, d.variantId, d.tag)
        defer { endBusy(id) }
        guard let client = activeClient() else { return }
        do {
            let verification = try await Self.verifyDownload(zipDest, asset: asset, release: rel, app: app, client: client)
            // Strict for current releases: a "latest" install (tag == nil) MUST checksum-verify — every
            // current release ships a correct SHA256SUMS, so a missing/incomplete manifest here is
            // anomalous (e.g. tampering) → abort. Explicit older-tag installs (the version picker) may
            // predate the manifest, so they stay verify-if-present. A hash MISMATCH always aborts (it
            // throws from verifyDownload) regardless of tag; size is always checked too.
            if tag == nil, verification != .verified {
                Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: zipDest) }   // 300-450 MB unlink: never on main
                let reason = Self.strictFailureReason(verification, assetName: asset.name)
                AppLog.shared.log("install \(app.id) \(rel.tagName): BLOCKED (strict) — \(reason)")
                throw InstallError.unverified(reason: reason)
            }
            switch verification {
            case .verified:       AppLog.shared.log("verified \(app.id) \(rel.tagName) (signed sha256)")
            case .unsigned:       AppLog.shared.log("install \(app.id) \(rel.tagName): older tag — sha256 ok but manifest UNSIGNED")
            case .noManifest:     AppLog.shared.log("install \(app.id) \(rel.tagName): unverified older tag (no SHA256SUMS)")
            case .assetNotListed: AppLog.shared.log("install \(app.id) \(rel.tagName): unverified older tag (asset not in SHA256SUMS)")
            }
            let toApps = UserDefaults.standard.bool(forKey: "theatre.installToApplications")
            let tagName = rel.tagName
            let slotKey = app.installKey(variantId: variantId)
            // Everything disk-heavy OFF the main actor, in one detached block: `ditto` (seconds for a Full
            // edition), the unlink of the 300-450 MB zip (12-50 ms on a busy disk — it used to sit on the main
            // actor right at the row flip), the freshly-installed app's icon (NSWorkspace + thumbnail, so the
            // first post-install render hits the cache instead of resolving it on main), and the diagnostic
            // display-name read. The zip is removed even when install() throws (a small SSD win — the next
            // attempt re-downloads to the same path anyway); `_ = try` keeps the throw propagating.
            let installedName: String? = try await Task.detached(priority: .userInitiated) {
                defer { try? FileManager.default.removeItem(at: zipDest) }
                let dest = try InstallManager.shared.install(app: app, version: tagName, downloadedZip: zipDest,
                                                             toApplications: toApps, variant: variantId)
                AppIconImage.refreshInstalledIcon(dest.path)
                return InstallManager.shared.installedDisplayName(slotKey)
            }.value
            LoopWatch.mark("install-main begin \(app.id)")
            // Only reflect the install in the row when it's the variant currently shown — a Download All
            // that fetches a non-selected Full edition into its own slot mustn't hijack the row's display.
            if variantId == selectedVariantId(app) {
                update(id) {
                    $0.installed = rel.tagName
                    $0.resolvedName = installedName
                    $0.status = Self.status(installed: rel.tagName, latest: $0.latest ?? rel.tagName, hasAsset: true)
                }
            }
            AppLog.shared.log("installed \(app.id) \(rel.tagName)\(variantId.map { " [\($0)]" } ?? "")\(toApps ? " (Applications)" : "")")
            bumpRows()   // header counts (Update All (N) / Download All) re-derive once per slot
            LoopWatch.mark("install-main end \(app.id)")
        } catch {
            update(id) { $0.status = .error(error.localizedDescription) }
            AppLog.shared.log("install \(app.id) FAILED: \(error.localizedDescription)")
        }
    }

    /// UI entry point: the removal (a one-dir Full edition is thousands of files, 300–450 MB — seconds of unlink)
    /// runs off the main actor with the row busy, then the row is updated. `uninstall(_:)` below stays
    /// synchronous for the CLI.
    func uninstallAsync(_ id: String) async {
        guard let row = rows.first(where: { $0.id == id }) else { return }
        let key = installKey(for: row.app)
        beginBusy(id)
        let failure: String? = await Task.detached(priority: .userInitiated) {
            do { try InstallManager.shared.uninstall(key); return nil } catch { return error.localizedDescription }
        }.value
        endBusy(id)
        applyUninstallOutcome(id, failure: failure)
    }

    private func applyUninstallOutcome(_ id: String, failure: String?) {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return }
        if let failure {
            rows[i].status = .error(failure)
            AppLog.shared.log("uninstall \(id) FAILED: \(failure)")
            return
        }
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
        bumpRows()   // header counts (Download All) re-derive
    }

    func uninstall(_ id: String) {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return }
        do {
            // Uninstalls the SELECTED variant's slot only (the other variant, if installed, stays).
            try InstallManager.shared.uninstall(installKey(for: rows[i].app))
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
            try InstallManager.shared.launch(installKey: installKey(for: rows[i].app))
        } catch {
            rows[i].status = .error(error.localizedDescription)
        }
    }

    // MARK: - Install-location relocation

    /// Called when the "install to Applications" setting changes. If some installed apps are still in
    /// the other location, raises a confirmation to move them all so the setting stays truthful.
    func installLocationChanged(toApplications: Bool) {
        // Count every installed slot (both variants of a variant app), not just the selected ones.
        let count = InstallManager.shared.manifest().keys.filter {
            InstallManager.shared.needsRelocation($0, toApplications: toApplications)
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
        // Move every installed slot (both variants of a variant app), then refresh each row from its
        // selected slot.
        for key in InstallManager.shared.manifest().keys.sorted() {
            let needed = InstallManager.shared.needsRelocation(key, toApplications: toApplications)
            do {
                try InstallManager.shared.relocate(key, toApplications: toApplications)
                if needed { moved += 1 }
            } catch {
                failed.append(rows.first { installKey(for: $0.app) == key }?.displayName ?? key)
            }
        }
        for i in rows.indices {
            let key = installKey(for: rows[i].app)
            rows[i].installed = InstallManager.shared.installedVersion(key)
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
            let dest = downloads.appendingPathComponent(PathSafe.component(asset.name))
            try await client.downloadAsset(owner: s.owner, repo: s.repo, assetId: asset.id, to: dest)
            // Strict verify (this is a current release): size + suite-signed SHA256SUMS + hash, through
            // the same path every app install uses. A bad hash or bad signature throws (and deletes the
            // file); anything short of `.verified` is refused too.
            let verification = try await Self.verifyDownload(dest, asset: asset, release: info,
                                                             app: CatalogApp.forSelf(s), client: client)
            guard verification == .verified else {
                try? FileManager.default.removeItem(at: dest)
                launcherDownloadMessage = "Couldn't verify the update — \(Self.strictFailureReason(verification, assetName: asset.name)). Not saved."
                return
            }
            NSWorkspace.shared.activateFileViewerSelecting([dest])
            launcherDownloadMessage = "Saved \(asset.name) to your Downloads folder — quit JB Theatre Tools and replace it."
        } catch {
            launcherDownloadMessage = "Download failed: \(error.localizedDescription)"
        }
    }
}
