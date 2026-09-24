package com.jamesbreedon.jbtheatretools.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.jamesbreedon.jbtheatretools.core.Appearance
import com.jamesbreedon.jbtheatretools.core.AppStatus
import com.jamesbreedon.jbtheatretools.core.AuthMode
import com.jamesbreedon.jbtheatretools.core.CatalogApp
import com.jamesbreedon.jbtheatretools.core.InstallProgress
import com.jamesbreedon.jbtheatretools.core.LauncherRepository
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Which of the three bottom-nav destinations is showing (§7). */
enum class Tab { APPS, UPDATES, ABOUT }

data class LauncherUiState(
    val signedIn: Boolean = false,
    val loading: Boolean = false,
    val statuses: List<AppStatus> = emptyList(),
    val progress: Map<String, InstallProgress> = emptyMap(),
    val tab: Tab = Tab.APPS,
    val search: String? = null,               // null = the search field is closed
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
) {
    val installedCount: Int get() = statuses.count { it.isInstalled }
    /** Apps with an update. */
    val appUpdateCount: Int get() = statuses.count { it.hasUpdate }
    /** Everything with an update — the apps plus the launcher itself (status row, nav badge, Update all). */
    val updateCount: Int get() = appUpdateCount + (if (launcherUpdate != null) 1 else 0)

    fun visibleStatuses(): List<AppStatus> {
        val shown = statuses.filter { !it.hidden }
        val q = search?.trim()?.lowercase().orEmpty()
        if (q.isEmpty()) return shown
        return shown.filter {
            it.app.name.lowercase().contains(q) || it.app.blurb.lowercase().contains(q)
        }
    }
}

class LauncherViewModel(app: Application) : AndroidViewModel(app) {

    val repo = LauncherRepository(app)

    private val _state = MutableStateFlow(
        LauncherUiState(
            signedIn = repo.hasCredential(),
            appearance = repo.settings.appearance,
            statuses = repo.catalog.apps.map { AppStatus(it) },
            devRevealed = repo.settings.devChannelRevealed,
            devChannel = repo.settings.devChannel,
        )
    )
    val state: StateFlow<LauncherUiState> = _state.asStateFlow()

    val launcherVersion: String get() = repo.launcherVersion

    init {
        if (repo.hasCredential()) refresh()
    }

    fun refresh() {
        if (_state.value.loading) return
        _state.update { it.copy(loading = true) }
        viewModelScope.launch {
            val statuses = repo.refresh()
            val launcherUpdate = repo.checkLauncherUpdate()
            _state.update {
                it.copy(loading = false, statuses = statuses, signedIn = repo.hasCredential(), launcherUpdate = launcherUpdate)
            }
        }
    }

    /** Update this launcher (same verification as any app). Android closes the app to replace it. */
    fun updateLauncher() {
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.") }
            repo.openInstallPermissionSettings()
            return
        }
        viewModelScope.launch { runLauncherUpdate() }
    }

    private suspend fun runLauncherUpdate() {
        val ok = repo.installLauncher { p -> _state.update { s -> s.copy(progress = s.progress + (p.catalogId to p)) } }
        // Only reached when Android did NOT replace us (refused, cancelled or failed verification).
        if (!ok) _state.update { it.copy(snackbar = "JB Theatre Tools was not updated") }
    }

    fun selectTab(tab: Tab) = _state.update { it.copy(tab = tab, sheetFor = null) }

    fun openSearch() = _state.update { it.copy(search = "") }

    fun setSearch(q: String) = _state.update { it.copy(search = q) }

    fun closeSearch() = _state.update { it.copy(search = null) }

    fun showSheet(app: CatalogApp?) = _state.update { it.copy(sheetFor = app) }

    fun askRemove(app: CatalogApp?) = _state.update { it.copy(confirmRemove = app, sheetFor = null) }

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
        refresh()
    }

    fun signOut() {
        repo.secrets.clear()
        repo.settings.debugFeedBase = ""
        repo.settings.authMode = AuthMode.SERVER
        _state.update {
            it.copy(signedIn = false, statuses = repo.catalog.apps.map { a -> AppStatus(a) })
        }
    }

    fun install(app: CatalogApp) {
        _state.update { it.copy(sheetFor = null) }
        if (!repo.canRequestInstalls()) {
            _state.update {
                it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.")
            }
            repo.openInstallPermissionSettings()
            return
        }
        viewModelScope.launch {
            val ok = repo.install(app) { p -> _state.update { s -> s.copy(progress = s.progress + (p.catalogId to p)) } }
            refreshInstalledOnly()
            _state.update {
                it.copy(snackbar = if (ok) "${app.name} installed" else "${app.name} was not installed")
            }
        }
    }

    fun updateAll() {
        if (_state.value.busyAll) return
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.") }
            repo.openInstallPermissionSettings()
            return
        }
        _state.update { it.copy(busyAll = true) }
        viewModelScope.launch {
            val pending = _state.value.statuses.filter { it.hasUpdate && it.canInstall }
            val done = repo.updateAll(pending) { p ->
                _state.update { s -> s.copy(progress = s.progress + (p.catalogId to p)) }
            }
            refreshInstalledOnly()
            _state.update {
                it.copy(busyAll = false, snackbar = if (pending.isEmpty()) it.snackbar else "Updated $done of ${pending.size}")
            }
            // The launcher goes LAST: replacing it ends this process, so every app update must be done first.
            if (_state.value.launcherUpdate != null) runLauncherUpdate()
        }
    }

    fun confirmRemove(app: CatalogApp) {
        _state.update { it.copy(confirmRemove = null) }
        repo.remove(app.id)
    }

    fun openApp(app: CatalogApp) {
        _state.update { it.copy(sheetFor = null) }
        if (!repo.open(app.id)) {
            _state.update { it.copy(snackbar = "${app.name} isn’t installed") }
        }
    }

    fun releaseNotesUrl(app: CatalogApp): String = repo.releaseNotesUrl(app)

    /** Cheap re-read of what's installed (no network) — after an install or a removal. */
    fun refreshInstalledOnly() {
        _state.update { s ->
            s.copy(statuses = s.statuses.map { it.copy(installedVersion = repo.installedVersionFor(it.app.id)) })
        }
    }

    // ── Dev channel (hidden; the maintainer's devices) ──────────────────────────────

    private var versionTaps = 0

    /** Seven taps on the About version line reveals "Development builds" (Android's developer-options gesture). */
    fun tapVersion() {
        if (_state.value.devRevealed) return
        versionTaps++
        if (versionTaps >= 7) {
            repo.settings.devChannelRevealed = true
            _state.update { it.copy(devRevealed = true, snackbar = "Development builds can now be switched on in About") }
        }
    }

    fun askBackToRelease(app: CatalogApp?) = _state.update { it.copy(confirmBackToRelease = app, sheetFor = null) }

    /**
     * Replace an installed dev build with the release. Android refuses to install an OLDER version over a
     * newer one, so the app is removed first (the system asks to confirm), then the release installs as soon
     * as the package is gone. Gives up quietly if the removal is cancelled.
     */
    fun backToRelease(app: CatalogApp) {
        _state.update { it.copy(confirmBackToRelease = null) }
        if (!repo.canRequestInstalls()) {
            _state.update { it.copy(snackbar = "Allow JB Theatre Tools to install apps, then try again.") }
            repo.openInstallPermissionSettings()
            return
        }
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
