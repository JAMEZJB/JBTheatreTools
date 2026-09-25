package com.jamesbreedon.jbtheatretools.ui

import android.app.Application
import android.net.Uri
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.jamesbreedon.jbtheatretools.core.ActivityEvent
import com.jamesbreedon.jbtheatretools.core.AppFilter
import com.jamesbreedon.jbtheatretools.core.AppVisibility
import com.jamesbreedon.jbtheatretools.core.Appearance
import com.jamesbreedon.jbtheatretools.core.AppStatus
import com.jamesbreedon.jbtheatretools.core.AuthMode
import com.jamesbreedon.jbtheatretools.core.CatalogApp
import com.jamesbreedon.jbtheatretools.core.Diagnostics
import com.jamesbreedon.jbtheatretools.core.InstallProgress
import com.jamesbreedon.jbtheatretools.core.LauncherRepository
import com.jamesbreedon.jbtheatretools.core.LauncherWhatsNew
import com.jamesbreedon.jbtheatretools.core.Notifier
import com.jamesbreedon.jbtheatretools.core.SetupPlanner
import com.jamesbreedon.jbtheatretools.core.SetupProfile
import com.jamesbreedon.jbtheatretools.core.StatusFilter
import com.jamesbreedon.jbtheatretools.core.UpdateAnnouncer
import com.jamesbreedon.jbtheatretools.core.UpdateCheckScheduler
import com.jamesbreedon.jbtheatretools.core.UpdatePolicy
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
    val autoCheckInterval: String = UpdatePolicy.DEFAULT_INTERVAL,
    val notifyUpdates: Boolean = true,
    /** Set to this build's version on the first launch after the launcher was updated (the banner). */
    val launcherWhatsNew: String? = null,
    val notes: NotesSheet? = null,
    val importPreview: ImportPreview? = null,
    val history: List<ActivityEvent> = emptyList(),
    /** Installed apps' size and the download cache's size (About → Storage), or null until measured. */
    val storage: Pair<Long, Long>? = null,
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
            history = repo.history(),
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
    /** Apps handed to the system uninstaller, with the version they had — recorded once they're gone. */
    private val pendingRemovals = HashMap<String, String?>()

    init {
        AppVisibility.foreground = true   // the view model is created with the activity, i.e. on screen
        prepareLauncherWhatsNew()
        if (repo.hasCredential()) refresh()
        updateShortcuts()
        syncBackgroundChecks()
        // "While open, check every…": one tick a minute; a check runs when it's due, the launcher is on screen and
        // nothing else is happening. (Closed, the background job takes over when notifications are on.)
        viewModelScope.launch {
            while (true) {
                delay(60_000)
                val s = _state.value
                if (AppVisibility.foreground && s.signedIn && !s.loading && !s.anyInstallRunning &&
                    UpdatePolicy.isDue(lastCheck, Instant.now(), repo.settings.autoCheckInterval)
                ) refresh()
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

    fun refresh() {
        if (_state.value.loading) return
        _state.update { it.copy(loading = true) }
        viewModelScope.launch {
            val statuses = repo.refresh()
            val launcherUpdate = repo.checkLauncherUpdate()
            lastCheck = Instant.now()
            _state.update {
                it.copy(loading = false, statuses = statuses, signedIn = repo.hasCredential(), launcherUpdate = launcherUpdate)
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
        val ok = repo.installLauncher { p -> onProgress(p) }
        // Only reached when Android did NOT replace us (refused, cancelled or failed verification).
        if (!ok) _state.update { it.copy(snackbar = "JB Theatre Tools was not updated") }
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

    fun setShowLock(on: Boolean) {
        repo.settings.showLock = on
        _state.update { it.copy(showLock = on, sheetFor = null) }
        if (on && _state.value.busyAll) stopAll()   // nothing more installs once the lock is on
    }

    /** The model-level guard behind every install / update / removal (the UI hides them too). */
    private fun blockedByLock(): Boolean {
        if (!_state.value.showLock) return false
        _state.update { it.copy(snackbar = "Show lock is on — unlock it in About to install, update or remove apps.") }
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
        viewModelScope.launch {
            var cancelled = false
            val ok = repo.install(app, { p ->
                if (p.phase == InstallProgress.Phase.CANCELLED) cancelled = true
                onProgress(p)
            }, tag)
            refreshInstalledOnly()
            _state.update {
                it.copy(
                    snackbar = when {
                        cancelled -> "Download of ${app.name} cancelled"
                        ok -> "${app.name} installed"
                        else -> "${app.name} was not installed"
                    },
                    history = repo.history(),
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
        _state.update { it.copy(busyAll = true) }
        viewModelScope.launch {
            val done = repo.updateAll(pending, { stopRequested }) { p -> onProgress(p) }
            batchIds.clear()
            refreshInstalledOnly()
            _state.update {
                it.copy(
                    busyAll = false, history = repo.history(),
                    snackbar = if (pending.isEmpty()) it.snackbar else "Updated $done of ${pending.size}",
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
        pendingRemovals[app.id] = repo.installedVersionFor(app.id)
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

    /** Cheap re-read of what's installed (no network) — after an install or a removal. */
    fun refreshInstalledOnly() {
        // A removal finishes in the system's own flow: record it once the package is really gone.
        val gone = pendingRemovals.keys.filter { !repo.isInstalled(it) }
        for (id in gone) {
            val from = pendingRemovals.remove(id)
            val name = repo.catalog.apps.firstOrNull { it.id == id }?.name ?: id
            repo.addHistory(id, name, "uninstall", from)
        }
        val held = repo.settings.heldApps
        _state.update { s ->
            s.copy(
                statuses = s.statuses.map {
                    val installed = repo.installedVersionFor(it.app.id)
                    it.copy(installedVersion = installed, held = installed != null && it.app.id in held)
                },
                history = if (gone.isEmpty()) s.history else repo.history(),
            )
        }
        updateShortcuts()
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
                    { e -> s.copy(notes = sheet.copy(loading = false, message = e.message ?: "Couldn't load the release notes.")) },
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
        _state.update {
            it.copy(
                notifyUpdates = repo.settings.notifyUpdates,
                snackbar = if (on && !notifier.canPost()) "Notifications are off for JB Theatre Tools in Android settings" else it.snackbar,
            )
        }
        syncBackgroundChecks()
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    fun refreshStorage() {
        viewModelScope.launch {
            val sizes = withContext(Dispatchers.IO) { repo.installedBytes() to repo.cacheBytes() }
            _state.update { it.copy(storage = sizes) }
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
            _state.update {
                it.copy(snackbar = result.fold({ "Saved ${statuses.size} installed app${if (statuses.size == 1) "" else "s"}" },
                                               { e -> "Couldn't save the setup: ${e.message}" }))
            }
        }
    }

    /** Reads a setup file and shows the preview (nothing installs until [confirmImport]). */
    fun previewImport(uri: Uri) {
        if (blockedByLock()) return
        viewModelScope.launch {
            val result = runCatching {
                val text = withContext(Dispatchers.IO) {
                    getApplication<Application>().contentResolver.openInputStream(uri)?.use { input ->
                        val bytes = readCapped(input, SetupProfile.MAX_BYTES)
                        if (bytes.size > SetupProfile.MAX_BYTES) throw SetupProfile.FormatError("This file is too large to be a setup file.")
                        String(bytes, Charsets.UTF_8)
                    } ?: error("couldn't open the file")
                }
                val profile = SetupProfile.parse(text)
                val catalog = repo.catalog.apps.map { a -> SetupPlanner.CatalogEntry(a.id, a.name, a.variants.map { it.id to it.label }) }
                val installedKeys = _state.value.statuses.filter { it.isInstalled }.map { it.app.id }.toSet()
                val plan = SetupPlanner.build(profile, catalog, installedKeys, supportsVariants = false)
                ImportPreview(SetupPlanner.summary(plan), plan)
            }
            _state.update {
                result.fold({ p -> it.copy(importPreview = p) }, { e -> it.copy(snackbar = e.message ?: "That file couldn't be read.") })
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
        _state.update { it.copy(busyAll = true) }
        viewModelScope.launch {
            var done = 0
            for (item in preview.plan.toInstall) {
                if (stopRequested) break
                val app = repo.catalog.apps.firstOrNull { it.id == item.appId } ?: continue
                if (repo.install(app, { p -> onProgress(p) }, item.tag, stop = { stopRequested })) done++
            }
            batchIds.clear()
            refreshInstalledOnly()
            _state.update {
                it.copy(busyAll = false, history = repo.history(), snackbar = "Installed $done of ${preview.plan.toInstall.size} from the setup file")
            }
        }
    }

    // ── Diagnostics ─────────────────────────────────────────────────────────

    /** The support report (no secrets) for the share sheet. */
    fun diagnostics(): String {
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
            _state.update { it.copy(devRevealed = true, snackbar = "Development builds can be switched on below") }
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
        pendingRemovals[app.id] = repo.installedVersionFor(app.id)
        repo.remove(app.id)
        viewModelScope.launch {
            for (i in 0 until 240) {                     // up to 2 minutes for the system uninstall dialog
                kotlinx.coroutines.delay(500)
                if (!repo.isInstalled(app.id)) break
            }
            refreshInstalledOnly()
            if (repo.isInstalled(app.id)) {
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
