import Foundation
import SwiftUI
import AppKit
import Combine
import UniformTypeIdentifiers

/// Where downloads authenticate: a personal GitHub token (direct API access), or the suite's
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

/// The one secondary sheet the launcher shows at a time (release notes, an app's details, the activity history,
/// a setup-file preview) — a single `.sheet(item:)` in ContentView presents whichever is set.
enum LauncherSheet: Identifiable {
    case notes(NotesModel)
    case details(String)                 // row id
    case activity([ActivityEvent])
    case importPreview(ImportPreviewModel)

    var id: String {
        switch self {
        case .notes: return "notes"
        case .details(let id): return "details-\(id)"
        case .activity: return "activity"
        case .importPreview: return "import"
        }
    }
}

/// In-app release notes: releases newest first, with the installed version to flag "New since your version".
struct NotesModel {
    let title: String
    let installed: String?
    var releases: [ReleaseInfo]
    var loading: Bool
    /// Shown when there are no releases to list (none yet, or the launcher's catalog line offline).
    var message: String?
}

/// What importing a setup file would do, shown before anything installs.
struct ImportPreviewModel {
    let source: String
    let summary: String
    let plan: SetupPlanner.Plan
    let layout: SetupProfile.Layout?
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
        @Published var status: Status = .unknown { didSet { if status != .checking { settledStatus = status } } }
        /// The last status that wasn't "Checking…". Visibility and the status filter use it while a check runs, so
        /// a refresh neither empties a filtered list nor flashes rows out of the list and back in.
        private(set) var settledStatus: Status = .unknown
        @Published var busy: Bool = false
        /// A download of this row is in flight and can be cancelled (the row's Cancel button).
        @Published var cancellable: Bool = false
        /// Slots of THIS app currently in flight (a pipelined Download All can have the Full edition downloading
        /// while the Light edition is still extracting). `busy` mirrors `busyCount > 0`.
        var busyCount: Int = 0
        /// A slot of this app is being verified / installed (not just downloaded). Launch waits only for this: a
        /// download — say an automatic update on slow venue Wi-Fi — never stops the operator opening the app.
        @Published var installing: Bool = false
        var installingCount: Int = 0
        /// The installed app's self-declared bundle name (kept for diagnostics only — NOT shown; see displayName).
        @Published var resolvedName: String?
        /// Suffix for the selected non-default variant (e.g. " (Full)"), appended to the curated name so a Full
        /// install reads consistently regardless of what the app calls its own bundle.
        @Published var variantSuffix: String = ""
        /// The "New in" line actually shown: the catalog's, overlaid by the relay's editable notes
        /// (`WhatsNewNotes`) when the relay has one for this app.
        @Published var whatsNew: String?
        @Published var whatsNewVersion: String?
        /// Name to show: the launcher's CURATED catalog name (the suite's curated naming) + the variant suffix. We do NOT
        /// use the installed bundle's self-name — several bundles diverge from the curated name (e.g. the
        /// Convert app calls itself "Convert to it!", Network Port Map's bundle is "Build Port Map", Show
        /// Dashboard's is "ShowDashboard"), and the catalog name is the suite's curated name.
        var displayName: String { app.name + variantSuffix }
        /// The error text when the row is in `.error`, else nil (the row shows it as a tooltip).
        var errorMessage: String? { if case .error(let m) = status { return m } else { return nil } }
        /// Shown once we know a row is relevant: anything installed locally, or any app whose repo
        /// the token is confirmed to reach. Not-yet-checked / inaccessible not-installed rows stay
        /// hidden, so inaccessible apps never flash into view and back out during a refresh.
        var isVisible: Bool {
            if installed != nil { return true }
            switch status == .checking ? settledStatus : status {
            case .unknown, .checking, .noAccess: return false
            // Nothing released on this Mac's channel (an app with only development builds so far, or none):
            // shown only with the Development builds switch on, so everyone else never sees a dead row.
            case .noRelease: return AppState.devChannel
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
    func bumpRows() { LoopWatch.mark("bumpRows"); rowsGen &+= 1; syncChrome() }

    /// What the menu bar commands and the menu-bar extra show. They observe THIS small object, not AppState: watching
    /// the whole model re-built the main menu (and re-sorted every row's releases for the Update All count) on every
    /// publish. Updated only when a value actually changes.
    let chrome = ChromeState()

    func syncChrome() {
        let lock = showLock, busy = batchRunning
        if chrome.showLock != lock { chrome.showLock = lock }
        // Counting updates walks every row's releases; bumpRows fires per install phase, so only count when it can matter.
        let canUpdate = !lock && !busy && updatesAvailable > 0
        if chrome.canUpdateAll != canUpdate { chrome.canUpdateAll = canUpdate }
        let slots = installedSlotsForLaunch.map { ChromeState.LaunchSlot(key: $0.key, name: $0.name) }
        if chrome.launchSlots != slots { chrome.launchSlots = slots }
    }
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

    // ── v1.30 ─────────────────────────────────────────────────────────────────────────────────────────
    /// Find & filter: the search text and the status filter (session-only).
    @Published var searchText = ""
    @Published var statusFilter: StatusFilter = .all
    /// Show lock: installs, updates and uninstalls are paused; Launch still works (per machine).
    @Published private(set) var showLock = UserDefaults.standard.bool(forKey: AppState.showLockKey)
    /// Apps held at their installed version: no Update, left out of Update All / automatic updates (per machine).
    @Published private(set) var heldIds: Set<String> = []
    /// A batch (Download All / Update All / an import / automatic updates) is running — the header offers Stop.
    @Published private(set) var batchRunning = false { didSet { syncChrome() } }
    /// This build's version on the first launch after the launcher itself was updated (the "Updated to" banner).
    @Published var launcherWhatsNew: String?
    /// The secondary sheet on screen (release notes, details, activity, import preview), or nil.
    @Published var activeSheet: LauncherSheet?
    /// Stop pressed during a batch: finish the slot that's installing, start nothing more.
    /// Stop was pressed: the header shows "Stopping…" until the app that's installing finishes.
    @Published private(set) var batchStopRequested = false
    /** A check was asked for while one was running (e.g. a new passphrase saved mid-check): run one more after it. */
    private var refreshRequested = false
    /** The rows of the running batch — what Stop cancels (never a separate install started from a row). */
    private var batchIds: Set<String> = []
    /// In-flight downloads by row id — cancelled by the row's Cancel button or a batch Stop.
    private var downloadTasks: [String: Task<Void, Error>] = [:]
    /// Install slots (app + edition) being downloaded or installed right now: a second request for the same slot
    /// (a row's Update while a batch has it, a double click) is refused instead of racing it for the same zip.
    private var slotsInFlight: Set<String> = []
    /// Set when someone asks for a visible check while a quiet one runs (see `refreshAll`).
    private var nextRefreshVisible = false
    private(set) var isRefreshing = false
    /// When the last check ran (the "While open, check every…" scheduler's clock).
    private var lastCheck: Date?
    private var scheduler: Task<Void, Never>?
    nonisolated static let showLockKey = "theatre.showLock"
    nonisolated static let heldKey = "theatre.heldApps"
    nonisolated static let autoCheckKey = "theatre.autoCheckInterval"
    nonisolated static let notifyKey = "theatre.notifyUpdates"
    nonisolated static let autoInstallKey = "theatre.autoInstallUpdates"
    nonisolated static let notifiedKey = "theatre.notifiedUpdates"
    nonisolated static let lastSeenKey = "theatre.lastSeenLauncherVersion"
    nonisolated static let menuBarKey = "theatre.showMenuBarExtra"

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
            heldIds = Set(UserDefaults.standard.stringArray(forKey: Self.heldKey) ?? []).intersection(known)
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
        UserDefaults.standard.removeObject(forKey: Self.devChannelRevealedKey)   // retired: the reveal is never stored
        prepareLauncherWhatsNew()
        AppLog.shared.log("launched v\(currentVersion)")
        syncChrome()   // the menus' first state (show lock, Update All, launchable apps)
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

    /// `quiet`: a scheduled background check — rows keep what they show while it runs (no "Checking…", which hides
    /// every not-installed row and its Install button) and only change when a result lands.
    func refreshAll(quiet: Bool = false) async {
        // One check at a time: a scheduled check, the menu-bar item and the Refresh button can all ask. A request
        // made meanwhile runs once more afterwards — it may carry new credentials the running check doesn't have.
        // The asker waits for it (so Refresh keeps showing "Checking…" until results are in), and a visible request
        // made during a quiet scheduled check makes the re-run visible.
        if isRefreshing {
            refreshRequested = true
            if !quiet { nextRefreshVisible = true }
            while isRefreshing && !Task.isCancelled { try? await Task.sleep(nanoseconds: 100_000_000) }
            return
        }
        isRefreshing = true
        var visible = !quiet
        repeat {
            refreshRequested = false
            nextRefreshVisible = false
            await refreshOnce(quiet: !visible)
            visible = nextRefreshVisible
        } while refreshRequested
        isRefreshing = false
    }

    private func refreshOnce(quiet: Bool = false) async {
        await ensureKeychainExplained()
        guard let client = activeClient() else { return }
        // Clear a stale network/credential error from a previous run (but keep a catalog-load error,
        // which leaves `rows` empty).
        if !rows.isEmpty { globalError = nil }

        // Mark every row "checking" in a SINGLE publish — mutate a local copy and assign `rows` once — so we
        // don't re-render the whole list 20× just to show the spinners (that churn was the boot-time lag).
        for row in rows {
            if !quiet { row.status = .checking }
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
            for await (id, outcome) in group { applyRefreshOutcome(id: id, outcome: outcome, quiet: quiet) }
        }
        if let (notes, data) = await notesFetch {
            applyWhatsNew(notes)
            WhatsNewNotes.cache(data)
            AppLog.shared.log("what's-new: applied \(notes.notes.count) relay line(s)")
        }
        hasRefreshed = true
        lastCheck = Date()
        afterCheck()
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
    /// `quiet` (a scheduled check): a failed check leaves the row as it was — an offline show machine shouldn't turn
    /// every row into an error with a Retry button every few hours. The launch check and Refresh still show errors.
    private func applyRefreshOutcome(id: String, outcome: FetchOutcome, quiet: Bool) {
        guard let app = rows.first(where: { $0.id == id })?.app else { return }
        let slot = installKey(for: app)
        switch outcome {
        case .releases(let all):
            guard let latest = Self.latest(from: all, for: macAsset(for: app)) else {
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
            // The credential itself is bad — surface one clear message instead of N broken rows. The cached releases
            // go too, so nothing (e.g. automatic updates) keeps acting on data this credential can no longer reach.
            update(id) { $0.status = .noAccess; $0.releases = []; $0.latestRelease = nil; $0.latest = nil; $0.latestAssetId = nil }
            globalError = Self.authMode == .token
                ? "Your GitHub token is invalid or expired. Open Settings to paste a new one."
                : "The download server rejected the passphrase. Check it in Settings."
            AppLog.shared.log("refresh: credentials rejected (\(Self.authMode.rawValue) mode)")
        case .noRelease:
            update(id) { $0.latest = nil; $0.releases = []; $0.latestRelease = nil; $0.status = .noRelease }
        case .error(let msg):
            if !quiet { update(id) { $0.status = .error(msg) } }
            AppLog.shared.log("refresh \(id) error\(quiet ? " (scheduled check — row left as it was)" : ""): \(msg)")
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
        // A held app is left alone by Update All, automatic updates and notifications.
        guard !isHeld(app.id), let row = rows.first(where: { $0.id == app.id }), row.latestRelease != nil else { return [] }
        var variants: [String?] = [app.hasVariants ? app.variants?.first?.id : nil]
        if let vs = app.variants { variants += vs.dropFirst().map { $0.id } }
        return variants.filter { vid in
            guard let name = app.macAssetName(variantId: vid),
                  let latest = Self.latest(from: row.releases, for: name),   // per edition (dev builds may lack one)
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
        // Find & filter narrows what's shown here only; the drag / move helpers keep working on the full groups
        // (and are switched off while a filter is active — see `isFiltering`).
        let filtering = isFiltering
        let pinned = filtering ? pinnedDisplayRows.filter { matchesFilter($0) } : pinnedDisplayRows
        if !pinned.isEmpty { groups.append(DisplayGroup(key: Self.pinnedGroupKey, title: "Pinned", rows: pinned)) }
        let main = filtering ? mainDisplayRows.filter { matchesFilter($0) } : mainDisplayRows
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

    /// While filtering, every section shows open so a match can never hide behind a fold.
    func isCollapsed(_ key: String) -> Bool { collapsedGroups.contains(key) && !isFiltering }

    /// Toggles a section's collapsed state (call inside `withAnimation` for the fold to animate) and persists.
    func toggleCollapsed(_ key: String) {
        if collapsedGroups.contains(key) { collapsedGroups.remove(key) } else { collapsedGroups.insert(key) }
        UserDefaults.standard.set(Array(collapsedGroups), forKey: Self.collapsedKey)
        AppLog.shared.log("\(collapsedGroups.contains(key) ? "collapsed" : "expanded") section \(key)")
    }

    /// Every category in the effective section order, whatever is filtered or pinned (what a setup file saves).
    var fullCategoryOrder: [String] {
        var order: [String] = []
        for c in categoryOrder where !order.contains(c) { order.append(c) }
        for c in catalogCategories where !order.contains(c) { order.append(c) }
        for c in Set(rows.map { categoryOf($0) }).sorted() where !order.contains(c) { order.append(c) }
        return order
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
        guard rows[i].latestRelease != nil,
              let latest = Self.latest(from: rows[i].releases, for: macAsset(for: rows[i].app)) else {
            rows[i].status = rows[i].installed != nil ? .installed : .unknown
            return
        }
        // Re-pick for the newly selected edition (a dev build may carry only one of them).
        rows[i].latestRelease = latest
        rows[i].latest = latest.tagName
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
    func canMove(_ id: String, up: Bool) -> Bool { !isFiltering && groupNeighbour(of: id, up: up) != nil }

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

    /// One install slot of a batch: a row, the edition (its REAL variant id for variant apps — nil would mean
    /// "whatever the row shows"), and a specific tag (nil = the latest).
    typealias Slot = (id: String, variant: String?, tag: String?)

    /// Updates every app that currently has an update available, one at a time.
    func updateAll() async {
        guard !blockedByLock("update all") else { return }
        let work = updateWork()
        AppLog.shared.log("update all: \(work.count) slot(s)")
        await runSlots(work, label: "update all")
    }

    /// Every installed, non-held slot with an update, default editions first.
    private func updateWork() -> [Slot] {
        orderedSlots(rows.flatMap { row in slotsToUpdate(row.app).map { Slot(id: row.id, variant: $0, tag: nil) } })
    }

    /// Default editions first, then Full editions — so consecutive slots are (almost always) different apps and
    /// the pipelined runner overlaps two apps rather than two slots of one row.
    private func orderedSlots(_ slots: [Slot]) -> [Slot] {
        func isDefault(_ s: Slot) -> Bool {
            rows.first { $0.id == s.id }.map { $0.app.isDefaultVariant(s.variant) } ?? true
        }
        return slots.filter { isDefault($0) } + slots.filter { !isDefault($0) }
    }

    /// The installed version of a slot, if any.
    private func installedVersion(_ slot: Slot) -> String? {
        guard let app = rows.first(where: { $0.id == slot.id })?.app else { return nil }
        return InstallManager.shared.installedVersion(app.installKey(variantId: slot.variant))
    }

    /// The download size of a batch's latest assets (the Download All / Update All menu labels).
    private func workBytes(_ work: [Slot]) -> Int64 {
        ByteSize.sum(work.compactMap { slot -> Int64? in
            guard let row = rows.first(where: { $0.id == slot.id }),
                  let name = row.app.macAssetName(variantId: slot.variant),
                  let rel = Self.latest(from: row.releases, for: name),
                  let asset = rel.assets.first(where: { $0.name == name }) else { return nil }
            return Int64(asset.size)
        })
    }

    /// Download sizes for the header menu: pending updates, Install every app, and with the Full editions.
    /// The header's "(120 MB)" totals. Computed once per list change (`rowsGen`), not per header render: each is a
    /// release pick across every row and edition, and the header re-renders on every publish.
    var updateAllBytes: Int64 { batchBytes().update }
    func downloadAllBytes(includeFull: Bool) -> Int64 { includeFull ? batchBytes().allFull : batchBytes().all }

    private var bytesCache: (gen: Int, update: Int64, all: Int64, allFull: Int64)?
    private func batchBytes() -> (update: Int64, all: Int64, allFull: Int64) {
        if let c = bytesCache, c.gen == rowsGen { return (c.update, c.all, c.allFull) }
        func dl(_ full: Bool) -> Int64 {
            workBytes(orderedSlots(rows.flatMap { row in
                slotsToDownload(row.app, includeFull: full).map { Slot(id: row.id, variant: $0, tag: nil) }
            }))
        }
        let v = (update: workBytes(updateWork()), all: dl(false), allFull: dl(true))
        bytesCache = (rowsGen, v.update, v.all, v.allFull)
        return v
    }

    /// Runs install slots with ONE download of lookahead: while slot N verifies + extracts (CPU/disk), slot
    /// N+1 is already downloading (network). Serially, the network sat idle through every hash + extract and
    /// the disk idle through every download — minutes per Download All on a slow machine. Downloads
    /// themselves stay serial (venue Wi-Fi is the bottleneck; two at once would just split it) and at most two
    /// downloaded zips exist at any moment (the one being installed + the one arriving).
    /// Stop (`stopBatch`) cancels the download in flight and starts nothing more; `unattended` (automatic
    /// updates) never asks to quit an open app. Returns what was installed.
    @discardableResult
    private func runSlots(_ work: [Slot], label: String, unattended: Bool = false) async -> [(name: String, version: String)] {
        var done: [(name: String, version: String)] = []
        guard !batchRunning else {
            AppLog.shared.log("\(label): another batch is running — not started")
            return done
        }
        batchRunning = true
        batchStopRequested = false
        batchIds = Set(work.map(\.id))
        // The list is fixed when the batch starts, but the person can keep working: each slot is re-checked just
        // before its turn (and before its look-ahead download starts) so the batch never undoes or repeats that.
        let installedAtStart = work.map { installedVersion($0) }
        func skipReason(_ i: Int) -> String? {
            let slot = work[i]
            // Held after the batch started: an update (latest, already installed) leaves it where it is.
            if heldNow(slot) { return "is held" }
            let now = installedVersion(slot)
            if slot.tag == nil, installedAtStart[i] != nil, now == nil { return "was removed meanwhile" }
            if let now, let app = rows.first(where: { $0.id == slot.id })?.app {
                let target = slot.tag ?? rows.first(where: { $0.id == slot.id })
                    .flatMap { Self.latest(from: $0.releases, for: app.macAssetName(variantId: slot.variant)) }?.tagName
                if let target, VersionDisplay.equal(now, target) { return "is already at \(VersionDisplay.display(target))" }
            }
            return nil
        }
        var next: Task<Downloaded?, Never>? = nil
        for (i, slot) in work.enumerated() {
            if batchStopRequested { break }
            if let reason = skipReason(i) {
                if next != nil { downloadTasks[slot.id]?.cancel() }   // its look-ahead download: no point finishing it
                if let n = next, let d = await n.value { discard(d) }
                next = nil
                AppLog.shared.log("\(label): \(slot.id) \(reason) — skipped")
                continue
            }
            let before = installedVersion(slot)
            let current = next ?? Task { await self.downloadPhase(slot.id, tag: slot.tag, variantOverride: slot.variant, unattended: unattended) }
            let d = await current.value                       // download N done (or failed + recorded)
            next = nil
            if i + 1 < work.count, !batchStopRequested, skipReason(i + 1) == nil {
                let n = work[i + 1]                           // start download N+1 now…
                next = Task { await self.downloadPhase(n.id, tag: n.tag, variantOverride: n.variant, unattended: unattended) }
            }
            if let d {
                if let reason = skipReason(i) {
                    discard(d)
                    AppLog.shared.log("\(label): \(slot.id) \(reason) — skipped")
                } else { await installPhase(d, id: slot.id) }   // …and verify + extract N meanwhile
            }
            if let after = installedVersion(slot), before.map({ !VersionDisplay.equal($0, after) }) ?? true,
               let app = rows.first(where: { $0.id == slot.id })?.app {
                done.append((name: app.name + app.variantSuffix(slot.variant), version: after))
            }
        }
        // A stopped batch may leave the look-ahead download running or finished: wait for it and drop it.
        if let next, let d = await next.value { discard(d) }
        let stopped = batchStopRequested
        batchIds = []
        batchRunning = false
        batchStopRequested = false
        AppLog.shared.log("\(label) \(stopped ? "stopped" : "complete") (\(done.count) installed)")
        bumpRows()
        return done
    }

    /// Stop a running batch: the batch's download in flight is cancelled and nothing more starts (a slot that has
    /// already downloaded still finishes installing). A separate install started from a row carries on.
    func stopBatch() {
        guard batchRunning else { return }
        batchStopRequested = true
        for (id, task) in downloadTasks where batchIds.contains(id) { task.cancel() }
        AppLog.shared.log("batch: stop requested")
    }

    /// A latest-version slot of an app that's held and already installed (a hold set after the batch started).
    private func heldNow(_ slot: Slot) -> Bool {
        slot.tag == nil && isHeld(slot.id) && installedVersion(slot) != nil
    }

    /// Drops a downloaded slot that won't be installed.
    private func discard(_ d: Downloaded) {
        Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: d.zip) }
        slotsInFlight.remove(d.app.installKey(variantId: d.variantId))
        endBusy(d.app.id)
    }

    /// Cancels a row's in-flight download (its Cancel button). Not an error: the row goes back to how it was.
    func cancelDownload(_ id: String) {
        guard let task = downloadTasks[id] else { return }
        AppLog.shared.log("install \(id): cancel requested")
        task.cancel()
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
        guard let row = rows.first(where: { $0.id == app.id }), row.latestRelease != nil else { return [] }
        var variants: [String?] = [app.hasVariants ? app.variants?.first?.id : nil]
        if includeFull, let vs = app.variants { variants += vs.dropFirst().map { $0.id } }
        return variants.filter { vid in
            guard let name = app.macAssetName(variantId: vid),
                  let latest = Self.latest(from: row.releases, for: name),   // per edition (dev builds may lack one)
                  latest.assets.contains(where: { $0.name == name }) else { return false }   // no asset for this arch → skip
            let installed = InstallManager.shared.installedVersion(app.installKey(variantId: vid))
            // A held app is only ever fetched when that edition isn't installed at all.
            guard let installed else { return true }
            return !isHeld(app.id) && Self.versionIsNewer(latest.tagName, than: installed)
        }
    }

    /// Download All: install/update every app. `includeFull` also fetches Full editions (their own slots,
    /// so Light and Full end up installed side by side). Skips anything already current or with no
    /// asset for this OS/arch.
    func downloadAll(includeFull: Bool) async {
        guard !blockedByLock("download all") else { return }
        let work = orderedSlots(rows.flatMap { row in
            slotsToDownload(row.app, includeFull: includeFull).map { Slot(id: row.id, variant: $0, tag: nil) }
        })
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
    ///
    /// Development pre-releases (`vX.Y.Z-dev.N`, built on the maintainer's Mac) are candidates ONLY when this Mac has the
    /// Dev channel on — then the highest semver among stable + dev wins (a stable X.Y.Z supersedes its own dev
    /// builds). Off, they're invisible, even for an app whose only releases are dev builds.
    nonisolated static func latest(from releases: [ReleaseInfo], devChannel: Bool = AppState.devChannel) -> ReleaseInfo? {
        let nonDev = releases.filter { !isDevTag($0.tagName) }
        let stable = nonDev.filter { !$0.prerelease }
        var pool = devChannel ? stable + releases.filter { isDevTag($0.tagName) } : stable
        if pool.isEmpty { pool = nonDev }
        return pool.max { versionIsNewer($1.tagName, than: $0.tagName) }
    }

    /// The release for ONE edition (asset). A development build may not carry every edition (PDF Tools' dev
    /// builds ship Light only), so when the dev pick lacks this asset, take the newest release that has it —
    /// e.g. Light gets v0.9.2-dev.1 while Full stays on v0.9.1 instead of reading "No macOS build".
    nonisolated static func latest(from releases: [ReleaseInfo], for assetName: String?,
                                   devChannel: Bool = AppState.devChannel) -> ReleaseInfo? {
        let pick = latest(from: releases, devChannel: devChannel)
        guard let pick, let name = assetName, isDevTag(pick.tagName),
              !pick.assets.contains(where: { $0.name == name }) else { return pick }
        return latest(from: releases.filter { $0.assets.contains { $0.name == name } }, devChannel: devChannel) ?? pick
    }

    /// This Mac's Dev channel (Settings → click the version 7× to reveal). Read straight from UserDefaults so
    /// the nonisolated release pick and the CLI (always off unless set) see the same value.
    nonisolated static var devChannel: Bool { UserDefaults.standard.bool(forKey: devChannelKey) }
    nonisolated static let devChannelKey = "theatre.devChannel"
    nonisolated static let devChannelRevealedKey = "theatre.devChannelRevealed"

    /// The stable tag to go back to when this Mac's Dev channel is off but the row's slot still has a dev build
    /// installed and no release is newer (so nothing would replace it until the next release); else nil.
    func backToReleaseTag(_ row: Row) -> String? {
        guard !Self.devChannel, let installed = row.installed, Self.isDevTag(installed),
              let stable = Self.latest(from: row.releases, devChannel: false), !Self.isDevTag(stable.tagName),
              !Self.versionIsNewer(stable.tagName, than: installed),
              stable.assets.contains(where: { $0.name == macAsset(for: row.app) }) else { return nil }
        return stable.tagName
    }

    /// True for development pre-releases (`vX.Y.Z-dev.N`).
    nonisolated static func isDevTag(_ tag: String) -> Bool { norm(tag).lowercased().contains("-dev.") }

    nonisolated private static func norm(_ s: String) -> String {
        var t = s.trimmingCharacters(in: .whitespaces)
        if t.hasPrefix("v") || t.hasPrefix("V") { t.removeFirst() }
        return t
    }

    /// True if `a` is a strictly newer version string than `b`. Semver-aware: the numeric core compares
    /// component-wise; a pre-release (`1.2.0-dev.3`, `1.2.0-rc1`) sorts BEFORE its release, with identifiers
    /// compared semver-style — so `0.1.0-dev.1 < 0.1.0-dev.2 < 0.1.0`. A tag that doesn't start with a digit
    /// (Convert's `build-20260912`) keeps the lenient digit-run parse below and is never a pre-release.
    nonisolated static func versionIsNewer(_ a: String, than b: String) -> Bool {
        func split(_ s: String) -> (String, [Substring]?) {
            let n = norm(s)
            guard let first = n.first, first.isNumber, let dash = n.firstIndex(of: "-") else { return (n, nil) }
            return (String(n[..<dash]), n[n.index(after: dash)...].split(separator: ".", omittingEmptySubsequences: false))
        }
        let (ca, pa) = split(a), (cb, pb) = split(b)
        if versionCoreIsNewer(ca, than: cb) { return true }
        if versionCoreIsNewer(cb, than: ca) { return false }
        switch (pa, pb) {
        case (nil, nil): return false
        case (nil, _): return true          // 1.2.0 > 1.2.0-dev.3
        case (_, nil): return false
        case let (x?, y?):
            for (p, q) in zip(x, y) where p != q {
                switch (Int(p), Int(q)) {
                case let (m?, n?): return m > n
                case (_?, nil): return false    // numeric identifiers sort before words
                case (nil, _?): return true
                default: return p > q
                }
            }
            return x.count > y.count
        }
    }

    /// The numeric core compare (component-wise, missing parts = 0; first digit run per segment).
    nonisolated private static func versionCoreIsNewer(_ a: String, than b: String) -> Bool {
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
        /// An automatic update: never asks to quit an open app (it's left for the next check).
        var unattended = false
        /// Only a version hand-picked from the ⋯ version list may install without a signed checksum manifest
        /// (very old releases predate it). Everything else — latest, Roll Back, Back to release, a setup file's
        /// versions — must verify against the signed manifest.
        var lenient = false
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
    func install(_ id: String, tag: String? = nil, variantOverride: String? = nil, lenient: Bool = false) async {
        guard var d = await downloadPhase(id, tag: tag, variantOverride: variantOverride, lenient: lenient && tag != nil) else { return }
        d.lenient = lenient && tag != nil
        await installPhase(d, id: id)
    }

    /// Phase 1 — resolve the release + asset and download it into the cache. Marks the row busy for the whole
    /// slot; on failure it records the error, ends busy and returns nil. Nothing here touches the disk beyond
    /// the download itself.
    private func downloadPhase(_ id: String, tag: String?, variantOverride: String?, unattended: Bool = false,
                               lenient: Bool = false) async -> Downloaded? {
        guard let app = rows.first(where: { $0.id == id })?.app else { return nil }
        guard !blockedByLock("install \(id)") else { return nil }
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
            let picked = Self.latest(from: releases, for: macAsset(for: app))
            update(id) { $0.releases = releases; $0.latestRelease = picked }
        }

        let variantId = variantOverride ?? selectedVariantId(app)
        // Per edition: a dev build may carry only some editions (PDF Tools' dev builds are Light only).
        // A named version matches "1.2.0" and "v1.2.0" alike (setup files from another launcher may omit the "v").
        let release = tag != nil ? releases.first { VersionDisplay.equal($0.tagName, tag!) }
                                 : Self.latest(from: releases, for: app.macAssetName(variantId: variantId))
        guard let rel = release else {
            update(id) { $0.status = .error("Version \(tag ?? "latest") not found.") }; endBusy(id); return nil
        }
        guard let asset = rel.assets.first(where: { $0.name == app.macAssetName(variantId: variantId) }) else {
            update(id) { $0.status = .error("No macOS asset in \(rel.tagName).") }; endBusy(id); return nil
        }
        let slotName = app.name + app.variantSuffix(variantId)
        let slotKey = app.installKey(variantId: variantId)
        guard slotsInFlight.insert(slotKey).inserted else {
            AppLog.shared.log("install \(slotKey): already downloading or installing — not started again")
            endBusy(id)
            return nil
        }
        var handedOn = false   // true once the Downloaded goes to installPhase / discard, which free the slot
        defer { if !handedOn { slotsInFlight.remove(slotKey) } }
        // A strict install (everything but the ⋯ version list) needs signed checksums: say so now rather than after
        // downloading 300–450 MB that could never pass.
        if !lenient, !Self.hasSignedManifest(rel) {
            let reason = Self.strictFailureReason(
                rel.assets.contains { $0.name == "SHA256SUMS" } ? .unsigned : .noManifest, assetName: asset.name)
            update(id) { $0.status = .error("\(VersionDisplay.display(rel.tagName)) can’t be verified: \(reason).") }
            AppLog.shared.log("install \(app.id) \(rel.tagName): BLOCKED (strict, before download) — \(reason)")
            HistoryStore.add(app: slotKey, name: slotName, action: "failed", to: rel.tagName, note: reason)
            endBusy(id)
            return nil
        }

        // Refuse up front rather than failing half-way through an extract on a full disk.
        let needed = DiskSpace.required(assetSize: Int64(asset.size), assetName: asset.name)
        // The free-space query is a CacheDelete XPC round-trip (~100 ms): never on the main actor.
        let free = await Task.detached(priority: .userInitiated) { InstallManager.shared.freeSpace() }.value
        if let message = DiskSpace.shortfall(required: needed, free: free) {
            update(id) { $0.status = .error(message) }
            AppLog.shared.log("install \(app.id) \(rel.tagName): \(message)")
            HistoryStore.add(app: slotKey, name: slotName, action: "failed", to: rel.tagName, note: message)
            endBusy(id)
            return nil
        }

        // Named per install SLOT (not per app): a batch downloads Light and Full of one app back to back, and the
        // look-ahead download must never overwrite the zip the previous slot is still verifying / installing.
        let zipDest = InstallManager.shared.cacheDir.appendingPathComponent(
            "\(PathSafe.component(slotKey))-\(PathSafe.component(rel.tagName)).zip")
        let appId = id
        // Stop or show lock may have come while this slot was still resolving (before its transfer was registered
        // below, where Stop / the lock can reach it): don't start a 300–450 MB download that nobody wants.
        if showLock || (batchStopRequested && batchIds.contains(id)) {
            AppLog.shared.log("install \(app.id): not started — \(showLock ? "show lock is on" : "stopped")")
            endBusy(id)
            return nil
        }
        // The transfer runs in its own Task so the row's Cancel (or a batch Stop) can cancel exactly it.
        let download = Task {
            try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: asset.id, to: zipDest) { p in
                // Progress goes to the hub, NOT `rows`: only the row's progress bar re-renders (≥1% steps).
                Task { @MainActor [weak self] in self?.progressHub.set(appId, p) }
            }
        }
        downloadTasks[id] = download
        update(id) { $0.cancellable = true }
        do {
            try await download.value
            finishDownload(id, download)
            var d = Downloaded(app: app, rel: rel, asset: asset, zip: zipDest, variantId: variantId, tag: tag)
            d.unattended = unattended
            handedOn = true
            return d
        } catch {
            finishDownload(id, download)
            if download.isCancelled {
                // Cancelled (the row's Cancel, or Stop): not a failure — the row simply goes back to how it was.
                try? FileManager.default.removeItem(at: zipDest)
                AppLog.shared.log("install \(app.id): download cancelled")
                endBusy(id)
                return nil
            }
            update(id) { $0.status = .error(error.localizedDescription) }
            AppLog.shared.log("install \(app.id) FAILED: \(error.localizedDescription)")
            HistoryStore.add(app: slotKey, name: slotName, action: "failed", to: rel.tagName, note: error.localizedDescription)
            endBusy(id)
            return nil
        }
    }

    /// Forgets a finished download task (unless a newer one for the same row has replaced it).
    private func finishDownload(_ id: String, _ task: Task<Void, Error>) {
        if downloadTasks[id] == task { downloadTasks[id] = nil }
        let stillDownloading = downloadTasks[id] != nil
        update(id) { $0.cancellable = stillDownloading }
    }

    /// Phase 2 — verify (size + signed SHA256SUMS + hash), then extract + install OFF the main actor, then
    /// reflect it in the row. Always ends the row's busy state.
    private func installPhase(_ d: Downloaded, id: String) async {
        let (app, rel, asset, zipDest, variantId) = (d.app, d.rel, d.asset, d.zip, d.variantId)
        update(id) { $0.installingCount += 1; $0.installing = true }
        defer {
            update(id) { $0.installingCount = max(0, $0.installingCount - 1); $0.installing = $0.installingCount > 0 }
            endBusy(id); slotsInFlight.remove(app.installKey(variantId: variantId))
        }
        // Show lock turned on while this was downloading: drop it, install nothing.
        if blockedByLock("install \(app.id) \(rel.tagName)") {
            Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: zipDest) }
            return
        }
        guard let client = activeClient() else { return }
        do {
            let verification = try await Self.verifyDownload(zipDest, asset: asset, release: rel, app: app, client: client)
            // Strict unless the user hand-picked an older version from the ⋯ list (`lenient`, which may predate
            // the signed manifest): latest, Roll Back, Back to release and setup-file installs MUST verify against
            // the signed SHA256SUMS — a missing / unsigned / incomplete manifest aborts. A hash MISMATCH always
            // aborts (it throws from verifyDownload); size is always checked too.
            if !d.lenient, verification != .verified {
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
            let fromVersion = InstallManager.shared.installedVersion(slotKey)   // for the history line
            // The app is open: ASK (sheet) instead of failing with a log-only "Quit X first". Quit → wait for it
            // to exit, then install; Not Now → skip quietly (the row keeps its Update button).
            // Show lock switched on while this downloaded or verified: install nothing, and never raise the
            // "quit it?" prompt below (it brings the launcher to the front, over the show).
            if showLock {
                Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: zipDest) }
                AppLog.shared.log("install \(app.id) \(rel.tagName): not installed — show lock is on")
                return
            }
            if d.unattended, InstallManager.shared.runningInstance(slotKey) != nil {
                // An automatic update never interrupts an open app: leave it for the next check.
                Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: zipDest) }
                AppLog.shared.log("install \(app.id) \(rel.tagName): left for later — it's open (automatic update)")
                return
            }
            if let running = InstallManager.shared.runningInstance(slotKey) {
                let name = app.name + app.variantSuffix(variantId)
                switch await Self.askToQuit(name, running, stillWanted: { [weak self] in self?.showLock == false }) {
                case .quit: break
                case .declined:
                    Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: zipDest) }
                    AppLog.shared.log("install \(app.id) \(rel.tagName): postponed — \(name) is open")
                    return
                case .stillRunning:
                    Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: zipDest) }
                    throw InstallError.appRunning(name)
                }
            }
            // Show lock switched on while the "quit it first?" prompt was up: install nothing.
            if showLock {
                Task.detached(priority: .utility) { try? FileManager.default.removeItem(at: zipDest) }
                AppLog.shared.log("install \(app.id) \(rel.tagName): not installed — show lock is on")
                return
            }
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
            HistoryStore.add(app: slotKey, name: app.name + app.variantSuffix(variantId),
                             action: ActivityHistory.action(from: fromVersion, to: rel.tagName), from: fromVersion, to: rel.tagName)
            bumpRows()   // header counts (Update All (N) / Download All) re-derive once per slot
            LoopWatch.mark("install-main end \(app.id)")
        } catch {
            update(id) { $0.status = .error(error.localizedDescription) }
            AppLog.shared.log("install \(app.id) FAILED: \(error.localizedDescription)")
            HistoryStore.add(app: app.installKey(variantId: variantId), name: app.name + app.variantSuffix(variantId),
                             action: "failed", to: rel.tagName, note: error.localizedDescription)
        }
    }

    /// UI entry point: the removal (a one-dir Full edition is thousands of files, 300–450 MB — seconds of unlink)
    /// runs off the main actor with the row busy, then the row is updated. `uninstall(_:)` below stays
    /// synchronous for the CLI.
    func uninstallAsync(_ id: String) async {
        guard let row = rows.first(where: { $0.id == id }), !blockedByLock("uninstall \(id)") else { return }
        let key = installKey(for: row.app)
        let fromVersion = InstallManager.shared.installedVersion(key)
        let name = row.displayName
        // Registered like an install: a batch can't start downloading this slot, and Launch waits while it's removed.
        guard slotsInFlight.insert(key).inserted else {
            AppLog.shared.log("uninstall \(key): an install of it is in progress — not started")
            return
        }
        beginBusy(id)
        update(id) { $0.installingCount += 1; $0.installing = true }
        let failure: String? = await Task.detached(priority: .userInitiated) {
            do { try InstallManager.shared.uninstall(key); return nil } catch { return error.localizedDescription }
        }.value
        update(id) { $0.installingCount = max(0, $0.installingCount - 1); $0.installing = $0.installingCount > 0 }
        slotsInFlight.remove(key)
        endBusy(id)
        if failure == nil, let fromVersion { HistoryStore.add(app: key, name: name, action: "uninstall", from: fromVersion) }
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

    enum QuitAnswer { case quit, declined, stillRunning }

    /// "Repo Radar is open — quit it and update?" as a sheet on the launcher window. On Quit, asks the app
    /// to quit normally (it may show its own save prompt) and waits up to 15 s for it to exit.
    /// `stillWanted` is asked again after the answer: if show lock came on while the prompt was up, nothing is quit.
    static func askToQuit(_ name: String, _ running: NSRunningApplication,
                          stillWanted: @MainActor () -> Bool) async -> QuitAnswer {
        let alert = NSAlert()
        alert.messageText = "\(name) is open"
        alert.informativeText = "Quit \(name) to install the update? If it has unsaved work it will ask you first."
        alert.addButton(withTitle: "Quit and Update")
        alert.addButton(withTitle: "Not Now")
        let answer: NSApplication.ModalResponse
        if let window = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeKey }) {
            NSApp.activate(ignoringOtherApps: true)
            answer = await withCheckedContinuation { cont in
                alert.beginSheetModal(for: window) { cont.resume(returning: $0) }
            }
        } else {
            answer = alert.runModal()
        }
        guard answer == .alertFirstButtonReturn, stillWanted() else { return .declined }
        running.terminate()
        for _ in 0..<75 where !running.isTerminated {
            try? await Task.sleep(nanoseconds: 200_000_000)
        }
        return running.isTerminated ? .quit : .stillRunning
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
        guard let i = rows.firstIndex(where: { $0.id == id }), !rows[i].installing else { return }
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
        // Moving every installed app breaks paths show-control cues, Dock pins and aliases point at: never under lock.
        guard !blockedByLock("move installed apps") else {
            inform("Show Lock Is On", "Installed apps weren't moved. Turn off show lock to move them.")
            return
        }
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

    // MARK: - Find & filter

    /// The list is narrowed by the search field or the status filter: reordering is off and sections show open.
    var isFiltering: Bool { AppFilter.isActive(searchText, statusFilter) }

    func matchesFilter(_ row: Row) -> Bool {
        AppFilter.matchesQuery(searchText, row.displayName, row.app.blurb, row.app.category, row.app.id)
            && AppFilter.matchesStatus(statusFilter, installed: row.installed != nil,
                                       updateAvailable: row.settledStatus == .updateAvailable && !isHeld(row.id),
                                       installable: row.settledStatus == .notInstalled)
    }

    /// "12 apps" / "3 of 12 apps" for the filter bar.
    var filterCountText: String {
        let total = pinnedDisplayRows.count + mainDisplayRows.count
        let noun = total == 1 ? "app" : "apps"
        guard isFiltering else { return "\(total) \(noun)" }
        return "\(displayGroups.reduce(0) { $0 + $1.rows.count }) of \(total) \(noun)"
    }

    func clearFilter() {
        searchText = ""
        statusFilter = .all
    }

    // MARK: - Hold (keep an app at its installed version)

    func isHeld(_ id: String) -> Bool { heldIds.contains(id) }

    func toggleHold(_ id: String) {
        if heldIds.contains(id) { heldIds.remove(id) } else { heldIds.insert(id) }
        UserDefaults.standard.set(Array(heldIds), forKey: Self.heldKey)
        bumpRows()   // header counts (Update All) re-derive
        AppLog.shared.log("\(isHeld(id) ? "held" : "released hold on") \(id)")
    }

    // MARK: - Show lock (installs / updates / uninstalls paused; Launch still works)

    /// From the UI (⌘L, the banner, the More menu, Settings): turning the lock ON is one step; turning it OFF asks,
    /// so a stray keystroke or click mid-show can't re-enable installs and automatic updates.
    func requestShowLock(_ on: Bool) {
        guard on != showLock else { return }
        if on { setShowLock(true); return }
        let alert = NSAlert()
        alert.messageText = "Turn Off Show Lock?"
        alert.informativeText = "Installs, updates and uninstalls can run again, including automatic updates if they're switched on."
        // Keep On is the default (Return): turning the lock off mid-show takes a deliberate click.
        alert.addButton(withTitle: "Keep On")
        let off = alert.addButton(withTitle: "Turn Off")
        off.keyEquivalent = ""
        alert.buttons[0].keyEquivalent = "\r"
        if alert.runModal() == .alertSecondButtonReturn { setShowLock(false) }
        else { objectWillChange.send() }   // a Settings checkbox switched off by the click shows "on" again
    }

    func setShowLock(_ on: Bool) {
        guard showLock != on else { return }
        showLock = on
        syncChrome()
        UserDefaults.standard.set(on, forKey: Self.showLockKey)
        AppLog.shared.log("show lock \(on ? "on" : "off")")
        guard on else { return }
        // Nothing more installs once the lock is on: stop a batch, and cancel every download in flight (a slot that
        // has already downloaded is dropped before it installs — see `installPhase`).
        if batchRunning { stopBatch() }
        for task in downloadTasks.values { task.cancel() }
    }

    /// The model-level guard behind every install / update / removal path (the UI hides them too).
    private func blockedByLock(_ what: String) -> Bool {
        guard showLock else { return false }
        AppLog.shared.log("\(what): blocked by show lock")
        return true
    }

    // MARK: - After a check: notifications, automatic updates, the scheduler

    /// Opt-in (default OFF): switching it on in Settings is what asks macOS for permission — never a surprise prompt.
    nonisolated static var notifyUpdates: Bool { UserDefaults.standard.object(forKey: notifyKey) as? Bool ?? false }

    /// Apps with an update on offer (installed, not held), each at the newest version any of its installed editions
    /// would move to.
    private func pendingUpdates() -> [UpdatePolicy.Pending] {
        rows.compactMap { row in
            var best: String?
            for vid in slotsToUpdate(row.app) {
                guard let name = row.app.macAssetName(variantId: vid),
                      let tag = Self.latest(from: row.releases, for: name)?.tagName else { continue }
                if best.map({ Self.versionIsNewer(tag, than: $0) }) ?? true { best = tag }
            }
            return best.map { UpdatePolicy.Pending(id: row.id, name: row.app.name, version: $0) }
        }
    }

    /// After every completed check: announce new updates (once each, and only when the launcher isn't the active
    /// app), then update automatically when that's switched on.
    private func afterCheck() {
        let already = UserDefaults.standard.stringArray(forKey: Self.notifiedKey) ?? []
        let (toNotify, pendingKeys) = UpdatePolicy.notify(pendingUpdates(), alreadyNotified: already)
        // An app whose check failed this time keeps what it was announced at.
        let unchecked = Set(rows.filter { row in
            if case .error = row.status { return true }
            return row.status == .noAccess
        }.map(\.id))
        let notified = UpdatePolicy.remembered(pendingKeys, alreadyNotified: already, uncheckedIds: unchecked)
        if notified != already { UserDefaults.standard.set(notified, forKey: Self.notifiedKey) }
        if !toNotify.isEmpty, Self.notifyUpdates, !showLock, !NSApp.isActive {
            Notifier.post(title: UpdatePolicy.notificationTitle(toNotify.count), body: UpdatePolicy.notificationBody(toNotify))
        }
        Task { await self.autoUpdateIfEnabled() }
    }

    /// Automatic updates (opt-in): every non-held slot with an update whose app isn't open. Never under show lock or
    /// alongside other work, never asks anything, and reports once when done.
    func autoUpdateIfEnabled() async {
        guard UserDefaults.standard.bool(forKey: Self.autoInstallKey), !showLock, !batchRunning,
              !rows.contains(where: { $0.busy }), hasCredentials else { return }
        let work = updateWork().filter { slot in
            guard let app = rows.first(where: { $0.id == slot.id })?.app else { return false }
            let open = InstallManager.shared.runningInstance(app.installKey(variantId: slot.variant)) != nil
            if open { AppLog.shared.log("automatic update: \(app.id) is open — left for later") }
            return !open
        }
        guard !work.isEmpty else { return }
        AppLog.shared.log("automatic update: \(work.count) slot(s)")
        let done = await runSlots(work, label: "automatic update", unattended: true)
        if !done.isEmpty, Self.notifyUpdates, !NSApp.isActive {
            Notifier.post(title: "JB Theatre Tools", body: UpdatePolicy.autoUpdateSummary(done))
        }
    }

    /// Starts the once-a-minute scheduler behind "While open, check every…" (idempotent).
    func startScheduler() {
        guard scheduler == nil else { return }
        scheduler = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 60_000_000_000)
                await self?.scheduledTick()
            }
        }
    }

    /// Runs a check when one is due — only in "Every launch" mode, and never on top of other work.
    /// The active credential has already been read this session (so a check can't trigger a Keychain prompt).
    private var credentialsInMemory: Bool {
        switch Self.authMode {
        case .token:  return TokenStore.cachedToken != nil
        case .server: return ServerAuthStore.cachedPassphrase != nil
        }
    }

    private func scheduledTick() async {
        let mode = UserDefaults.standard.string(forKey: "theatre.updateMode") ?? UpdateCheckMode.everyLaunch.rawValue
        guard mode == UpdateCheckMode.everyLaunch.rawValue, !isRefreshing, !batchRunning, activeSheet == nil,
              !showLock,                          // show lock: nothing happens by itself during a show
              !showKeychainExplainer, credentialsInMemory,   // never raise the Keychain explainer unattended
              !rows.contains(where: { $0.busy }), hasCredentials,
              UpdatePolicy.isDue(lastCheck: lastCheck, now: Date(), raw: UserDefaults.standard.string(forKey: Self.autoCheckKey))
        else { return }
        lastCheck = Date()   // an attempt counts, so a failing check isn't retried every minute
        AppLog.shared.log("scheduled update check")
        await refreshAll(quiet: true)
        await checkLauncherUpdate()
    }

    // MARK: - Roll back

    /// The version this row's slot had before its last update, when that release still has this edition's build —
    /// the one-click roll back target; nil when there's nothing to roll back to.
    func rollbackTag(_ row: Row) -> String? {
        let key = installKey(for: row.app)
        guard let installed = InstallManager.shared.installedVersion(key),
              let prev = InstallManager.shared.record(key)?.previousVersion,
              Self.versionIsNewer(installed, than: prev),   // only ever BACK (after a roll back, "previous" is newer)
              let asset = macAsset(for: row.app) else { return nil }
        // Roll back installs strictly, so only offer a release that can pass: signed checksums, and never a
        // development build on a Mac without Development builds switched on.
        return offeredReleases(row).first {
            VersionDisplay.equal($0.tagName, prev) && $0.assets.contains { $0.name == asset } && Self.hasSignedManifest($0)
        }?.tagName
    }

    /// Roll back to the previous version, then hold the app there so Update All leaves it alone.
    func rollBack(_ id: String, to tag: String) async {
        guard !blockedByLock("roll back \(id)"), let row = rows.first(where: { $0.id == id }) else { return }
        await install(id, tag: tag)
        if let now = InstallManager.shared.installedVersion(installKey(for: row.app)), VersionDisplay.equal(now, tag) {
            if !isHeld(id) { toggleHold(id) }
            AppLog.shared.log("rolled back \(id) to \(tag) (held)")
        }
    }

    /// The row menu's "Roll Back to vX…": confirms first (it's a downgrade, and the app is held afterwards).
    func requestRollBack(_ id: String, to tag: String) {
        guard !blockedByLock("roll back \(id)"), let row = rows.first(where: { $0.id == id }),
              let current = row.installed else { return }
        let alert = NSAlert()
        alert.messageText = "Roll \(row.displayName) back to \(VersionDisplay.display(tag))?"
        alert.informativeText = "It goes back from \(VersionDisplay.display(current)) to \(VersionDisplay.display(tag)), then stays "
            + "held at that version so Update All and automatic updates leave it alone — release the hold from its ⋯ menu "
            + "when you're ready."
        alert.addButton(withTitle: "Roll Back")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        Task { await self.rollBack(id, to: tag) }
    }

    // MARK: - Sheets: release notes, details, activity

    private static func newestFirst(_ list: [ReleaseInfo]) -> [ReleaseInfo] {
        list.sorted { versionIsNewer($0.tagName, than: $1.tagName) }
    }

    func showReleaseNotes(_ id: String) {
        guard let row = rows.first(where: { $0.id == id }) else { return }
        let releases = offeredReleases(row)
        activeSheet = .notes(NotesModel(title: "\(row.app.name) — Release Notes", installed: row.installed,
                                        releases: Self.newestFirst(releases), loading: false,
                                        message: releases.isEmpty ? "No releases yet." : nil))
    }

    /// The releases a person is shown or offered for a row (release notes, the ⋯ version list, Roll Back):
    /// development builds only with the Development builds switch on.
    func offeredReleases(_ row: Row) -> [ReleaseInfo] {
        Self.devChannel ? row.releases : row.releases.filter { !Self.isDevTag($0.tagName) }
    }

    /// True when the release carries the signed checksum manifest a strict install needs.
    nonisolated static func hasSignedManifest(_ rel: ReleaseInfo) -> Bool {
        rel.assets.contains { $0.name == "SHA256SUMS" } && rel.assets.contains { $0.name == "SHA256SUMS.minisig" }
    }

    func showDetails(_ id: String) { activeSheet = .details(id) }

    func showActivity() { activeSheet = .activity(HistoryStore.load()) }

    // MARK: - The launcher's own "what's new", once after it has been updated

    private func prepareLauncherWhatsNew() {
        let last = UserDefaults.standard.string(forKey: Self.lastSeenKey)
        // Nothing recorded, but the launcher has been used before (it saved settings or installed apps): an update
        // from a version that predates the record.
        let d = UserDefaults.standard
        let usedBefore = last == nil && ([Self.codeIDKey, Self.appOrderKey, "theatre.updateMode", "theatre.appearance"]
            .contains { d.object(forKey: $0) != nil } || !InstallManager.shared.manifest().isEmpty)
        if LauncherWhatsNew.shouldShow(lastSeen: last, current: currentVersion, existingInstall: usedBefore) {
            launcherWhatsNew = currentVersion
        } else if last.map({ !VersionDisplay.equal($0, currentVersion) }) ?? true {
            UserDefaults.standard.set(currentVersion, forKey: Self.lastSeenKey)   // a fresh install: nothing to announce
        }
    }

    func dismissLauncherWhatsNew() {
        UserDefaults.standard.set(currentVersion, forKey: Self.lastSeenKey)
        launcherWhatsNew = nil
    }

    /// The notes of every launcher release since the one last seen (the launcher's repo is public, so no Keychain
    /// read), with the catalog's one-line what's-new as the offline fallback.
    func showLauncherWhatsNew() async {
        let since = UserDefaults.standard.string(forKey: Self.lastSeenKey)
        let current = currentVersion
        dismissLauncherWhatsNew()
        let title = "JB Theatre Tools — What's New"
        activeSheet = .notes(NotesModel(title: title, installed: current, releases: [], loading: true, message: nil))
        var list: [ReleaseInfo] = []
        if let s = selfInfo {
            let all = (try? await cachedOnlyClient().releases(owner: s.owner, repo: s.repo)) ?? []
            list = all.filter { rel in
                !Self.versionIsNewer(rel.tagName, than: current)
                    // Nothing seen before (an update from a version that didn't record it): just this version's notes.
                    && (since.map { old in Self.versionIsNewer(rel.tagName, than: old) } ?? VersionDisplay.equal(rel.tagName, current))
                    && (Self.devChannel || !Self.isDevTag(rel.tagName))
            }
        }
        var fallback: String? = nil
        if let line = selfInfo?.whatsNew, !line.isEmpty {
            fallback = selfInfo?.whatsNewVersion.map { "New in \($0): \(line)" } ?? line
        }
        guard case .notes(let open)? = activeSheet, open.title == title else { return }   // closed while loading
        activeSheet = .notes(NotesModel(title: title, installed: current, releases: Self.newestFirst(list), loading: false,
                                        message: list.isEmpty ? (fallback ?? ReleaseNotesText.empty) : nil))
    }

    // MARK: - Setup files (export / import)

    private func inform(_ title: String, _ text: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = text
        alert.addButton(withTitle: "OK")
        _ = alert.runModal()
    }

    /// Saves which apps (and editions) are installed, their versions, holds, and the list layout.
    func exportSetup() {
        var entries: [SetupProfile.Entry] = []
        for row in rows {
            let vids: [String?] = row.app.hasVariants ? (row.app.variants ?? []).map { Optional($0.id) } : [nil]
            for vid in vids {
                guard let version = InstallManager.shared.installedVersion(row.app.installKey(variantId: vid)) else { continue }
                entries.append(.init(id: row.id, variant: row.app.isDefaultVariant(vid) ? nil : vid,
                                     version: version, held: isHeld(row.id)))
            }
        }
        guard !entries.isEmpty else {
            inform("Export Setup", "No apps are installed yet, so there's nothing to export.")
            return
        }
        let profile = SetupProfile(
            createdAt: SetupProfile.timestamp(Date()),
            createdBy: "JB Theatre Tools \(currentVersion) (macOS)",
            apps: entries,
            layout: .init(pinned: Array(pinnedIds).sorted(), hidden: Array(hiddenIds).sorted(), order: rows.map(\.id),
                          categoryOrder: fullCategoryOrder, collapsed: Array(collapsedGroups).sorted()))
        let panel = NSSavePanel()
        panel.title = "Export Setup"
        panel.nameFieldStringValue = SetupProfile.suggestedFileName(Date())
        panel.allowedContentTypes = [.json]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try profile.serialize().write(to: url, options: .atomic)
            AppLog.shared.log("exported setup: \(entries.count) slot(s)")
            inform("Setup Exported", "Saved \(entries.count) installed app\(entries.count == 1 ? "" : "s") to \(url.lastPathComponent).\n\nOn another machine, choose Import Setup… to install the same apps.")
        } catch {
            inform("Export Setup", error.localizedDescription)
        }
    }

    /// Reads a setup file and shows what importing it would do (nothing installs until `runImport`).
    func importSetup() {
        guard !showLock else {
            inform("Show Lock Is On", "Unlock it to import a setup.")
            return
        }
        let panel = NSOpenPanel()
        panel.title = "Import Setup"
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            let size = (try? url.resourceValues(forKeys: [.fileSizeKey]))?.fileSize ?? 0
            if size > SetupProfile.maxBytes { throw SetupProfile.FormatError(message: "This file is too large to be a setup file.") }
            let profile = try SetupProfile.parse(try Data(contentsOf: url))
            let catalog = rows.map { r in
                SetupPlanner.CatalogEntry(id: r.id, name: r.app.name, variants: (r.app.variants ?? []).map { (id: $0.id, label: $0.label) })
            }
            let installedKeys = Set(InstallManager.shared.manifest().keys.filter { InstallManager.shared.installedVersion($0) != nil })
            var plan = SetupPlanner.build(profile, catalog: catalog, installedKeys: installedKeys, allowDevTags: Self.devChannel)
            // An edition with no macOS build for this Mac can't install here.
            plan.toInstall = plan.toInstall.filter { item in
                guard let app = rows.first(where: { $0.id == item.appId })?.app else { return false }
                if app.macAssetName(variantId: item.variantId ?? app.variants?.first?.id) != nil { return true }
                plan.skipped.append("\(item.label) — no macOS build")
                return false
            }
            activeSheet = .importPreview(ImportPreviewModel(source: url.lastPathComponent, summary: SetupPlanner.summary(plan),
                                                            plan: plan, layout: profile.layout))
        } catch {
            inform("Import Setup", error.localizedDescription)
        }
    }

    /// Applies a previewed setup: holds first, then (optionally) the layout, then every missing slot as one batch.
    func runImport(_ model: ImportPreviewModel, applyLayout: Bool) async {
        activeSheet = nil
        guard !blockedByLock("import setup") else { return }
        // A batch (e.g. an automatic update) started while the preview was open: say so instead of doing nothing.
        guard !batchRunning else {
            inform("Import Setup", "Another install is running. Import the setup again when it has finished.")
            return
        }
        // Let the preview sheet finish closing: the first download may need the Keychain explainer sheet.
        try? await Task.sleep(nanoseconds: 400_000_000)
        for id in model.plan.holdIds where !isHeld(id) { toggleHold(id) }
        if applyLayout, let layout = model.layout { self.applyLayout(layout) }
        AppLog.shared.log("import setup: \(model.plan.toInstall.count) to install, \(model.plan.holdIds.count) held, layout \(applyLayout ? "applied" : "kept")")
        guard !model.plan.toInstall.isEmpty else { return }
        guard hasCredentials else {
            inform("Import Setup", credentialsPrompt + ".")
            return
        }
        // The default edition is passed by its real id: a nil variant would mean "the row's selected edition".
        // A development build named in the file installs only on a Mac with Development builds switched on.
        let work: [Slot] = model.plan.toInstall.compactMap { item in
            if let tag = item.tag, Self.isDevTag(tag), !Self.devChannel {
                AppLog.shared.log("import setup: skipped \(item.appId) \(tag) — a development build")
                return nil
            }
            guard let app = rows.first(where: { $0.id == item.appId })?.app else { return nil }
            return Slot(id: item.appId, variant: item.variantId ?? (app.hasVariants ? app.variants?.first?.id : nil), tag: item.tag)
        }
        await runSlots(orderedSlots(work), label: "import setup")
    }

    /// Takes over a setup file's list layout (pins, hidden apps, app order, section order, folded sections).
    private func applyLayout(_ l: SetupProfile.Layout) {
        let known = Set(rows.map(\.id))
        pinnedIds = Set(l.pinned).intersection(known)
        UserDefaults.standard.set(Array(pinnedIds), forKey: Self.pinnedKey)
        hiddenIds = Set(l.hidden).intersection(known)
        UserDefaults.standard.set(Array(hiddenIds), forKey: Self.hiddenKey)
        var seen = Set<String>()
        let order = l.order.filter { known.contains($0) && seen.insert($0).inserted }
        UserDefaults.standard.set(order, forKey: Self.appOrderKey)
        rows = Self.applyingSavedOrder(rows)
        setCategoryOrder(l.categoryOrder)
        collapsedGroups = Set(l.collapsed)
        UserDefaults.standard.set(Array(collapsedGroups), forKey: Self.collapsedKey)
    }

    // MARK: - Storage, diagnostics

    /// The installed apps' size and the download cache's size, measured off the main actor.
    func storageSizes() async -> (installed: Int64, cache: Int64) {
        await Task.detached(priority: .utility) {
            (InstallManager.shared.installedSize(), InstallManager.shared.cacheSize())
        }.value
    }

    /// Clearing the cache is off during show lock and while anything downloads or installs (installs extract there).
    var canClearCache: Bool { !showLock && !batchRunning && !rows.contains { $0.busy } }

    func clearCache() async {
        guard canClearCache else { return }
        await Task.detached(priority: .utility) { InstallManager.shared.clearCache() }.value
        AppLog.shared.log("download cache cleared")
    }

    /// The support report — versions, settings, every app's state and recent log lines; never a secret.
    func diagnostics() -> String {
        let host: String? = Self.authMode == .server ? serverBase.flatMap { URL(string: $0)?.host } : nil
        let apps = rows.map { row -> Diagnostics.AppLine in
            let status: String
            switch row.status {
            case .upToDate: status = "up to date"
            case .updateAvailable: status = "update available"
            case .notInstalled: status = "not installed"
            case .installed: status = "installed (not checked)"
            case .noRelease: status = "no release"
            case .missingAsset: status = "no macOS build"
            case .error: status = "error"
            case .checking: status = "checking"
            case .noAccess: status = "no access"
            case .unknown: status = "unknown"
            }
            return .init(name: row.displayName, installed: row.installed, latest: row.latest, status: status,
                         held: row.installed != nil && isHeld(row.id))
        }
        return Diagnostics.build(.init(
            launcherVersion: currentVersion,
            os: "macOS \(ProcessInfo.processInfo.operatingSystemVersionString)",
            arch: MacArch.isAppleSilicon ? "Apple silicon" : "Intel",
            authMode: Self.authMode.label, relayHost: host, devChannel: Self.devChannel, showLock: showLock,
            installLocation: UserDefaults.standard.bool(forKey: "theatre.installToApplications") ? "Applications folder" : "Launcher",
            apps: apps, logTail: AppLog.shared.tail(Diagnostics.logLines), now: Date()))
    }

    func copyDiagnostics() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(diagnostics(), forType: .string)
        AppLog.shared.log("diagnostics copied")
    }

    // MARK: - Quick launch (the menu-bar extra)

    /// Every installed slot as (install key, name) — pinned apps first, then the list order.
    var installedSlotsForLaunch: [(key: String, name: String)] {
        let ordered = rows.filter { pinnedIds.contains($0.id) } + rows.filter { !pinnedIds.contains($0.id) }
        return ordered.flatMap { row -> [(key: String, name: String)] in
            let vids: [String?] = row.app.hasVariants ? (row.app.variants ?? []).map { Optional($0.id) } : [nil]
            return vids.compactMap { vid in
                let key = row.app.installKey(variantId: vid)
                guard InstallManager.shared.installedVersion(key) != nil else { return nil }
                return (key: key, name: row.app.name + row.app.variantSuffix(vid))
            }
        }
    }

    func launchSlot(_ key: String) {
        // Never start an app while it's being installed / replaced (the menu-bar item has no busy state of its own).
        let appId = String(key.split(separator: "@").first ?? Substring(key))   // slot keys are "<id>" or "<id>@<variant>"
        if let row = rows.first(where: { $0.app.id == appId }), row.installing {
            inform("Couldn't Open", "\(row.displayName) is being installed. Open it when that has finished.")
            return
        }
        do {
            try InstallManager.shared.launch(installKey: key)
            AppLog.shared.log("launched \(key) (menu bar)")
        } catch {
            inform("Couldn't Open", error.localizedDescription)
        }
    }

    // MARK: - Launcher self-update

    /// Checks JBTheatreTools' own latest release against the running version.
    @discardableResult
    func checkLauncherUpdate() async -> LauncherCheck {
        guard let s = selfInfo else { return .unavailable("No self-update info in catalog.") }
        // The launcher repo is public — use credentials only if already in memory; never force a
        // Keychain read here (would trigger the prompt for a check that doesn't need auth).
        let client = cachedOnlyClient()
        do {
            let target = try await launcherTarget(client: client, owner: s.owner, repo: s.repo)
            launcherUpdateAvailable = target?.tagName
            return target.map { .available(current: currentVersion, latest: $0.tagName) } ?? .upToDate(currentVersion)
        } catch GitHubError.noRelease {
            return .unavailable("No launcher release published yet.")
        } catch {
            return .unavailable(error.localizedDescription)
        }
    }

    /// The launcher release to offer, or nil when there's nothing to do. Dev channel ON: the highest release
    /// including the launcher's own dev builds (`vX.Y.Z-dev.N`; a dev build reports its full tag as
    /// CFBundleShortVersionString, stamped by build.sh from JBTT_VERSION). OFF: the latest release — and when
    /// THIS copy is a dev build, that release even if it's older ("back to release").
    func launcherTarget(client: GitHubClient, owner: String, repo: String) async throws -> ReleaseInfo? {
        let current = currentVersion
        if Self.devChannel {
            guard let pick = Self.latest(from: try await client.releases(owner: owner, repo: repo), devChannel: true)
            else { return nil }
            return Self.versionIsNewer(pick.tagName, than: current) ? pick : nil
        }
        let latest = try await client.latestRelease(owner: owner, repo: repo)
        if Self.versionIsNewer(latest.tagName, than: current) { return latest }
        return Self.isDevTag(current) ? latest : nil
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
            guard let info = try await launcherTarget(client: client, owner: s.owner, repo: s.repo) else {
                launcherDownloadMessage = "You're up to date."
                return
            }
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

/// The little slice of state the app's menus need (see `AppState.chrome`).
@MainActor
final class ChromeState: ObservableObject {
    struct LaunchSlot: Equatable { let key: String; let name: String }
    @Published var showLock = false
    @Published var canUpdateAll = false
    @Published var launchSlots: [LaunchSlot] = []
}
