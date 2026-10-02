package com.jamesbreedon.jbtheatretools.ui

import android.app.Application
import android.net.Uri
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.jamesbreedon.jbtheatretools.core.ActiveInstalls
import com.jamesbreedon.jbtheatretools.core.ActivityEvent
import com.jamesbreedon.jbtheatretools.core.AppLog
import com.jamesbreedon.jbtheatretools.core.AppFilter
import com.jamesbreedon.jbtheatretools.core.AppLayout
import com.jamesbreedon.jbtheatretools.core.BatchRules
import com.jamesbreedon.jbtheatretools.core.BatchSlot
import com.jamesbreedon.jbtheatretools.core.ByteSize
import com.jamesbreedon.jbtheatretools.core.LayoutPrefs
import com.jamesbreedon.jbtheatretools.core.PreviousVersion
import com.jamesbreedon.jbtheatretools.core.AppVisibility
import com.jamesbreedon.jbtheatretools.core.Appearance
import com.jamesbreedon.jbtheatretools.core.AppStatus
import com.jamesbreedon.jbtheatretools.core.AuthMode
import com.jamesbreedon.jbtheatretools.core.AutoUpdatePolicy
import com.jamesbreedon.jbtheatretools.core.AutoUpdateScheduler
import com.jamesbreedon.jbtheatretools.core.CatalogApp
import com.jamesbreedon.jbtheatretools.core.Diagnostics
import com.jamesbreedon.jbtheatretools.core.InstallProgress
import com.jamesbreedon.jbtheatretools.core.InstallResult
import com.jamesbreedon.jbtheatretools.core.LauncherRepository
import com.jamesbreedon.jbtheatretools.core.LauncherWhatsNew
import com.jamesbreedon.jbtheatretools.core.Notifier
import com.jamesbreedon.jbtheatretools.core.SetupPlanner
import com.jamesbreedon.jbtheatretools.core.SetupProfile
import com.jamesbreedon.jbtheatretools.core.StatusFilter
import com.jamesbreedon.jbtheatretools.core.UpdateAnnouncer
import com.jamesbreedon.jbtheatretools.core.UpdateCheckScheduler
import com.jamesbreedon.jbtheatretools.core.UpdatePolicy
import com.jamesbreedon.jbtheatretools.core.UserMessage
import com.jamesbreedon.jbtheatretools.core.VersionCompare
import com.jamesbreedon.jbtheatretools.net.ReleaseInfo
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.time.Instant

/** Which of the three bottom-nav destinations is showing (§7). */
enum class Tab { APPS, UPDATES, ABOUT }

/** The in-app release notes sheet: an app's (or the launcher's) releases, loaded on demand. */
data class NotesSheet(
    val title: String,
    val installed: String? = null,
    val releases: List<ReleaseInfo> = emptyList(),
    val loading: Boolean = true,
    /** Shown when there are no releases to list (an error, or the launcher's catalog line offline). */
    val message: String? = null,
)

/** The preview before a setup file is applied; [layout] is the file's list layout, used only when [applyLayout]. */
data class ImportPreview(
    val summary: String,
    val plan: SetupPlanner.Plan,
    val layout: SetupProfile.Layout? = null,
    val applyLayout: Boolean = false,
)

/** "Install version": an app's releases that have an Android build, loaded on demand. */
data class VersionsSheet(
    val app: CatalogApp,
    val installed: String?,
    val releases: List<ReleaseInfo> = emptyList(),
    val loading: Boolean = true,
    val message: String? = null,
)

/**
 * Going to another version of an installed app. Android can't install an older version over a newer one, so an older
 * target ([needsRemoval]) removes the app first and installs straight after — asked first, as it resets the app's
 * saved settings.
 */
data class ReinstallRequest(
    val app: CatalogApp,
    /** The release tag to install; null = the channel's latest (Back to release). */
    val tag: String?,
    /** The version being installed, for the wording ("1.1.0"). */
    val target: String,
    val from: String?,
    val kind: Kind,
    val needsRemoval: Boolean,
) {
    enum class Kind {
        /** A dev build back to the release (Development builds switched off). */
        BACK_TO_RELEASE,
        /** One click back to the version before the last update, then held there. */
        ROLL_BACK,
        /** A version hand-picked from "Install version". */
        PICKED,
    }
}

