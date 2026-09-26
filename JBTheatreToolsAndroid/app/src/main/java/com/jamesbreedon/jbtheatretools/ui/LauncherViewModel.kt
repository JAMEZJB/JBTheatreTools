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
import com.jamesbreedon.jbtheatretools.core.AppVisibility
import com.jamesbreedon.jbtheatretools.core.Appearance
import com.jamesbreedon.jbtheatretools.core.AppStatus
import com.jamesbreedon.jbtheatretools.core.AuthMode
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

/** The preview before a setup file is applied. */
data class ImportPreview(val summary: String, val plan: SetupPlanner.Plan)

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
    val confirmBackToRelease: CatalogApp? = null,   // rule-8 confirm (it removes the app first)
    val devChannel: Boolean = false,
    /** Show lock: installs, updates and removals are paused; opening apps still works. */
    val showLock: Boolean = false,
    /** "Turn off show lock?" is showing (turning it off always asks first). */
    val confirmShowLockOff: Boolean = false,
    val autoCheckInterval: String = UpdatePolicy.DEFAULT_INTERVAL,
    val notifyUpdates: Boolean = true,
    /** Set to this build's version on the first launch after the launcher was updated (the banner). */
    val launcherWhatsNew: String? = null,
    val notes: NotesSheet? = null,
    val importPreview: ImportPreview? = null,
    val history: List<ActivityEvent> = emptyList(),
    /** Installed apps' size and the download cache's size (About → Storage), or null until measured. */
    val storage: Pair<Long, Long>? = null,
    /** The log's last lines (About → Log), read off the main thread. */
    val logTail: String = "",
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

    /** The Apps list after Find & filter: the query matches name, blurb, category or id; plus the status chips. */
    fun visibleStatuses(): List<AppStatus> = statuses.filter { !it.hidden }.filter {
        AppFilter.matchesQuery(search, it.app.name, it.app.blurb, it.app.category, it.app.id) &&
            AppFilter.matchesStatus(statusFilter, it.isInstalled, it.updatePending, !it.isInstalled && it.canInstall)
    }
}

class LauncherViewModel(app: Application) : AndroidViewModel(app) {

    val repo = LauncherRepository(app)
    private val notifier = Notifier(app)

    private val _state = MutableStateFlow(
        LauncherUiState(
            signedIn = repo.hasCredential(),
            appearance = repo.settings.appearance,
            statuses = repo.catalog.apps.map { AppStatus(it) },
            devRevealed = repo.settings.devChannel,
            devChannel = repo.settings.devChannel,
            showLock = repo.settings.showLock,
            autoCheckInterval = repo.settings.autoCheckInterval,
            notifyUpdates = repo.settings.notifyUpdates && Notifier(app).canPost(),
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
                if (AppVisibility.foreground && s.signedIn && !s.loading && !s.anyInstallRunning &&
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

    /** The background check runs only when notifications are on and allowed (it has nothing else to do). */
    private fun syncBackgroundChecks() =
        UpdateCheckScheduler.sync(getApplication(), repo.settings, repo.hasCredential() && notifier.canPost())

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
                    // The launcher's check reports "no update" and "failed" alike; only a restart clears a found one.
                    launcherUpdate = if (scheduled) launcherUpdate ?: s.launcherUpdate else launcherUpdate,
                )
            }
            announceNewUpdates()
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

    fun showSheet(app: CatalogApp?) = _state.update { it.copy(sheetFor = app) }

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
            it.copy(signedIn = false, statuses = repo.catalog.apps.map { a -> AppStatus(a) })
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

    fun install(app: CatalogApp, tag: String? = null) {
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
            val result = repo.install(app, { p -> onProgress(p) }, tag)
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
        }
    }

    /** Stops [app]'s download (the Cancel button). */
    fun cancelInstall(app: CatalogApp) = repo.requestCancel(app.id)

