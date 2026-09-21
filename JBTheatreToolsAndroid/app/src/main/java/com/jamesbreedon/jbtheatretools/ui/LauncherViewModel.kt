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
) {
    val installedCount: Int get() = statuses.count { it.isInstalled }
    val updateCount: Int get() = statuses.count { it.hasUpdate }

    fun visibleStatuses(): List<AppStatus> {
        val q = search?.trim()?.lowercase().orEmpty()
        if (q.isEmpty()) return statuses
        return statuses.filter {
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
            _state.update { it.copy(loading = false, statuses = statuses, signedIn = repo.hasCredential()) }
        }
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
                it.copy(busyAll = false, snackbar = "Updated $done of ${pending.size}")
            }
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
        val installed = com.jamesbreedon.jbtheatretools.install.InstalledApps(getApplication())
        _state.update { s ->
            s.copy(statuses = s.statuses.map { it.copy(installedVersion = installed.forCatalogId(it.app.id)?.versionName) })
        }
    }
}