data class LauncherUiState(
    val signedIn: Boolean = false,
    val loading: Boolean = false,
    val statuses: List<AppStatus> = emptyList(),
    val progress: Map<String, InstallProgress> = emptyMap(),
    val tab: Tab = Tab.APPS,
    val search: String? = null,               // null = the search field is closed
    val statusFilter: StatusFilter = StatusFilter.ALL,
    val appearance: Appearance = Appearance.SYSTEM,
    val snackbar: String? = null,
    val sheetFor: CatalogApp? = null,         // long-press actions
    val confirmRemove: CatalogApp? = null,    // rule-8 confirm
    val busyAll: Boolean = false,
    /** A newer launcher version with an Android build (e.g. "1.28.0"), or null. */
    val launcherUpdate: String? = null,
    val devRevealed: Boolean = false,
    /** Rule-8 confirm before an older version replaces a newer one (it removes the app first) or a roll back. */
    val confirmReinstall: ReinstallRequest? = null,
    val devChannel: Boolean = false,
    /** Show lock: installs, updates and removals are paused; opening apps still works. */
    val showLock: Boolean = false,
    /** "Turn off show lock?" is showing (turning it off always asks first). */
    val confirmShowLockOff: Boolean = false,
    val autoCheckInterval: String = UpdatePolicy.DEFAULT_INTERVAL,
    val notifyUpdates: Boolean = true,
    /** "Install updates automatically" (Android 12+ only — [autoInstallAvailable]). */
    val autoInstallUpdates: Boolean = false,
    val autoInstallAvailable: Boolean = false,
    /** Set to this build's version on the first launch after the launcher was updated (the banner). */
    val launcherWhatsNew: String? = null,
    val notes: NotesSheet? = null,
    val importPreview: ImportPreview? = null,
    val history: List<ActivityEvent> = emptyList(),
    /** Installed apps' size and the download cache's size (About → Storage), or null until measured. */
    val storage: Pair<Long, Long>? = null,
    /** The log's last lines (About → Log), read off the main thread. */
    val logTail: String = "",
    /** Pinned, hidden, order, sections — this device's list layout. */
    val layout: LayoutPrefs = LayoutPrefs(),
    /** Phones and small tablets: rows instead of tiles. */
    val listView: Boolean = false,
    /** The catalog's section order (for the grouped list). */
    val catalogCategories: List<String> = emptyList(),
    /** The version each app had before its last install changed it (Roll back's target; shown in the details). */
    val previousVersions: Map<String, String> = emptyMap(),
    /** Releases loaded for an app's details (Roll back is offered only for a release that can install). */
    val appReleases: Map<String, List<ReleaseInfo>> = emptyMap(),
    val versions: VersionsSheet? = null,
    /** The section whose actions are showing (long-press on its header). */
    val sectionSheet: String? = null,
) {
    val installedCount: Int get() = statuses.count { it.isInstalled }
    /** Apps with an update that isn't held. */
    val appUpdateCount: Int get() = statuses.count { it.updatePending }
    /** Everything with an update — the apps plus the launcher itself (status row, nav badge, Update all). */
    val updateCount: Int get() = appUpdateCount + (if (launcherUpdate != null) 1 else 0)
    /** The download size of every pending app update (Update all's label). */
    val updateBytes: Long get() = com.jamesbreedon.jbtheatretools.core.ByteSize.sum(
        statuses.filter { it.updatePending && it.canInstall }.map { it.apkSizeBytes }
    )
    val anyInstallRunning: Boolean get() = busyAll || progress.values.any { it.isActive }

    /** Install every app's work: everything with an Android build not installed yet, plus the pending updates. */
    val installAllWork: List<AppStatus> get() = BatchRules.installAllWork(statuses)
    val installAllBytes: Long get() = ByteSize.sum(installAllWork.map { it.apkSizeBytes })
    /** Apps with an Android build that aren't installed (Install every app is offered while there are any). */
    val notInstalledCount: Int get() = statuses.count { !it.isInstalled && it.canInstall && !it.hidden }

    /** A search or status filter narrows the list: sections show open and reordering is paused. */
    val filtering: Boolean get() = AppFilter.isActive(search, statusFilter)

    /** Every app the Apps list would show without a filter, in this device's order (hidden apps left out). */
    fun listedStatuses(): List<AppStatus> {
        val byId = statuses.associateBy { it.app.id }
        return AppLayout.ordered(statuses.map { it.app.id }, layout.order).mapNotNull { byId[it] }
            .filter { !it.hidden && it.app.id !in layout.hidden }
    }

    /** The Apps list after Find & filter: the query matches name, blurb, category or id; plus the status chips. */
    fun visibleStatuses(): List<AppStatus> = listedStatuses().filter {
        AppFilter.matchesQuery(search, it.app.name, it.app.blurb, it.app.category, it.app.id) &&
            AppFilter.matchesStatus(statusFilter, it.isInstalled, it.updatePending, !it.isInstalled && it.canInstall)
    }

    /** The Apps list's sections: Pinned first, then each category with something to show. */
    fun groups(): List<Pair<AppLayout.Group, List<AppStatus>>> {
        val shown = visibleStatuses()
        val byId = shown.associateBy { it.app.id }
        return AppLayout.groups(shown.map { it.app.id }, ::categoryOf, layout.pinned, layout.categoryOrder, catalogCategories)
            .map { g -> g to g.ids.mapNotNull { byId[it] } }
    }

    fun categoryOf(id: String): String = AppLayout.categoryOf(statuses.firstOrNull { it.app.id == id }?.app?.category)

    /** A section is folded unless a filter is on (a match never hides behind a fold). */
    fun isCollapsed(key: String): Boolean = key in layout.collapsed && !filtering

    /** "3 of 24 apps" — shown while a search or filter is on. */
    fun countLabel(): String = AppLayout.countLabel(visibleStatuses().size, listedStatuses().size, filtering)

    /** [id] can move [up]/down inside its section (never while a filter is on). */
    fun canMove(id: String, up: Boolean): Boolean = !filtering && moved(id, up) != null

    /** The app order after moving [id] past its neighbour on screen, or null. */
    fun moved(id: String, up: Boolean): List<String>? {
        // The full order (hidden apps keep their place); the neighbour must be one that's on screen.
        val order = AppLayout.ordered(statuses.map { it.app.id }, layout.order)
        val visible = listedStatuses().map { it.app.id }.filter { groupKeyOf(it) !in layout.collapsed }.toSet()
        return AppLayout.move(order, id, up, visible, ::groupKeyOf)
    }

    fun groupKeyOf(id: String): String = AppLayout.groupKey(id, layout.pinned, ::categoryOf)
}

class LauncherViewModel(app: Application) : AndroidViewModel(app) {

    val repo = LauncherRepository(app)
    private val notifier = Notifier(app)

    private val _state = MutableStateFlow(
        LauncherUiState(
            signedIn = repo.hasCredential(),
            appearance = repo.settings.appearance,
            statuses = repo.initialStatuses(),
            layout = repo.settings.layout,
            listView = repo.settings.listView,
            catalogCategories = repo.catalog.categories,
            devRevealed = repo.settings.devChannel,
            devChannel = repo.settings.devChannel,
            showLock = repo.settings.showLock,
            autoCheckInterval = repo.settings.autoCheckInterval,
            notifyUpdates = repo.settings.notifyUpdates && Notifier(app).canPost(),
            autoInstallUpdates = repo.settings.autoInstallUpdates && AutoUpdatePolicy.available(android.os.Build.VERSION.SDK_INT),
            autoInstallAvailable = AutoUpdatePolicy.available(android.os.Build.VERSION.SDK_INT),
        )
    )
    val state: StateFlow<LauncherUiState> = _state.asStateFlow()

    val launcherVersion: String get() = repo.launcherVersion

    /** When the last check ran (the scheduler's clock). */
    private var lastCheck: Instant? = null
    /** Stop pressed on Update all / an import (or show lock turned on): finish the current app, start no more. */
    @Volatile private var stopRequested = false
    /** The apps of the Update all / import that's running — what Stop cancels (never a separate, manual install). */
    private val batchIds: MutableSet<String> = java.util.concurrent.ConcurrentHashMap.newKeySet()
    /** The release-notes load in flight — replaced (cancelled) by the next one, and by closing the sheet. */
    private var notesJob: Job? = null
    /** Apps handed to the system uninstaller, with the version they had — recorded once they're gone (guarded by itself). */
    private val pendingRemovals = HashMap<String, String?>()

    init {
        AppVisibility.foreground = true   // the view model is created with the activity, i.e. on screen
        prepareLauncherWhatsNew()
        reloadHistory()
        refreshInstalledOnly()   // what's installed and each app's previous version, off the main thread
        if (repo.hasCredential()) refresh()
        updateShortcuts()
        if (_state.value.notifyUpdates) notifier.ensureChannel()
        syncBackgroundChecks()
        // "While open, check every…": one tick a minute; a check runs when it's due, the launcher is on screen and
        // nothing else is happening. (Closed, the background job takes over when notifications are on.)
        viewModelScope.launch {
            while (true) {
                delay(60_000)
                val s = _state.value
                if (AppVisibility.foreground && s.signedIn && !s.showLock && !s.loading && !s.anyInstallRunning &&
                    UpdatePolicy.isDue(lastCheck, Instant.now(), repo.settings.autoCheckInterval)
                ) runRefresh(scheduled = true)
            }
        }
    }