    fun updateAll() {
        if (_state.value.busyAll || blockedByLock()) return
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.") }
            repo.openInstallPermissionSettings()
            return
        }
        stopRequested = false
        val pending = _state.value.statuses.filter { it.updatePending && it.canInstall }
        batchIds.clear()
        batchIds.addAll(pending.map { it.app.id })
        ActiveInstalls.queue(batchIds)   // the background check never announces what's being installed
        _state.update { it.copy(busyAll = true) }
        viewModelScope.launch {
            val count = try {
                repo.updateAll(pending, { stopRequested }) { p -> onProgress(p) }
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
                    snackbar = if (count.attempted == 0) it.snackbar else "Updated ${count.done} of ${count.attempted}",
                )
            }
            // The launcher goes LAST: replacing it ends this process, so every app update must be done first.
            if (_state.value.launcherUpdate != null && !stopRequested && !_state.value.showLock) runLauncherUpdate()
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
            _state.update { s ->
                s.copy(
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

    /** Writes this device's setup (installed apps, versions, holds) to [uri] (from the system file picker). */
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
                    ImportPreview(SetupPlanner.summary(plan, SetupPlanner.Wording.ANDROID), plan)
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

    /** Applies the previewed setup: holds first, then each missing app, one at a time (Stop ends the run). */
    fun confirmImport() {
        val preview = _state.value.importPreview ?: return
        _state.update { it.copy(importPreview = null) }
        if (blockedByLock()) return
        repo.settings.heldApps = repo.settings.heldApps + preview.plan.holdIds
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
        batchIds.clear()
        batchIds.addAll(preview.plan.toInstall.map { it.appId })
        ActiveInstalls.queue(batchIds)
        _state.update { it.copy(busyAll = true) }
        viewModelScope.launch {
            var done = 0
            var attempted = 0
            try {
                for (item in preview.plan.toInstall) {
                    if (stopRequested) break
                    val app = repo.catalog.apps.firstOrNull { it.id == item.appId } ?: continue
                    // Installed from its own row since the preview: the import leaves installed apps as they are.
                    if (repo.isInstalled(app.id)) continue
                    if (!repo.isInstalling(app.id)) repo.clearCancel(app.id)
                    // The repository refuses a development build here unless Development builds are on (the preview
                    // already skipped them; this covers the switch being turned off in between).
                    when (repo.install(app, { p -> onProgress(p) }, item.tag, stop = { stopRequested })) {
                        InstallResult.INSTALLED -> { done++; attempted++ }
                        InstallResult.ALREADY_RUNNING -> Unit   // installing from its own row: not a failure
                        else -> attempted++
                    }
                }
            } finally {
                batchIds.clear()
                ActiveInstalls.clearQueue()
            }
            refreshInstalledOnly()
            reloadHistory()
            _state.update {
                it.copy(busyAll = false, snackbar = if (attempted == 0) it.snackbar else "Installed $done of $attempted from the setup file")
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

    fun askBackToRelease(app: CatalogApp?) {
        if (app != null && blockedByLock()) return
        _state.update { it.copy(confirmBackToRelease = app, sheetFor = null) }
    }

    /**
     * Replace an installed dev build with the release. Android refuses to install an OLDER version over a
     * newer one, so the app is removed first (the system asks to confirm), then the release installs as soon
     * as the package is gone. Gives up quietly if the removal is cancelled.
     */
    fun backToRelease(app: CatalogApp) {
        _state.update { it.copy(confirmBackToRelease = null) }
        if (blockedByLock()) return
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.") }
            repo.openInstallPermissionSettings()
            return
        }
        val version = _state.value.statuses.firstOrNull { it.app.id == app.id }?.installedVersion
        synchronized(pendingRemovals) { pendingRemovals[app.id] = version }
        repo.remove(app.id)
        viewModelScope.launch {
            for (i in 0 until 240) {                     // up to 2 minutes for the system uninstall dialog
                kotlinx.coroutines.delay(500)
                if (!withContext(Dispatchers.IO) { repo.isInstalled(app.id) }) break
            }
            refreshInstalledOnly()
            if (withContext(Dispatchers.IO) { repo.isInstalled(app.id) }) {
                _state.update { it.copy(snackbar = "${app.name} was not removed, so it's still on the dev build") }
                return@launch
            }
            install(app)
        }
    }

    fun setDevChannel(on: Boolean) {
        repo.settings.devChannel = on
        _state.update { it.copy(devChannel = on) }
        refresh()
    }
}