    fun setForeground(on: Boolean) {
        AppVisibility.foreground = on
        // Notifications may have been switched off for the app in Android settings meanwhile.
        if (on) {
            _state.update { it.copy(notifyUpdates = repo.settings.notifyUpdates && notifier.canPost()) }
            syncBackgroundChecks()
        }
    }

    /** The background check runs only when notifications are on and allowed (it has nothing else to do); automatic
     *  updates have their own job (charging + Wi-Fi + idle). */
    private fun syncBackgroundChecks() {
        UpdateCheckScheduler.sync(getApplication(), repo.settings, repo.hasCredential() && notifier.canPost())
        AutoUpdateScheduler.sync(getApplication(), repo.settings, repo.hasCredential())
    }

    fun refresh() = runRefresh(scheduled = false)

    /**
     * A check. [scheduled] (the "While open, check again" timer): an app whose check fails keeps its row as it was —
     * a passing network blip mustn't blank every row; a check the user asked for shows the failure.
     */
    private fun runRefresh(scheduled: Boolean) {
        if (_state.value.loading) return
        _state.update { it.copy(loading = true) }
        viewModelScope.launch {
            val fresh = repo.refresh()
            val launcherUpdate = repo.checkLauncherUpdate()
            lastCheck = Instant.now()
            _state.update { s ->
                val statuses = if (!scheduled) fresh else fresh.map { n ->
                    val old = s.statuses.firstOrNull { it.app.id == n.app.id }
                    if (n.checkFailed && old != null && old.latestVersion != null) {
                        old.copy(installedVersion = n.installedVersion, held = n.held)
                    } else n
                }
                s.copy(
                    loading = false, statuses = statuses, signedIn = repo.hasCredential(),
                    appReleases = emptyMap(),   // a check may have found new releases: the details load them again
                    // The launcher's check reports "no update" and "failed" alike; only a restart clears a found one.
                    launcherUpdate = if (scheduled) launcherUpdate ?: s.launcherUpdate else launcherUpdate,
                )
            }
            announceNewUpdates()
            runAutoUpdate()
        }
    }

    /**
     * After a check, with "Install updates automatically" on: update what Android lets this launcher update without
     * asking (apps it installed), quietly. The launcher is on screen, so no other app is in use. Never under show lock
     * or during another batch; one Android would only update after asking keeps its Update button.
     */
    private fun runAutoUpdate() {
        val s = _state.value
        if (!s.autoInstallUpdates || s.showLock || s.busyAll || !repo.hasCredential()) return
        val pending = AutoUpdatePolicy.candidates(s.statuses, repo.settings.heldApps, ActiveInstalls.ids())
        if (pending.isEmpty()) return
        stopRequested = false
        batchIds.clear()
        batchIds.addAll(pending.map { it.app.id })
        ActiveInstalls.queue(batchIds)
        _state.update { it.copy(busyAll = true) }
        viewModelScope.launch {
            val result = try {
                repo.autoUpdate(pending, stop = { stopRequested }) { p -> onProgress(p) }
            } finally {
                batchIds.clear()
                ActiveInstalls.clearQueue()
            }
            refreshInstalledOnly()
            reloadHistory()
            _state.update {
                it.copy(
                    busyAll = false,
                    snackbar = when {
                        result.updated.isEmpty() && result.needsUser.isEmpty() -> it.snackbar
                        result.needsUser.isEmpty() -> UpdatePolicy.autoUpdateSummary(result.updated)
                        result.updated.isEmpty() -> AutoUpdatePolicy.needsYourOk(result.needsUser)
                        else -> UpdatePolicy.autoUpdateSummary(result.updated) + ". " + AutoUpdatePolicy.needsYourOk(result.needsUser)
                    },
                )
            }
        }
    }

    /** One notification per new update (never for held apps), and only while the launcher isn't on screen. */
    private fun announceNewUpdates() {
        val s = _state.value
        UpdateAnnouncer.announce(repo.settings, notifier, s.statuses, s.launcherUpdate)
    }

    /** Update this launcher (same verification as any app). Android closes the app to replace it. */
    fun updateLauncher() {
        if (blockedByLock()) return
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.") }
            repo.openInstallPermissionSettings()
            return
        }
        viewModelScope.launch { runLauncherUpdate() }
    }

    private suspend fun runLauncherUpdate() {
        val result = repo.installLauncher { p -> onProgress(p) }
        // Only reached when Android did NOT replace us (refused, cancelled or failed verification).
        val message = when (result) {
            InstallResult.CANCELLED ->
                if (repo.settings.showLock) "Show lock is on — JB Theatre Tools was not updated" else "Update of JB Theatre Tools cancelled"
            InstallResult.NOT_INSTALLED -> "JB Theatre Tools was not updated"
            else -> null   // updated (this process ends), or that update was already running
        }
        if (message != null) _state.update { it.copy(snackbar = message) }
    }

    /** Progress from the repository. A cancelled download simply goes back to how the row was. */
    private fun onProgress(p: InstallProgress) {
        _state.update { s ->
            if (p.phase == InstallProgress.Phase.CANCELLED) s.copy(progress = s.progress - p.catalogId)
            else s.copy(progress = s.progress + (p.catalogId to p))
        }
    }

    fun selectTab(tab: Tab) = _state.update { it.copy(tab = tab, sheetFor = null) }

    fun openSearch() = _state.update { it.copy(search = "") }

    fun setSearch(q: String) = _state.update { it.copy(search = q) }

    fun closeSearch() = _state.update { it.copy(search = null) }

    fun setStatusFilter(filter: StatusFilter) = _state.update { it.copy(statusFilter = filter) }

    fun showSheet(app: CatalogApp?) {
        _state.update { it.copy(sheetFor = app) }
        // An installed app's details offer Roll back only for a release that can install: load its releases.
        if (app != null && _state.value.statuses.firstOrNull { it.app.id == app.id }?.isInstalled == true) loadReleases(app)
    }

    /** Loads [app]'s releases into the details cache (once per check); a failure just means no Roll back offered. */
    private fun loadReleases(app: CatalogApp, then: ((List<ReleaseInfo>?, Throwable?) -> Unit)? = null) {
        _state.value.appReleases[app.id]?.let { cached -> then?.invoke(cached, null); return }
        viewModelScope.launch {
            val result = runCatching { repo.releasesFor(app) }
            result.getOrNull()?.let { list -> _state.update { it.copy(appReleases = it.appReleases + (app.id to list)) } }
            then?.invoke(result.getOrNull(), result.exceptionOrNull())
        }
    }

    fun askRemove(app: CatalogApp?) {
        if (app != null && blockedByLock()) return
        _state.update { it.copy(confirmRemove = app, sheetFor = null) }
    }

    fun dismissSnackbar() = _state.update { it.copy(snackbar = null) }

    fun setAppearance(value: Appearance) {
        repo.settings.appearance = value
        _state.update { it.copy(appearance = value) }
    }

    fun signIn(secret: String, mode: AuthMode, debugBase: String = "", debugKey: String = "") {
        when (mode) {
            AuthMode.TOKEN -> repo.secrets.saveToken(secret)
            AuthMode.SERVER -> repo.secrets.savePassphrase(secret)
            AuthMode.DEBUG_FEED -> {
                repo.settings.debugFeedBase = debugBase
                if (debugKey.isNotBlank()) repo.settings.debugFeedMinisignKey = debugKey
            }
        }
        repo.settings.authMode = mode
        repo.settings.firstRunDone = true
        _state.update { it.copy(signedIn = repo.hasCredential()) }
        syncBackgroundChecks()
        refresh()
    }

    fun signOut() {
        repo.secrets.clear()
        repo.settings.debugFeedBase = ""
        repo.settings.authMode = AuthMode.SERVER
        _state.update {
            it.copy(signedIn = false, statuses = repo.initialStatuses(), appReleases = emptyMap())
        }
        syncBackgroundChecks()
    }

    // ── Show lock ───────────────────────────────────────────────────────────

    /** Show lock on: at once. Off: only after "Turn off show lock?" is confirmed — never with one tap mid-show. */
    fun requestShowLock(on: Boolean) {
        if (on) setShowLock(true)
        else if (_state.value.showLock) _state.update { it.copy(confirmShowLockOff = true, sheetFor = null) }
    }

    /** "Keep on" (or the sheet dismissed): the lock stays on. */
    fun keepShowLock() = _state.update { it.copy(confirmShowLockOff = false) }

    fun confirmShowLockOff() {
        _state.update { it.copy(confirmShowLockOff = false) }
        setShowLock(false)
    }

    private fun setShowLock(on: Boolean) {
        repo.settings.showLock = on
        _state.update { it.copy(showLock = on, sheetFor = null) }
        // Nothing more installs once the lock is on: a batch stops, and EVERY download in flight — a row's own
        // install and the launcher's update too — is cancelled by the repository's cancel check, which reads the lock.
        if (on && _state.value.busyAll) stopAll()
    }

    /** The model-level guard behind every install / update / removal (the UI hides them too). */
    private fun blockedByLock(): Boolean {
        if (!_state.value.showLock) return false
        _state.update { it.copy(snackbar = "Show lock is on — turn it off in About to install, update or remove apps.") }
        return true
    }

    // ── Install / update ────────────────────────────────────────────────────

    fun install(app: CatalogApp, tag: String? = null) = startInstall(app, tag)

    /**
     * One app's install from its row or sheet. [lenient] = a hand-picked version (see [LauncherRepository.install]);
     * [replacing] = the version a removal just took off; [afterInstalled] runs once it has installed.
     */
    private fun startInstall(
        app: CatalogApp,
        tag: String? = null,
        lenient: Boolean = false,
        replacing: String? = null,
        afterInstalled: (() -> Unit)? = null,
    ) {
        _state.update { it.copy(sheetFor = null) }
        if (blockedByLock()) return
        if (!repo.canRequestInstalls()) {
            _state.update {
                it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.")
            }
            repo.openInstallPermissionSettings()
            return
        }
        // A second tap while this app is already downloading or installing: nothing new starts and nothing is said.
        if (_state.value.progress[app.id]?.isActive == true || repo.isInstalling(app.id)) return
        repo.clearCancel(app.id)
        // The row shows Downloading (its buttons disable, Cancel appears) at once — before the release lookup's
        // network call, so a quick second tap can't start a second install.
        val placeholder = InstallProgress(app.id, InstallProgress.Phase.DOWNLOADING, 0.0)
        onProgress(placeholder)
        viewModelScope.launch {
            val result = repo.install(app, { p -> onProgress(p) }, tag, lenient = lenient, replacing = replacing)
            if (result == InstallResult.ALREADY_RUNNING) {
                // Lost a race with another install of this app: leave its progress alone, drop only our placeholder.
                _state.update { s -> if (s.progress[app.id] === placeholder) s.copy(progress = s.progress - app.id) else s }
                return@launch
            }
            refreshInstalledOnly()
            reloadHistory()
            _state.update {
                it.copy(
                    snackbar = when (result) {
                        InstallResult.CANCELLED ->
                            if (repo.settings.showLock) "Show lock is on — ${app.name} was not installed"
                            else "Install of ${app.name} cancelled"
                        InstallResult.INSTALLED -> "${app.name} installed"
                        else -> "${app.name} was not installed"
                    },
                )
            }
            if (result == InstallResult.INSTALLED) afterInstalled?.invoke()
        }
    }

    /** Stops [app]'s download (the Cancel button). */
    fun cancelInstall(app: CatalogApp) = repo.requestCancel(app.id)

    /** Update all: every installed, non-held app with an update (then the launcher itself, last). */
    fun updateAll() = startBatch(installAll = false)

    /**
     * Install every app: every app with an Android build that isn't installed yet, plus the pending updates. Android
     * shows its own install dialog for each first install; the batch waits for each one.
     */
    fun installAll() = startBatch(installAll = true)

    private fun startBatch(installAll: Boolean) {
        if (_state.value.busyAll || blockedByLock()) return
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.") }
            repo.openInstallPermissionSettings()
            return
        }
        stopRequested = false
        // Re-derived when the list runs out, so an update found (or a hold released) meanwhile is picked up too.
        val derive: () -> List<BatchSlot> = {
            val s = _state.value.statuses
            (if (installAll) BatchRules.installAllWork(s) else BatchRules.updateWork(s)).map(BatchRules::slotOf)
        }
        val work = derive()
        if (work.isEmpty() && (installAll || _state.value.launcherUpdate == null)) return
        batchIds.clear()
        batchIds.addAll(work.map { it.app.id })
        ActiveInstalls.queue(batchIds)   // the background check never announces what's being installed
        _state.update { it.copy(busyAll = true) }
        viewModelScope.launch {
            val count = try {
                repo.runBatch(
                    work, { stopRequested }, { p -> onProgress(p) }, refill = derive,
                    onAdded = { ids -> batchIds.addAll(ids); ActiveInstalls.addToQueue(ids) },
                    title = if (installAll) "Install every app" else "Update all",
                )
            } finally {
                // Also when the batch is cancelled (the screen closed): the process-wide queue must not keep these ids.
                batchIds.clear()
                ActiveInstalls.clearQueue()
            }
            refreshInstalledOnly()
            reloadHistory()
            _state.update {
                it.copy(
                    busyAll = false,
                    // An app that was already installing from its own row isn't counted either way.
                    snackbar = if (count.attempted == 0) it.snackbar
                    else if (installAll) "Installed ${count.done} of ${count.attempted}"
                    else "Updated ${count.done} of ${count.attempted}",
                )
            }
            // The launcher goes LAST: replacing it ends this process, so every app update must be done first.
            if (!installAll && _state.value.launcherUpdate != null && !stopRequested && !_state.value.showLock) runLauncherUpdate()
        }
    }

    /** Stop on Update all / an import: cancel the batch's download in flight and start nothing more. */
    fun stopAll() {
        stopRequested = true
        _state.value.progress.values
            .filter { it.phase == InstallProgress.Phase.DOWNLOADING && it.catalogId in batchIds }
            .forEach { repo.requestCancel(it.catalogId) }
    }

    // ── Hold ────────────────────────────────────────────────────────────────

    fun toggleHold(app: CatalogApp) {
        val held = repo.settings.heldApps
        val now = if (app.id in held) held - app.id else held + app.id
        repo.settings.heldApps = now
        _state.update { s ->
            s.copy(
                sheetFor = null,
                statuses = s.statuses.map { if (it.app.id == app.id) it.copy(held = it.isInstalled && app.id in now) else it },
                snackbar = if (app.id in now) "${app.name} is held at this version" else "${app.name} will update again",
            )
        }
    }

    // ── List layout: pin, hide, reorder, sections, view ────────────────────────

    private fun saveLayout(layout: LayoutPrefs, message: String? = null) {
        repo.settings.layout = layout
        _state.update { it.copy(layout = layout, sheetFor = null, sectionSheet = null, snackbar = message ?: it.snackbar) }
    }

    fun togglePin(app: CatalogApp) {
        val now = AppLayout.togglePin(_state.value.layout, app.id)
        saveLayout(now)
        AppLog.get(getApplication()).log("${if (app.id in now.pinned) "pinned" else "unpinned"} ${app.id}")
    }

    fun hideApp(app: CatalogApp) {
        val l = _state.value.layout
        saveLayout(l.copy(hidden = l.hidden + app.id), "${app.name} is hidden — About → App list shows it again")
        AppLog.get(getApplication()).log("hid ${app.id}")
    }

    /** "Show hidden apps (n)": every hidden app comes back. */
    fun showHiddenApps() {
        saveLayout(_state.value.layout.copy(hidden = emptySet()), "Hidden apps are shown again")
        AppLog.get(getApplication()).log("unhid all apps")
    }

    /** "Reset app order": the catalog's order again (pins, hidden apps and sections stay as they are). */
    fun resetAppOrder() {
        saveLayout(_state.value.layout.copy(order = emptyList()), "App order reset")
        AppLog.get(getApplication()).log("reset the app order")
    }

    /** Move up / Move down: past the nearest app on screen in the same section (paused while a filter is on). */
    fun moveApp(app: CatalogApp, up: Boolean) {
        val s = _state.value
        if (s.filtering) return
        val order = s.moved(app.id, up) ?: return
        repo.settings.layout = s.layout.copy(order = order)
        // The sheet stays open, so the app can move several places in a row.
        _state.update { it.copy(layout = it.layout.copy(order = order)) }
        AppLog.get(getApplication()).log("moved ${app.id} ${if (up) "up" else "down"}")
    }

    fun toggleSection(key: String) {
        val l = _state.value.layout
        val now = if (key in l.collapsed) l.collapsed - key else l.collapsed + key
        saveLayout(l.copy(collapsed = now))
    }

    fun showSectionSheet(key: String?) = _state.update { it.copy(sectionSheet = key) }

    /** Moves a category section one place up/down (Pinned always stays first). */
    fun moveSection(key: String, up: Boolean) {
        val s = _state.value
        if (s.filtering) return
        val displayed = s.groups().map { it.first.key }
        val order = AppLayout.moveSection(displayed, key, up) ?: return
        repo.settings.layout = s.layout.copy(categoryOrder = order)
        _state.update { it.copy(layout = it.layout.copy(categoryOrder = order)) }
        AppLog.get(getApplication()).log("reordered category sections")
    }

    fun setListView(on: Boolean) {
        repo.settings.listView = on
        _state.update { it.copy(listView = on) }
    }

    // ── Remove / open ───────────────────────────────────────────────────────

    fun confirmRemove(app: CatalogApp) {
        _state.update { it.copy(confirmRemove = null) }
        if (blockedByLock()) return
        val version = _state.value.statuses.firstOrNull { it.app.id == app.id }?.installedVersion
        synchronized(pendingRemovals) { pendingRemovals[app.id] = version }
        repo.remove(app.id)
    }

    fun openApp(app: CatalogApp) {
        _state.update { it.copy(sheetFor = null) }
        if (!repo.open(app.id)) {
            _state.update { it.copy(snackbar = "${app.name} isn’t installed") }
        } else {
            updateShortcuts()
        }
    }

    /**
     * Re-read of what's installed (no network) — after an install or a removal, and on every resume. The package
     * lookups and any history write run off the main thread.
     */
    fun refreshInstalledOnly() {
        viewModelScope.launch {
            val ids = repo.catalog.apps.map { it.id }
            val (installedNow, recorded) = withContext(Dispatchers.IO) {
                // A removal finishes in the system's own flow: record it once the package is really gone.
                val gone = synchronized(pendingRemovals) {
                    pendingRemovals.keys.filter { !repo.isInstalled(it) }.map { id -> id to pendingRemovals.remove(id) }
                }
                for ((id, from) in gone) {
                    val name = repo.catalog.apps.firstOrNull { it.id == id }?.name ?: id
                    repo.addHistory(id, name, "uninstall", from)
                }
                ids.associateWith { repo.installedVersionFor(it) } to gone.isNotEmpty()
            }
            val held = repo.settings.heldApps
            val previous = ids.mapNotNull { id -> repo.settings.previousVersion(id)?.let { id to it } }.toMap()
            _state.update { s ->
                s.copy(
                    previousVersions = previous,
                    statuses = s.statuses.map {
                        if (it.app.id !in installedNow) return@map it
                        val installed = installedNow[it.app.id]
                        it.copy(installedVersion = installed, held = installed != null && it.app.id in held)
                    },
                )
            }
            if (recorded) reloadHistory()
            updateShortcuts()
        }
    }

    /** Re-reads the activity history off the main thread. */
    private fun reloadHistory() {
        viewModelScope.launch {
            val history = withContext(Dispatchers.IO) { repo.history() }
            _state.update { it.copy(history = history) }
        }
    }

    /** The launcher-icon shortcuts decode app icons — off the main thread. */
    private fun updateShortcuts() {
        viewModelScope.launch(Dispatchers.IO) { repo.updateShortcuts() }
    }

    // ── Release notes ───────────────────────────────────────────────────────

    /** The in-app release notes for [app] (every release, newest first; "New since your version" marked). */
    fun showReleaseNotes(app: CatalogApp) {
        val installed = _state.value.statuses.firstOrNull { it.app.id == app.id }?.installedVersion
        _state.update { it.copy(sheetFor = null, notes = NotesSheet("${app.name} — release notes", installed)) }
        notesJob?.cancel()
        notesJob = viewModelScope.launch {
            val result = runCatching { repo.releasesFor(app) }
            ensureActive()   // closed, or another sheet opened, while loading: drop this result
            _state.update { s ->
                val sheet = s.notes ?: return@update s
                result.fold(
                    { list -> s.copy(notes = sheet.copy(loading = false, releases = sortNewestFirst(list), message = if (list.isEmpty()) "No releases yet." else null)) },
                    { e -> s.copy(notes = sheet.copy(loading = false, message = UserMessage.of(e))) },
                )
            }
        }
    }

    fun closeNotes() {
        notesJob?.cancel()
        notesJob = null
        _state.update { it.copy(notes = null) }
    }

    private fun sortNewestFirst(list: List<ReleaseInfo>): List<ReleaseInfo> =
        list.sortedWith { a, b -> VersionCompare.compare(b.tagName, a.tagName) }

    // ── The launcher's own "what's new", once after it has been updated ────

    private fun prepareLauncherWhatsNew() {
        val last = repo.settings.lastSeenLauncherVersion
        val cur = launcherVersion
        if (LauncherWhatsNew.shouldShow(last, cur, existingInstall = last.isBlank() && repo.launcherWasUpdated())) {
            _state.update { it.copy(launcherWhatsNew = cur) }
        } else if (!VersionCompare.equal(last, cur)) {
            repo.settings.lastSeenLauncherVersion = cur
        }
    }

    fun dismissLauncherWhatsNew() {
        repo.settings.lastSeenLauncherVersion = launcherVersion
        _state.update { it.copy(launcherWhatsNew = null) }
    }

    fun showLauncherWhatsNew() {
        val since = repo.settings.lastSeenLauncherVersion
        dismissLauncherWhatsNew()
        _state.update { it.copy(notes = NotesSheet("JB Theatre Tools — what's new", launcherVersion)) }
        notesJob?.cancel()
        notesJob = viewModelScope.launch {
            val list = runCatching { repo.launcherReleasesSince(since) }.getOrDefault(emptyList())
            ensureActive()
            _state.update { s ->
                val sheet = s.notes ?: return@update s
                s.copy(notes = sheet.copy(
                    loading = false, releases = sortNewestFirst(list),
                    message = if (list.isEmpty()) (repo.launcherWhatsNewLine() ?: "No notes for this release.") else null,
                ))
            }
        }
    }

    // ── Settings: scheduled checks, notifications ──────────────────────────

    fun setAutoCheckInterval(raw: String) {
        repo.settings.autoCheckInterval = raw
        _state.update { it.copy(autoCheckInterval = raw) }
        syncBackgroundChecks()
    }

    /** "Install updates automatically" on/off (Android 12+). Switching it on updates what's waiting straight away. */
    fun setAutoInstallUpdates(on: Boolean) {
        if (!AutoUpdatePolicy.available(android.os.Build.VERSION.SDK_INT)) return
        repo.settings.autoInstallUpdates = on
        _state.update { it.copy(autoInstallUpdates = on) }
        syncBackgroundChecks()
        if (on) runAutoUpdate()
    }

    /** Notifications on/off. The screen asks for the Android 13+ permission first and passes the answer here. */
    fun setNotifyUpdates(on: Boolean) {
        repo.settings.notifyUpdates = on && notifier.canPost()
        // The channel exists from the moment notifications are on, so it can be set up before the first one.
        if (repo.settings.notifyUpdates) notifier.ensureChannel()
        _state.update {
            it.copy(
                notifyUpdates = repo.settings.notifyUpdates,
                snackbar = if (on && !notifier.canPost())
                    "Notifications are off for JB Theatre Tools — allow them in Android Settings → Apps → JB Theatre Tools"
                else it.snackbar,
            )
        }
        syncBackgroundChecks()
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    /** About's measured bits — storage sizes and the log's last lines — read off the main thread. */
    fun refreshStorage() {
        viewModelScope.launch {
            val (sizes, tail) = withContext(Dispatchers.IO) {
                (repo.installedBytes() to repo.cacheBytes()) to repo.logText().lines().filter { it.isNotBlank() }.takeLast(6).joinToString("\n")
            }
            _state.update { it.copy(storage = sizes, logTail = tail) }
        }
    }

    fun clearCache() {
        if (blockedByLock()) return
        if (_state.value.anyInstallRunning) {
            _state.update { it.copy(snackbar = "Wait for the installs to finish, then clear the cache.") }
            return
        }
        viewModelScope.launch {
            withContext(Dispatchers.IO) { repo.clearCache() }
            refreshStorage()
            _state.update { it.copy(snackbar = "Download cache cleared") }
        }
    }

    // ── Setup files ─────────────────────────────────────────────────────────

    /** Writes this device's setup (installed apps, versions, holds, the list layout) to [uri] (from the system file picker). */
    fun exportSetup(uri: Uri) {
        val statuses = _state.value.statuses.filter { it.isInstalled }
        if (statuses.isEmpty()) {
            _state.update { it.copy(snackbar = "No apps are installed yet, so there's nothing to export.") }
            return
        }
        val held = repo.settings.heldApps
        val profile = SetupProfile(
            createdAt = SetupProfile.timestamp(Instant.now()),
            createdBy = "JB Theatre Tools $launcherVersion (Android)",
            apps = statuses.map { SetupProfile.Entry(it.app.id, null, it.installedVersion, it.app.id in held) },
            // The shared `layout` section, so any launcher can take this device's pins, hidden apps and order.
            layout = AppLayout.toProfile(
                repo.settings.layout, repo.catalog.apps.map { it.id }, repo.catalog.categories,
                repo.catalog.apps.map { AppLayout.categoryOf(it.category) },
            ),
        )
        viewModelScope.launch {
            val result = runCatching {
                withContext(Dispatchers.IO) {
                    getApplication<Application>().contentResolver.openOutputStream(uri, "wt")?.use {
                        it.write(profile.serialize().toByteArray(Charsets.UTF_8))
                    } ?: error("couldn't open the file")
                }
            }
            result.exceptionOrNull()?.let { e -> AppLog.get(getApplication()).log("export setup: ${e.javaClass.simpleName}: ${e.message}") }
            _state.update {
                it.copy(snackbar = result.fold({ "Saved ${statuses.size} installed app${if (statuses.size == 1) "" else "s"}" },
                                               { "Couldn't save the setup file." }))
            }
        }
    }

    /** Reads a setup file and shows the preview (nothing installs until [confirmImport]). */
    fun previewImport(uri: Uri) {
        if (blockedByLock()) return
        viewModelScope.launch {
            // Reading, parsing and planning all run off the main thread (a setup file can be up to 1 MB).
            val result = runCatching {
                withContext(Dispatchers.IO) {
                    val text = getApplication<Application>().contentResolver.openInputStream(uri)?.use { input ->
                        val bytes = readCapped(input, SetupProfile.MAX_BYTES)
                        if (bytes.size > SetupProfile.MAX_BYTES) throw SetupProfile.FormatError("This file is too large to be a setup file.")
                        String(bytes, Charsets.UTF_8)
                    } ?: throw SetupProfile.FormatError("That file couldn't be opened.")
                    val profile = SetupProfile.parse(text)
                    val catalog = repo.catalog.apps.map { a -> SetupPlanner.CatalogEntry(a.id, a.name, a.variants.map { it.id to it.label }) }
                    val installedKeys = _state.value.statuses.filter { it.isInstalled }.map { it.app.id }.toSet()
                    // A development build named in the file installs only with Development builds on here.
                    val plan = SetupPlanner.build(
                        profile, catalog, installedKeys, supportsVariants = false, allowDevTags = repo.settings.devChannel,
                    )
                    ImportPreview(SetupPlanner.summary(plan, SetupPlanner.Wording.ANDROID), plan, profile.layout)
                }
            }
            _state.update {
                result.fold({ p -> it.copy(importPreview = p) }, { e ->
                    it.copy(snackbar = if (e is SetupProfile.FormatError) e.message else "That file couldn't be read.")
                })
            }
        }
    }

    /** Reads at most a little past [limit] bytes (InputStream.readNBytes is Android 13+ only; minSdk is 29). */
    private fun readCapped(input: java.io.InputStream, limit: Int): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        val buf = ByteArray(8192)
        while (out.size() <= limit) {
            val n = input.read(buf)
            if (n < 0) break
            out.write(buf, 0, n)
        }
        return out.toByteArray()
    }

    fun cancelImport() = _state.update { it.copy(importPreview = null) }

    /** The preview's "Also use this file's list layout" choice. */
    fun setImportApplyLayout(on: Boolean) = _state.update { s -> s.copy(importPreview = s.importPreview?.copy(applyLayout = on)) }

    /** Applies the previewed setup: holds first, then each missing app, one at a time (Stop ends the run). */
    fun confirmImport() {
        val preview = _state.value.importPreview ?: return
        _state.update { it.copy(importPreview = null) }
        if (blockedByLock()) return
        repo.settings.heldApps = repo.settings.heldApps + preview.plan.holdIds
        if (preview.applyLayout && preview.layout != null) {
            val layout = AppLayout.fromProfile(preview.layout, repo.catalog.apps.map { it.id }.toSet())
            repo.settings.layout = layout
            _state.update { it.copy(layout = layout) }
            AppLog.get(getApplication()).log("import setup: the file's list layout applied")
        }
        refreshInstalledOnly()
        if (preview.plan.toInstall.isEmpty()) {
            _state.update { it.copy(snackbar = "Setup applied — nothing new to install") }
            return
        }
        if (_state.value.busyAll) {
            _state.update { it.copy(snackbar = "Holds applied — wait for the running updates to finish, then import again to install the apps.") }
            return
        }
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then import again.") }
            repo.openInstallPermissionSettings()
            return
        }
        stopRequested = false
        // Installed from its own row since the preview: the import leaves installed apps as they are. The repository
        // refuses a development build here unless Development builds are on (the preview already skipped them).
        val work = preview.plan.toInstall.mapNotNull { item ->
            repo.catalog.apps.firstOrNull { it.id == item.appId }?.let { BatchSlot(it, item.tag, onlyIfMissing = true) }
        }
        batchIds.clear()
        batchIds.addAll(work.map { it.app.id })
        ActiveInstalls.queue(batchIds)
        _state.update { it.copy(busyAll = true) }
        viewModelScope.launch {
            val count = try {
                repo.runBatch(work, { stopRequested }, { p -> onProgress(p) }, title = "import setup")
            } finally {
                batchIds.clear()
                ActiveInstalls.clearQueue()
            }
            refreshInstalledOnly()
            reloadHistory()
            _state.update {
                it.copy(busyAll = false, snackbar = if (count.attempted == 0) it.snackbar else "Installed ${count.done} of ${count.attempted} from the setup file")
            }
        }
    }

    // ── Diagnostics ─────────────────────────────────────────────────────────

    /** Builds the support report off the main thread (it reads the log), then hands it to [onReady] on the main thread. */
    fun shareDiagnostics(onReady: (String) -> Unit) {
        viewModelScope.launch { onReady(withContext(Dispatchers.IO) { diagnostics() }) }
    }

    /** Reads the log off the main thread, then hands it to [onReady]. */
    fun shareLog(onReady: (String) -> Unit) {
        viewModelScope.launch { onReady(withContext(Dispatchers.IO) { repo.logText() }) }
    }

    /** The support report (no secrets). Reads the log file: call it off the main thread. */
    private fun diagnostics(): String {
        val s = _state.value
        val relayHost = if (repo.settings.authMode == AuthMode.SERVER) {
            runCatching { java.net.URL(repo.relayBase() ?: "").host }.getOrNull()
        } else null
        return Diagnostics.build(Diagnostics.Info(
            launcherVersion = launcherVersion,
            os = "Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT})",
            arch = Build.SUPPORTED_ABIS.firstOrNull() ?: "unknown",
            authMode = when (repo.settings.authMode) {
                AuthMode.TOKEN -> "GitHub token"
                AuthMode.DEBUG_FEED -> "Debug feed"
                AuthMode.SERVER -> "Download server"
            },
            relayHost = relayHost,
            devChannel = s.devChannel,
            showLock = s.showLock,
            installLocation = "Android package manager",
            apps = s.statuses.map {
                Diagnostics.AppLine(
                    it.app.name, it.installedVersion, it.latestVersion,
                    when {
                        it.updatePending -> "update available"
                        it.hasUpdate -> "update available (held)"
                        it.isInstalled -> "installed"
                        it.canInstall -> "not installed"
                        else -> it.note ?: "no release"
                    },
                    it.held,
                )
            },
            logTail = repo.logText().lines().filter { it.isNotBlank() },
            now = Instant.now(),
        ))
    }

    fun releaseNotesUrl(app: CatalogApp): String = repo.releaseNotesUrl(app)

    // ── Dev channel (hidden; the maintainer's devices) ──────────────────────────────

    private val versionTaps = ArrayDeque<Long>()

    /** Seven taps within three seconds on the About version line reveal "Development builds" for this session. */
    fun tapVersion() {
        if (_state.value.devRevealed) return
        val now = android.os.SystemClock.elapsedRealtime()
        versionTaps.addLast(now)
        while (versionTaps.isNotEmpty() && now - versionTaps.first() >= 3_000) versionTaps.removeFirst()
        if (versionTaps.size >= 7) {
            _state.update { it.copy(devRevealed = true, snackbar = "Development builds can now be switched on — scroll down, after Recent activity") }
        }
    }

    // ── Other versions: back to release, roll back, install version ─────────

    /** Back to release: a dev build replaced by the latest release (Development builds switched off). */
    fun askBackToRelease(app: CatalogApp?) {
        if (app == null) { cancelReinstall(); return }
        if (blockedByLock()) return
        val status = _state.value.statuses.firstOrNull { it.app.id == app.id } ?: return
        val target = status.latestVersion ?: return
        _state.update {
            it.copy(sheetFor = null, confirmReinstall = ReinstallRequest(
                app, null, target, status.installedVersion, ReinstallRequest.Kind.BACK_TO_RELEASE, needsRemoval = true,
            ))
        }
    }

    /** "Roll back to vX…": asks first (it's a downgrade — Android removes the app first — and the app is held after). */
    fun askRollBack(app: CatalogApp, tag: String) {
        if (blockedByLock()) return
        val installed = _state.value.statuses.firstOrNull { it.app.id == app.id }?.installedVersion ?: return
        _state.update {
            it.copy(sheetFor = null, confirmReinstall = ReinstallRequest(
                app, tag, VersionCompare.norm(tag), installed, ReinstallRequest.Kind.ROLL_BACK,
                needsRemoval = PreviousVersion.needsRemoval(installed, tag),
            ))
        }
    }

    /** "Install version…": the app's releases with an Android build, newest first. */
    fun showVersions(app: CatalogApp) {
        if (blockedByLock()) return
        val installed = _state.value.statuses.firstOrNull { it.app.id == app.id }?.installedVersion
        _state.update { it.copy(sheetFor = null, versions = VersionsSheet(app, installed)) }
        loadReleases(app) { list, error ->
            _state.update { s ->
                val sheet = s.versions?.takeIf { it.app.id == app.id } ?: return@update s
                val offered = list?.let { PreviousVersion.installable(app, it) }.orEmpty()
                s.copy(versions = sheet.copy(
                    loading = false, releases = offered,
                    message = when {
                        error != null -> UserMessage.of(error)
                        offered.isEmpty() -> "No release of ${app.name} has an Android build yet."
                        else -> null
                    },
                ))
            }
        }
    }

    fun closeVersions() = _state.update { it.copy(versions = null) }

    /**
     * A version picked from "Install version". The only install that may go ahead without a signed checksum list (an
     * old release that predates it); its certificate is still checked. An older version than the one installed asks
     * first, as Android needs the app removed before it can go back.
     */
    fun pickVersion(app: CatalogApp, tag: String) {
        _state.update { it.copy(versions = null) }
        if (blockedByLock()) return
        val installed = _state.value.statuses.firstOrNull { it.app.id == app.id }?.installedVersion
        if (PreviousVersion.needsRemoval(installed, tag)) {
            _state.update {
                it.copy(confirmReinstall = ReinstallRequest(
                    app, tag, VersionCompare.norm(tag), installed, ReinstallRequest.Kind.PICKED, needsRemoval = true,
                ))
            }
        } else {
            startInstall(app, tag, lenient = true)
        }
    }

    fun cancelReinstall() = _state.update { it.copy(confirmReinstall = null) }

    /** The confirm sheet's go-ahead: remove first when Android needs it, then install; a roll back is then held. */
    fun confirmReinstall(request: ReinstallRequest) {
        _state.update { it.copy(confirmReinstall = null) }
        if (blockedByLock()) return
        val lenient = request.kind == ReinstallRequest.Kind.PICKED
        val after: (() -> Unit)? = if (request.kind != ReinstallRequest.Kind.ROLL_BACK) null else { { holdAfterRollBack(request) } }
        if (request.needsRemoval) removeThenInstall(request.app, request.tag, request.from, lenient, after)
        else startInstall(request.app, request.tag, lenient, afterInstalled = after)
    }

    /** Rolled back: held at that version so Update all and automatic updates leave it alone (the desktop behaviour). */
    private fun holdAfterRollBack(request: ReinstallRequest) = viewModelScope.launch {
        val now = withContext(Dispatchers.IO) { repo.installedVersionFor(request.app.id) } ?: return@launch
        if (!VersionCompare.equal(now, request.target)) return@launch
        if (request.app.id !in repo.settings.heldApps) repo.settings.heldApps = repo.settings.heldApps + request.app.id
        AppLog.get(getApplication()).log("rolled back ${request.app.id} to ${request.target} (held)")
        _state.update { s ->
            s.copy(
                statuses = s.statuses.map { if (it.app.id == request.app.id) it.copy(held = true) else it },
                snackbar = "${request.app.name} rolled back to ${VersionCompare.display(request.target)} and held there",
            )
        }
        refreshInstalledOnly()
    }

    /**
     * Android refuses to install an OLDER version over a newer one, so the app is removed first (the system asks to
     * confirm), then [tag] installs as soon as the package is gone. Gives up quietly if the removal is cancelled.
     */
    private fun removeThenInstall(app: CatalogApp, tag: String?, from: String?, lenient: Boolean, afterInstalled: (() -> Unit)?) {
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.") }
            repo.openInstallPermissionSettings()
            return
        }
        synchronized(pendingRemovals) { pendingRemovals[app.id] = from }
        repo.remove(app.id)
        viewModelScope.launch {
            for (i in 0 until 240) {                     // up to 2 minutes for the system uninstall dialog
                kotlinx.coroutines.delay(500)
                if (!withContext(Dispatchers.IO) { repo.isInstalled(app.id) }) break
            }
            refreshInstalledOnly()
            if (withContext(Dispatchers.IO) { repo.isInstalled(app.id) }) {
                _state.update { it.copy(snackbar = "${app.name} was not removed, so it's still at ${from?.let(VersionCompare::display) ?: "its version"}") }
                return@launch
            }
            startInstall(app, tag, lenient, replacing = from, afterInstalled = afterInstalled)
        }
    }

    fun setDevChannel(on: Boolean) {
        repo.settings.devChannel = on
        _state.update { it.copy(devChannel = on) }
        refresh()
    }
}
