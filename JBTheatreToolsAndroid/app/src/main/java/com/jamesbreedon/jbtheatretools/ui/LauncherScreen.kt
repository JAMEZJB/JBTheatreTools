package com.jamesbreedon.jbtheatretools.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.remember
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.setValue
import androidx.compose.material3.Text
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import com.jamesbreedon.jbtheatretools.core.UpdatePolicy
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Snackbar
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.material3.windowsizeclass.WindowWidthSizeClass
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import com.jamesbreedon.jbtheatretools.core.ActivityHistory
import com.jamesbreedon.jbtheatretools.core.AppFilter
import com.jamesbreedon.jbtheatretools.core.AppStatus
import com.jamesbreedon.jbtheatretools.core.Appearance
import com.jamesbreedon.jbtheatretools.core.ByteSize
import com.jamesbreedon.jbtheatretools.core.InstallProgress
import com.jamesbreedon.jbtheatretools.core.RelativeAge
import com.jamesbreedon.jbtheatretools.core.ReleaseNotesText
import com.jamesbreedon.jbtheatretools.core.SetupProfile
import com.jamesbreedon.jbtheatretools.core.StatusFilter
import com.jamesbreedon.jbtheatretools.core.VersionCompare
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.ZoneId

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LauncherScreen(vm: LauncherViewModel, widthClass: WindowWidthSizeClass) {
    val state by vm.state.collectAsState()
    val c = House.colors
    val context = LocalContext.current

    if (!state.signedIn) {
        SignInScreen(vm)
        return
    }

    val topInset = WindowInsets.statusBars.asPaddingValues().calculateTopPadding()
    val bottomInset = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
    val expanded = widthClass == WindowWidthSizeClass.Expanded
    val medium = widthClass == WindowWidthSizeClass.Medium
    val gutter = if (expanded) Metrics.gutterExpanded else Metrics.gutterCompact

    Column(Modifier.fillMaxSize().background(c.ground)) {
        HouseAppBar(
            topInset = topInset,
            searching = state.search != null,
            searchText = state.search.orEmpty(),
            onSearchText = vm::setSearch,
            onOpenSearch = vm::openSearch,
            onCloseSearch = vm::closeSearch,
            onRefresh = vm::refresh,
        )
        StatusRow(
            installed = state.installedCount,
            updates = state.updateCount,
            version = vm.launcherVersion,
            loading = state.loading,
        )
        if (state.showLock) {
            Banner(
                "Show lock is on — installs, updates and uninstalls are paused. Opening apps still works.",
                c.info, "Turn off", { vm.requestShowLock(false) },
            )
        }
        state.launcherWhatsNew?.let { version ->
            Banner(
                "Updated to JB Theatre Tools ${VersionCompare.display(version)}.", c.accent,
                "What's new", vm::showLauncherWhatsNew, "Dismiss", vm::dismissLauncherWhatsNew,
            )
        }

        Row(Modifier.weight(1f).fillMaxWidth()) {
            // Medium: an 80dp nav rail. Expanded: the desktop launcher's 220dp sidebar (§4).
            if (medium) NavRail(state.tab, vm::selectTab, state.updateCount)
            if (expanded) Sidebar(state.tab, vm::selectTab, state.updateCount, vm.launcherVersion)

            Box(Modifier.weight(1f).fillMaxHeight()) {
                when (state.tab) {
                    Tab.APPS -> Column(Modifier.fillMaxSize()) {
                        // Find & filter: the status chips (the app bar's search narrows further).
                        // Segments size to their labels and wrap onto a second row when they don't fit (a 360dp
                        // phone at a large font scale), so "Not installed" is never cut off.
                        Segmented(
                            StatusFilter.entries.map { it.label },
                            state.statusFilter.ordinal,
                            { vm.setStatusFilter(StatusFilter.entries[it]) },
                            Modifier.padding(start = gutter, end = gutter, top = 10.dp).fillMaxWidth(),
                            role = Role.Tab,
                        )
                        Box(Modifier.weight(1f).fillMaxWidth()) {
                            if (state.visibleStatuses().isEmpty() && AppFilter.isActive(state.search, state.statusFilter)) {
                                NoMatches()
                            } else if (expanded) {
                                GroupedList(vm, state, gutter)
                            } else {
                                TileGrid(vm, state, gutter, medium)
                            }
                        }
                    }

                    Tab.UPDATES -> UpdatesList(vm, state, gutter)
                    Tab.ABOUT -> AboutScreen(vm, state, gutter)
                }
            }
        }

        // Pinned primary (§3): one per screen, full width, 48 high.
        // While a batch runs its Stop stays here even under show lock (turning the lock on also stops it).
        if (state.tab == Tab.UPDATES && (state.busyAll || (state.updateCount > 0 && !state.showLock))) {
            Box(
                Modifier.fillMaxWidth().background(c.surface)
                    .padding(start = gutter, end = gutter, top = 12.dp, bottom = 12.dp),
            ) {
                if (state.busyAll) {
                    // Stop: the download in flight is cancelled and nothing more starts.
                    SecondaryButton("Stop", Modifier.fillMaxWidth(), onClick = vm::stopAll)
                } else {
                    val size = state.updateBytes
                    PrimaryButton(
                        text = "Update all (${state.updateCount})" + if (size > 0) " · ${ByteSize.format(size)}" else "",
                        modifier = Modifier.fillMaxWidth(),
                        onClick = vm::updateAll,
                    )
                }
            }
        }

        if (!expanded && !medium) {
            BottomNav(state.tab, vm::selectTab, state.updateCount, bottomInset)
        } else {
            Box(Modifier.height(bottomInset))
        }
    }

    // Long-press actions (§7): Open / Update / Hold / Release notes / Remove, plus the app's details.
    state.sheetFor?.let { app ->
        val status = state.statuses.firstOrNull { it.app.id == app.id }
        val progress = state.progress[app.id]
        // Measures the APK on disk: off the main thread, never during composition.
        val details by produceState<Pair<Long, Long>?>(null, app.id, status?.installedVersion) {
            value = withContext(Dispatchers.IO) { vm.repo.installedDetails(app.id) }
        }
        ModalBottomSheet(
            onDismissRequest = { vm.showSheet(null) },
            containerColor = c.raised,
            shape = RoundedCornerShape(topStart = Radii.window, topEnd = Radii.window),
            sheetState = rememberModalBottomSheetState(),
        ) {
            Column(Modifier.padding(horizontal = 20.dp).padding(bottom = 28.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    AppIconTile(app.id, app.name, 40.dp)
                    HSpace(12.dp)
                    Column {
                        TitleText(app.name)
                        status?.latestVersion?.let { MonoText(latestLine(status, it)) }
                    }
                }
                VSpace(10.dp)
                if (status?.isInstalled == true) {
                    SmallText(installedLine(status.installedVersion!!, details), color = c.text2, weight = FontWeight.Normal)
                    if (status.held && status.hasUpdate) {
                        SmallText(
                            "v${status.latestVersion} is available — held at v${status.installedVersion}",
                            color = c.text2, weight = FontWeight.Medium, maxLines = 2,
                        )
                    }
                }
                VSpace(12.dp)
                if (status != null && !status.isInstalled && !status.canInstall) {
                    // Nothing to open or install: say why (no Android build yet / sign in / feed error)
                    // instead of a sheet that silently offers only the release notes.
                    SmallText(status.note ?: "No release", color = c.text2, weight = FontWeight.Normal)
                    VSpace(12.dp)
                }
                if (status?.isInstalled == true) {
                    PrimaryButton("Open", Modifier.fillMaxWidth()) { vm.openApp(app) }
                    VSpace(10.dp)
                }
                if (progress?.phase == InstallProgress.Phase.DOWNLOADING) {
                    SecondaryButton("Cancel download", Modifier.fillMaxWidth()) { vm.cancelInstall(app) }
                    VSpace(10.dp)
                } else if (!state.showLock && status?.backToRelease == true) {
                    SecondaryButton("Back to release v${status.latestVersion}", Modifier.fillMaxWidth()) {
                        vm.askBackToRelease(app)
                    }
                    VSpace(10.dp)
                } else if (!state.showLock && status?.canInstall == true && (!status.isInstalled || status.updatePending)) {
                    SecondaryButton(
                        if (status.isInstalled) "Update" else "Install",
                        Modifier.fillMaxWidth(),
                        enabled = progress?.isActive != true,
                    ) { vm.install(app) }
                    VSpace(10.dp)
                }
                SecondaryButton("Release notes", Modifier.fillMaxWidth()) { vm.showReleaseNotes(app) }
                if (status?.isInstalled == true) {
                    VSpace(10.dp)
                    SecondaryButton(if (status.held) "Release hold" else "Hold at this version", Modifier.fillMaxWidth()) {
                        vm.toggleHold(app)
                    }
                    if (!state.showLock) {
                        VSpace(10.dp)
                        GhostButton("Remove", Modifier.fillMaxWidth()) { vm.askRemove(app) }
                    }
                }
            }
        }
    }

    state.notes?.let { notes -> ReleaseNotesSheet(notes, vm::closeNotes) }

    state.importPreview?.let { preview ->
        ModalBottomSheet(
            onDismissRequest = vm::cancelImport,
            containerColor = c.raised,
            shape = RoundedCornerShape(topStart = Radii.window, topEnd = Radii.window),
            sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        ) {
            Column(Modifier.padding(horizontal = 20.dp).padding(bottom = 28.dp)) {
                TitleText("Import setup")
                VSpace(10.dp)
                // The summary scrolls and the buttons stay pinned: a full-rig import is ~27 lines, which pushed
                // Install / Cancel off a small screen.
                Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState())) {
                    BodyText(preview.summary, color = c.text, maxLines = Int.MAX_VALUE)
                }
                VSpace(16.dp)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    SecondaryButton("Cancel", Modifier.weight(1f), onClick = vm::cancelImport)
                    PrimaryButton(if (preview.plan.toInstall.isEmpty()) "Apply" else "Install", Modifier.weight(1f), onClick = vm::confirmImport)
                }
            }
        }
    }

    // Rule 8: destructive actions confirm in a bottom sheet, never a centred modal.
    state.confirmRemove?.let { app ->
        ModalBottomSheet(
            onDismissRequest = { vm.askRemove(null) },
            containerColor = c.raised,
            shape = RoundedCornerShape(topStart = Radii.window, topEnd = Radii.window),
            sheetState = rememberModalBottomSheetState(),
        ) {
            Column(Modifier.padding(horizontal = 20.dp).padding(bottom = 28.dp)) {
                TitleText("Remove ${app.name}?")
                VSpace(6.dp)
                BodyText("The app is uninstalled from this device. Its saved settings go with it.", maxLines = Int.MAX_VALUE)
                VSpace(16.dp)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    SecondaryButton("Cancel", Modifier.weight(1f)) { vm.askRemove(null) }
                    DangerButton("Remove", Modifier.weight(1f)) { vm.confirmRemove(app) }
                }
            }
        }
    }

    state.confirmBackToRelease?.let { app ->
        ModalBottomSheet(
            onDismissRequest = { vm.askBackToRelease(null) },
            containerColor = c.raised,
            shape = RoundedCornerShape(topStart = Radii.window, topEnd = Radii.window),
            sheetState = rememberModalBottomSheetState(),
        ) {
            Column(Modifier.padding(horizontal = 20.dp).padding(bottom = 28.dp)) {
                TitleText("Back to the release of ${app.name}?")
                VSpace(6.dp)
                BodyText(
                    "Android can't install an older version over a newer one, so the development build is removed " +
                        "first and the release installs straight after. The app's saved settings on this device are reset.",
                    maxLines = Int.MAX_VALUE,
                )
                VSpace(16.dp)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    SecondaryButton("Cancel", Modifier.weight(1f)) { vm.askBackToRelease(null) }
                    DangerButton("Remove and reinstall", Modifier.weight(1f)) { vm.backToRelease(app) }
                }
            }
        }
    }

    // Turning show lock off always asks first — one stray tap mid-show must not reopen installs. "Keep on" is the
    // default: it's the primary button, and dismissing the sheet keeps the lock too.
    if (state.confirmShowLockOff) {
        ModalBottomSheet(
            onDismissRequest = vm::keepShowLock,
            containerColor = c.raised,
            shape = RoundedCornerShape(topStart = Radii.window, topEnd = Radii.window),
            sheetState = rememberModalBottomSheetState(),
        ) {
            Column(Modifier.padding(horizontal = 20.dp).padding(bottom = 28.dp)) {
                TitleText("Turn off show lock?", maxLines = 2)
                VSpace(6.dp)
                BodyText("Installs, updates and uninstalls can run again.", maxLines = Int.MAX_VALUE)
                VSpace(16.dp)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    SecondaryButton("Turn off", Modifier.weight(1f), onClick = vm::confirmShowLockOff)
                    PrimaryButton("Keep on", Modifier.weight(1f), onClick = vm::keepShowLock)
                }
            }
        }
    }

    state.snackbar?.let { message ->
        LaunchedEffect(message) {
            kotlinx.coroutines.delay(3500)
            vm.dismissSnackbar()
        }
        Box(Modifier.fillMaxSize().padding(bottom = bottomInset + 96.dp), Alignment.BottomCenter) {
            Snackbar(
                containerColor = c.raised,
                contentColor = c.text,
                modifier = Modifier.padding(horizontal = 20.dp),
            ) { BodyText(message, color = c.text, maxLines = 5) }
        }
    }
}

// ── chrome ──────────────────────────────────────────────────────────────────

/** §3/§7: 52 high + top inset, identity tile, title, ONE action. */
@Composable
private fun HouseAppBar(
    topInset: androidx.compose.ui.unit.Dp,
    searching: Boolean,
    searchText: String,
    onSearchText: (String) -> Unit,
    onOpenSearch: () -> Unit,
    onCloseSearch: () -> Unit,
    onRefresh: () -> Unit,
) {
    val c = House.colors
    Column(Modifier.fillMaxWidth().background(c.surface)) {
        Box(Modifier.height(topInset))
        Row(
            Modifier.fillMaxWidth().height(Metrics.appBarHeight).padding(horizontal = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            if (searching) {
                IconAction(HouseIcons.ArrowLeft, "Close search", c.text2, onCloseSearch)
                HSpace(4.dp)
                HouseTextField(searchText, onSearchText, "Search apps", Modifier.weight(1f))
            } else {
                IdentityTile()
                HSpace(10.dp)
                TitleText("JB Theatre Tools", Modifier.weight(1f))
                IconAction(HouseIcons.Refresh, "Check for updates", c.text2, onRefresh)
                IconAction(HouseIcons.Search, "Search apps", c.text2, onOpenSearch)
            }
        }
        Divider()
    }
}

/** Rule 20: 36 high, `--raised`, dot + text, right-aligned mono value. */
@Composable
private fun StatusRow(installed: Int, updates: Int, version: String, loading: Boolean) {
    val c = House.colors
    Column(Modifier.fillMaxWidth().background(c.raised)) {
        Row(
            Modifier.fillMaxWidth().height(Metrics.statusRowHeight).padding(horizontal = 20.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Dot(if (loading) c.info else if (updates > 0) c.warn else c.ok)
            HSpace(8.dp)
            SmallText(
                if (loading) "Checking releases…"
                else "$installed installed · $updates update${if (updates == 1) "" else "s"} available",
                Modifier.weight(1f),
                color = c.text2,
                weight = FontWeight.Medium,
            )
            MonoText("v$version", color = c.text3)
        }
        Divider()
    }
}

/** §3: 80 high + bottom inset, 3 destinations, active = accent pill 56×32. */
@Composable
private fun BottomNav(tab: Tab, onSelect: (Tab) -> Unit, updates: Int, bottomInset: androidx.compose.ui.unit.Dp) {
    val c = House.colors
    Column(Modifier.fillMaxWidth().background(c.surface)) {
        Divider()
        Row(Modifier.fillMaxWidth().height(Metrics.bottomNavHeight)) {
            NavItem("Apps", HouseIcons.Apps, tab == Tab.APPS, Modifier.weight(1f)) { onSelect(Tab.APPS) }
            NavItem(
                if (updates > 0) "Updates ($updates)" else "Updates",
                HouseIcons.Download, tab == Tab.UPDATES, Modifier.weight(1f),
            ) { onSelect(Tab.UPDATES) }
            NavItem("About", HouseIcons.Info, tab == Tab.ABOUT, Modifier.weight(1f)) { onSelect(Tab.ABOUT) }
        }
        Box(Modifier.height(bottomInset).fillMaxWidth().background(c.surface))
    }
}

@Composable
private fun NavItem(
    label: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    active: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    val c = House.colors
    Column(
        modifier.fillMaxHeight().clickable(onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Box(
            Modifier.size(56.dp, 32.dp).clip(RoundedCornerShape(16.dp))
                .background(if (active) c.accent else Color.Transparent),
            contentAlignment = Alignment.Center,
        ) {
            Glyph(icon, 22.dp, if (active) c.onAccent else c.text2)
        }
        VSpace(4.dp)
        SmallText(
            label,
            color = if (active) c.text else c.text2,
            weight = if (active) FontWeight.SemiBold else FontWeight.Medium,
            align = TextAlign.Center,
        )
    }
}

/** Medium: the sidebar becomes an 80dp rail (§4). */
@Composable
private fun NavRail(tab: Tab, onSelect: (Tab) -> Unit, updates: Int) {
    val c = House.colors
    Row {
        Column(
            Modifier.width(Metrics.navRailWidth).fillMaxHeight().background(c.surface)
                .padding(vertical = 12.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            RailItem("Apps", HouseIcons.Apps, tab == Tab.APPS) { onSelect(Tab.APPS) }
            RailItem(
                if (updates > 0) "Updates" else "Updates", HouseIcons.Download, tab == Tab.UPDATES,
            ) { onSelect(Tab.UPDATES) }
            RailItem("About", HouseIcons.Info, tab == Tab.ABOUT) { onSelect(Tab.ABOUT) }
        }
        Box(Modifier.width(1.dp).fillMaxHeight().background(c.line))
    }
}

@Composable
private fun RailItem(
    label: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    active: Boolean,
    onClick: () -> Unit,
) {
    val c = House.colors
    Column(
        Modifier.fillMaxWidth().heightIn(min = 60.dp).clickable(onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Box(
            Modifier.size(56.dp, 32.dp).clip(RoundedCornerShape(16.dp))
                .background(if (active) c.accent else Color.Transparent),
            contentAlignment = Alignment.Center,
        ) {
            Glyph(icon, 22.dp, if (active) c.onAccent else c.text2)
        }
        VSpace(4.dp)
        SmallText(
            label, color = if (active) c.text else c.text2,
            weight = if (active) FontWeight.SemiBold else FontWeight.Medium, align = TextAlign.Center,
        )
    }
}

/** Expanded: the desktop launcher's 220dp sidebar, groups + foot credit (§4). */
@Composable
private fun Sidebar(tab: Tab, onSelect: (Tab) -> Unit, updates: Int, version: String) {
    val c = House.colors
    Row {
        Column(
            Modifier.width(Metrics.sidebarWidth).fillMaxHeight().background(c.surface)
                .padding(horizontal = 12.dp, vertical = 12.dp),
        ) {
            SidebarItem("Apps", HouseIcons.Apps, tab == Tab.APPS) { onSelect(Tab.APPS) }
            SidebarItem(
                if (updates > 0) "Updates ($updates)" else "Updates",
                HouseIcons.Download, tab == Tab.UPDATES,
            ) { onSelect(Tab.UPDATES) }
            SidebarItem("About", HouseIcons.Info, tab == Tab.ABOUT) { onSelect(Tab.ABOUT) }
            Box(Modifier.weight(1f))
            Divider()
            VSpace(10.dp)
            // The credit never wraps mid-phrase and is never cut off (§6): at 220dp it sits on two
            // lines, the same way the desktop launcher's sidebar foot does.
            SmallText(
                "Created by: James Breedon & Claude Code",
                color = c.text3, weight = FontWeight.Normal, maxLines = 2,
            )
            MonoText("v$version", color = c.text3)
        }
        Box(Modifier.width(1.dp).fillMaxHeight().background(c.line))
    }
}

@Composable
private fun SidebarItem(
    label: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    active: Boolean,
    onClick: () -> Unit,
) {
    val c = House.colors
    Row(
        Modifier.fillMaxWidth().height(Metrics.touchTargetPreferred)
            .clip(RoundedCornerShape(Radii.control))
            .background(if (active) c.accent else Color.Transparent)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Glyph(icon, 20.dp, if (active) c.onAccent else c.text2)
        HSpace(10.dp)
        SmallText(
            label, color = if (active) c.onAccent else c.text,
            weight = if (active) FontWeight.SemiBold else FontWeight.Medium,
        )
    }
}

// ── the three destinations ──────────────────────────────────────────────────

/** §7: 3-column glyph-tile grid, name 12/500, mono version under it, long-press = actions. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun TileGrid(
    vm: LauncherViewModel,
    state: LauncherUiState,
    gutter: androidx.compose.ui.unit.Dp,
    medium: Boolean,
) {
    val items = state.visibleStatuses()
    LazyVerticalGrid(
        columns = if (medium) GridCells.Adaptive(112.dp) else GridCells.Fixed(3),
        modifier = Modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = gutter, end = gutter, top = 16.dp, bottom = 24.dp,
        ),
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        items(items, key = { it.app.id }) { status ->
            AppTile(status, state.progress[status.app.id], vm)
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun AppTile(status: AppStatus, progress: InstallProgress?, vm: LauncherViewModel) {
    val c = House.colors
    Column(
        Modifier
            .clip(RoundedCornerShape(Radii.panel))
            .combinedClickable(
                onClick = { vm.showSheet(status.app) },
                onLongClick = { vm.showSheet(status.app) },
            )
            .padding(vertical = 8.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Box(contentAlignment = Alignment.TopEnd) {
            AppIconTile(status.app.id, status.app.name, Metrics.gridTile)
            if (status.updatePending) {
                Box(
                    Modifier.size(10.dp).clip(RoundedCornerShape(5.dp)).background(c.warn)
                        .border(2.dp, c.ground, RoundedCornerShape(5.dp)),
                )
            }
        }
        VSpace(6.dp)
        SmallText(
            status.app.name, color = c.text, weight = FontWeight.Medium,
            align = TextAlign.Center, maxLines = 2,
            modifier = Modifier.fillMaxWidth(),
        )
        val version = status.installedVersion ?: status.latestVersion
        if (version != null) {
            val tag = when {
                status.isDev -> "dev"
                status.held -> "held"
                else -> null
            }
            TileVersion("v$version", tag, if (status.isDev) c.warn else c.text3)
        } else {
            SmallText(status.note ?: "", color = c.text3, weight = FontWeight.Normal, align = TextAlign.Center, maxLines = 2)
        }
        if (progress != null && progress.isActive) {
            VSpace(4.dp)
            HouseProgress(progress.fraction.toFloat(), Modifier.fillMaxWidth().padding(horizontal = 6.dp))
        }
    }
}

/** Expanded: the desktop launcher's grouped list (§7). */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun GroupedList(vm: LauncherViewModel, state: LauncherUiState, gutter: androidx.compose.ui.unit.Dp) {
    val visible = state.visibleStatuses()
    val categories = vm.repo.catalog.orderedCategories()
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = gutter, end = gutter, top = 8.dp, bottom = 24.dp,
        ),
    ) {
        categories.forEach { category ->
            val rows = visible.filter { it.app.category == category }
            if (rows.isEmpty()) return@forEach
            item(key = "header-$category") { SectionHeader(category) }
            items(rows, key = { it.app.id }) { status ->
                AppRow(status, state.progress[status.app.id], vm, state.showLock)
            }
        }
        val uncategorised = visible.filter { it.app.category == null }
        if (uncategorised.isNotEmpty()) {
            item(key = "header-other") { SectionHeader("Other") }
            items(uncategorised, key = { it.app.id }) { status ->
                AppRow(status, state.progress[status.app.id], vm, state.showLock)
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun AppRow(status: AppStatus, progress: InstallProgress?, vm: LauncherViewModel, locked: Boolean) {
    val c = House.colors
    Column(
        Modifier.fillMaxWidth().padding(vertical = 4.dp)
            .clip(RoundedCornerShape(Radii.panel))
            .background(c.surface)
            .border(1.dp, c.line, RoundedCornerShape(Radii.panel))
            .combinedClickable(
                onClick = { vm.showSheet(status.app) },
                onLongClick = { vm.showSheet(status.app) },
            )
            .padding(horizontal = 14.dp, vertical = 12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            AppIconTile(status.app.id, status.app.name, 40.dp)
            HSpace(12.dp)
            Column(Modifier.weight(1f)) {
                TitleText(status.app.name)
                BodyText(status.app.blurb, maxLines = 1)
                status.whatsNew?.let { line ->
                    SmallText(
                        (status.whatsNewVersion?.let { "New in $it: " } ?: "") + line,
                        color = c.text3, weight = FontWeight.Normal, maxLines = 1,
                    )
                }
            }
            HSpace(12.dp)
            Column(horizontalAlignment = Alignment.End, modifier = Modifier.widthIn(min = 120.dp)) {
                val busy = progress?.isActive == true
                when {
                    status.updatePending -> {
                        MonoText("v${status.installedVersion} → v${status.latestVersion}", color = c.warn)
                        if (!locked) {
                            VSpace(6.dp)
                            PrimaryButton("Update", Modifier.width(120.dp), enabled = !busy) { vm.install(status.app) }
                        }
                    }

                    status.isInstalled -> {
                        MonoText("v${status.installedVersion}" + if (status.held) " · held" else "", color = c.text3)
                        if (status.held && status.hasUpdate) {
                            SmallText("v${status.latestVersion} available", color = c.text2, weight = FontWeight.Normal)
                        }
                        VSpace(6.dp)
                        SecondaryButton("Open", Modifier.width(120.dp)) { vm.openApp(status.app) }
                    }

                    status.canInstall -> {
                        MonoText(
                            "v${status.latestVersion}" + if (status.apkSizeBytes > 0) " · ${ByteSize.format(status.apkSizeBytes)}" else "",
                            color = c.text3,
                        )
                        if (!locked) {
                            VSpace(6.dp)
                            SecondaryButton("Install", Modifier.width(120.dp), enabled = !busy) { vm.install(status.app) }
                        }
                    }

                    else -> SmallText(status.note ?: "No release", color = c.text3, weight = FontWeight.Normal, maxLines = 3)
                }
                if (status.isDev) {
                    VSpace(4.dp)
                    SmallText("dev build", color = c.warn, weight = FontWeight.Medium)
                }
                if (status.backToRelease && !locked) {
                    VSpace(6.dp)
                    SecondaryButton("Back to release", Modifier.width(120.dp)) { vm.askBackToRelease(status.app) }
                }
            }
        }
        if (progress != null && progress.phase != InstallProgress.Phase.DONE) {
            VSpace(8.dp)
            Row(verticalAlignment = Alignment.CenterVertically) {
                SmallText(progressLine(progress), Modifier.weight(1f), color = c.text2, weight = FontWeight.Normal)
                if (progress.phase == InstallProgress.Phase.DOWNLOADING) {
                    GhostButton(
                        "Cancel",
                        Modifier.width(88.dp).semantics { contentDescription = "Cancel the download of ${status.app.name}" },
                    ) { vm.cancelInstall(status.app) }
                }
            }
            VSpace(4.dp)
            if (progress.isActive) HouseProgress(progress.fraction.toFloat(), Modifier.fillMaxWidth())
        }
    }
}

@Composable
private fun UpdatesList(vm: LauncherViewModel, state: LauncherUiState, gutter: androidx.compose.ui.unit.Dp) {
    val c = House.colors
    val pending = state.statuses.filter { it.updatePending }
    val held = state.statuses.filter { it.held && it.hasUpdate }
    val notInstalled = state.statuses.filter { !it.isInstalled && it.canInstall }
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = gutter, end = gutter, top = 8.dp, bottom = 24.dp,
        ),
    ) {
        state.launcherUpdate?.let { version ->
            item { SectionHeader("JB Theatre Tools") }
            item { LauncherUpdateRow(version, state.progress["jbtheatretools"], vm, state.showLock) }
        }
        if (pending.isEmpty() && state.launcherUpdate == null) {
            item {
                Column(
                    Modifier.fillMaxWidth().padding(top = 40.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Glyph(HouseIcons.Check, 28.dp, c.ok)
                    VSpace(10.dp)
                    // Held apps with a newer version are listed below: "everything is up to date" would be untrue.
                    TitleText(if (held.isEmpty()) "Everything is up to date" else "No updates to install", maxLines = 2)
                    VSpace(4.dp)
                    BodyText(
                        if (held.isEmpty()) "${state.installedCount} apps installed"
                        else "Held apps stay at their version until you release the hold.",
                        maxLines = Int.MAX_VALUE,
                    )
                }
            }
        } else if (pending.isNotEmpty()) {
            item { SectionHeader("Updates available") }
            items(pending, key = { it.app.id }) { AppRow(it, state.progress[it.app.id], vm, state.showLock) }
        }
        if (held.isNotEmpty()) {
            item { SectionHeader("Held at their version") }
            items(held, key = { "held-" + it.app.id }) { AppRow(it, state.progress[it.app.id], vm, state.showLock) }
        }
        if (notInstalled.isNotEmpty()) {
            item { SectionHeader("Not installed") }
            items(notInstalled, key = { it.app.id }) { AppRow(it, state.progress[it.app.id], vm, state.showLock) }
        }
    }
}

/** The launcher's own update, at the top of Updates (and reachable from About). */
@Composable
private fun LauncherUpdateRow(version: String, progress: InstallProgress?, vm: LauncherViewModel, locked: Boolean) {
    val c = House.colors
    Column(Modifier.fillMaxWidth().padding(vertical = 10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IdentityTile(40.dp)
            HSpace(12.dp)
            Column(Modifier.weight(1f)) {
                SmallText("JB Theatre Tools", color = c.text, weight = FontWeight.Medium)
                MonoText("v${vm.launcherVersion} → v$version", color = c.text3)
            }
            val busy = progress?.isActive == true
            if (!locked) {
                SecondaryButton(if (busy) "Updating…" else "Update", Modifier.width(120.dp), enabled = !busy) {
                    vm.updateLauncher()
                }
            }
        }
        VSpace(6.dp)
        SmallText(
            "The launcher closes while Android installs its update — open it again afterwards.",
            color = c.text3, weight = FontWeight.Normal, maxLines = Int.MAX_VALUE,
        )
        if (progress != null && progress.phase != InstallProgress.Phase.DONE) {
            VSpace(8.dp)
            SmallText(progressLine(progress), color = c.text2, weight = FontWeight.Normal)
            VSpace(4.dp)
            HouseProgress(progress.fraction.toFloat(), Modifier.fillMaxWidth())
        }
    }
}

private fun progressLine(p: InstallProgress): String = when (p.phase) {
    InstallProgress.Phase.DOWNLOADING -> "Downloading — ${(p.fraction * 100).toInt()}%"
    InstallProgress.Phase.VERIFYING -> "Verifying signature and checksum…"
    InstallProgress.Phase.INSTALLING -> "Installing…"
    InstallProgress.Phase.DONE -> "Installed"
    InstallProgress.Phase.FAILED -> p.message ?: "Failed"
    InstallProgress.Phase.CANCELLED -> "Cancelled"
}

@Composable
private fun AboutScreen(vm: LauncherViewModel, state: LauncherUiState, gutter: androidx.compose.ui.unit.Dp) {
    val c = House.colors
    val context = LocalContext.current
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = gutter, end = gutter, top = 16.dp, bottom = 32.dp,
        ),
    ) {
        item {
            Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
                IdentityTile(64.dp)
                VSpace(10.dp)
                TitleText("JB Theatre Tools")
                VSpace(4.dp)
                // Rule 28, exactly.
                SmallText(
                    "Created by: James Breedon & Claude Code  ·  v${vm.launcherVersion}",
                    color = c.text2, weight = FontWeight.Normal,
                )
                SmallText(
                    "Android build ${vm.launcherVersion} (arm64)",
                    color = c.text3, weight = FontWeight.Normal,
                    modifier = Modifier.clickable(
                        interactionSource = remember { MutableInteractionSource() }, indication = null,
                    ) { vm.tapVersion() },
                )
                state.launcherUpdate?.takeIf { !state.showLock }?.let { version ->
                    VSpace(12.dp)
                    SecondaryButton("Update to v$version", Modifier.width(220.dp)) { vm.updateLauncher() }
                }
            }
            VSpace(20.dp)
        }
        item {
            Panel(Modifier.fillMaxWidth()) {
                Column {
                    LabelText("Appearance")
                    VSpace(8.dp)
                    Segmented(
                        listOf("Light", "Dark", "System"),
                        when (state.appearance) {
                            Appearance.LIGHT -> 0
                            Appearance.DARK -> 1
                            Appearance.SYSTEM -> 2
                        },
                        {
                            vm.setAppearance(
                                when (it) {
                                    0 -> Appearance.LIGHT
                                    1 -> Appearance.DARK
                                    else -> Appearance.SYSTEM
                                }
                            )
                        },
                        Modifier.fillMaxWidth(),
                    )
                }
            }
            VSpace(12.dp)
        }
        item { AboutV130Panels(vm, state) }
        if (state.devRevealed) {
            item {
                Panel(Modifier.fillMaxWidth()) {
                    Column {
                        LabelText("Development builds")
                        VSpace(8.dp)
                        Segmented(listOf("Off", "On"), if (state.devChannel) 1 else 0, { vm.setDevChannel(it == 1) },
                            Modifier.fillMaxWidth())
                        VSpace(8.dp)
                        BodyText(
                            "On this device only: also offer pre-release development builds (marked “dev”). " +
                                "They're verified exactly like releases. A proper release always replaces them.",
                            maxLines = Int.MAX_VALUE,
                        )
                    }
                }
                VSpace(12.dp)
            }
        }
        item {
            Panel(Modifier.fillMaxWidth()) {
                Column {
                    LabelText("Downloads")
                    VSpace(8.dp)
                    BodyText(
                        "Releases are verified before they install: the release's checksum list must " +
                            "carry the suite signature, the file must match its checksum, and the app " +
                            "must be signed with the suite certificate.",
                        maxLines = Int.MAX_VALUE,
                    )
                    VSpace(12.dp)
                    SecondaryButton("Sign out", Modifier.fillMaxWidth()) { vm.signOut() }
                }
            }
            VSpace(12.dp)
        }
        item {
            Panel(Modifier.fillMaxWidth()) {
                Column {
                    LabelText("Log")
                    VSpace(8.dp)
                    BodyText(state.logTail.ifBlank { "No entries yet." }, maxLines = 8)
                    VSpace(12.dp)
                    SecondaryButton("Share log", Modifier.fillMaxWidth()) {
                        vm.shareLog { text -> shareText(context, "JB Theatre Tools log", text, "Share log") }
                    }
                    VSpace(10.dp)
                    SecondaryButton("Share diagnostics", Modifier.fillMaxWidth()) {
                        vm.shareDiagnostics { text -> shareText(context, "JB Theatre Tools diagnostics", text, "Share diagnostics") }
                    }
                }
            }
            VSpace(12.dp)
        }
        item {
            Panel(Modifier.fillMaxWidth()) {
                Column {
                    LabelText("Convert")
                    VSpace(8.dp)
                    BodyText(
                        "Convert is a repackage of the open-source p2r3/convert, under the GPL-2.0. " +
                            "Its source is at github.com/p2r3/convert.",
                        maxLines = Int.MAX_VALUE,
                    )
                    VSpace(12.dp)
                    SecondaryButton("Open the Convert source", Modifier.fillMaxWidth()) {
                        openUrl(context, "https://github.com/p2r3/convert")
                    }
                }
            }
        }
    }
}

private fun openUrl(context: android.content.Context, url: String) {
    runCatching {
        context.startActivity(
            android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(url))
                .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}

private fun shareText(context: android.content.Context, subject: String, text: String, title: String) {
    runCatching {
        val intent = android.content.Intent(android.content.Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(android.content.Intent.EXTRA_SUBJECT, subject)
            putExtra(android.content.Intent.EXTRA_TEXT, text)
            addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(android.content.Intent.createChooser(intent, title))
    }
}

// ── v1.30 pieces ────────────────────────────────────────────────────────────

/** "v1.2.0 · 12 MB · 3 days ago" — the latest release, its download size and age. */
private fun latestLine(status: AppStatus, latest: String): String {
    val bits = mutableListOf("v$latest")
    if (status.apkSizeBytes > 0) bits.add(ByteSize.format(status.apkSizeBytes))
    RelativeAge.parseIso(status.latestPublished)?.let { bits.add(RelativeAge.describe(it, Instant.now())) }
    return bits.joinToString(" · ")
}

/** "Installed v1.1.0 · 12 Sep 2026 · 14 MB". */
private fun installedLine(installed: String, details: Pair<Long, Long>?): String {
    val bits = mutableListOf("Installed v$installed")
    if (details != null) {
        if (details.first > 0) {
            val date = Instant.ofEpochMilli(details.first).atZone(ZoneId.systemDefault()).toLocalDate()
            bits.add(java.time.format.DateTimeFormatter.ofPattern("d MMM yyyy", java.util.Locale.US).format(date))
        }
        if (details.second > 0) bits.add(ByteSize.format(details.second))
    }
    return bits.joinToString(" · ")
}

/**
 * A full-width notice band (the kit's `.banner`): a 10% wash of the tint with the tint on its edge and actions; the
 * text is `--text` (the tint itself is below 4.5:1 on its own wash) and wraps as far as it needs to. With two actions
 * they sit under the text, so a narrow phone at a large font scale doesn't squeeze the text into a sliver.
 */
@Composable
private fun Banner(
    text: String,
    tint: Color,
    action: String,
    onAction: () -> Unit,
    secondary: String? = null,
    onSecondary: (() -> Unit)? = null,
) {
    val c = House.colors
    Column(Modifier.fillMaxWidth().background(c.surface).background(tint.copy(alpha = 0.10f))) {
        if (secondary != null && onSecondary != null) {
            SmallText(
                text, Modifier.fillMaxWidth().padding(start = 20.dp, end = 20.dp, top = 10.dp),
                color = c.text, weight = FontWeight.SemiBold, maxLines = Int.MAX_VALUE,
            )
            Row(
                Modifier.fillMaxWidth().padding(start = 8.dp, end = 8.dp, bottom = 2.dp),
                horizontalArrangement = Arrangement.End,
            ) {
                BannerAction(secondary, c.text2, onSecondary)
                BannerAction(action, tint, onAction)
            }
        } else {
            Row(
                Modifier.fillMaxWidth().padding(start = 20.dp, end = 8.dp, top = 6.dp, bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                SmallText(text, Modifier.weight(1f), color = c.text, weight = FontWeight.SemiBold, maxLines = Int.MAX_VALUE)
                BannerAction(action, tint, onAction)
            }
        }
        Box(Modifier.fillMaxWidth().height(1.dp).background(tint.copy(alpha = 0.40f)))
    }
}

/**
 * The tile's "v1.12.0 · held": on one line when it fits; when it doesn't (a 3-column phone grid at a large font scale)
 * the version keeps its own line and the tag drops underneath — never "v1.12.0 · h…".
 */
@Composable
private fun TileVersion(version: String, tag: String?, color: Color) {
    if (tag == null) {
        MonoText(version, color = color)
        return
    }
    var stacked by remember(version, tag) { mutableStateOf(false) }
    if (!stacked) {
        Text(
            "$version · $tag", color = color, maxLines = 1, softWrap = false,
            style = TextStyle(fontFamily = HouseType.mono, fontWeight = FontWeight.Medium, fontSize = HouseType.smallSize),
            onTextLayout = { if (it.hasVisualOverflow) stacked = true },
        )
    } else {
        MonoText(version, color = color)
        MonoText(tag, color = color)
    }
}

@Composable
private fun BannerAction(label: String, color: Color, onClick: () -> Unit) {
    Box(
        Modifier.heightIn(min = Metrics.touchTarget).clip(RoundedCornerShape(Radii.control))
            .clickable(onClick = onClick).padding(horizontal = 12.dp),
        contentAlignment = Alignment.Center,
    ) {
        SmallText(label, color = color, weight = FontWeight.SemiBold)
    }
}

@Composable
private fun NoMatches() {
    val c = House.colors
    Column(
        Modifier.fillMaxWidth().padding(top = 48.dp, start = 24.dp, end = 24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Glyph(HouseIcons.Search, 28.dp, c.text3)
        VSpace(10.dp)
        TitleText("No apps match")
        VSpace(4.dp)
        BodyText("Clear the search or pick another filter.", maxLines = 2)
    }
}

/** In-app release notes: every release, newest first, with its date and notes; newer-than-installed flagged. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ReleaseNotesSheet(notes: NotesSheet, onClose: () -> Unit) {
    val c = House.colors
    ModalBottomSheet(
        onDismissRequest = onClose,
        containerColor = c.raised,
        shape = RoundedCornerShape(topStart = Radii.window, topEnd = Radii.window),
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
    ) {
        LazyColumn(Modifier.fillMaxWidth().padding(horizontal = 20.dp)) {
            item {
                TitleText(notes.title, maxLines = 2)
                VSpace(12.dp)
            }
            if (notes.loading) {
                item { BodyText("Loading the release notes…") }
            }
            notes.message?.let { msg -> item { BodyText(msg, color = c.text, maxLines = 20) } }
            val now = Instant.now()
            items(notes.releases, key = { it.tagName }) { r ->
                Column(Modifier.fillMaxWidth().padding(bottom = 18.dp)) {
                    TitleText(VersionCompare.display(r.tagName))
                    val meta = mutableListOf<String>()
                    RelativeAge.parseIso(r.publishedAt)?.let { p ->
                        val date = p.atZone(ZoneId.systemDefault()).toLocalDate()
                        meta.add(java.time.format.DateTimeFormatter.ofPattern("d MMM yyyy", java.util.Locale.US).format(date) +
                            " (" + RelativeAge.describe(p, now) + ")")
                    }
                    if (r.prerelease) meta.add(if (VersionCompare.isDev(r.tagName)) "development build" else "pre-release")
                    val installed = notes.installed
                    if (installed != null && VersionCompare.equal(r.tagName, installed)) meta.add("✓ installed")
                    else if (installed != null && VersionCompare.isNewer(r.tagName, installed)) meta.add("New since your version")
                    if (meta.isNotEmpty()) SmallText(meta.joinToString(" · "), color = c.text2, weight = FontWeight.Normal, maxLines = 2)
                    VSpace(6.dp)
                    BodyText(ReleaseNotesText.plain(r.body), color = c.text, maxLines = Int.MAX_VALUE)
                }
            }
            item { VSpace(28.dp) }
        }
    }
}

/** About → the v1.30 panels: show lock, automatic checks + notifications, setup files, storage, recent activity. */
@Composable
private fun AboutV130Panels(vm: LauncherViewModel, state: LauncherUiState) {
    val c = House.colors
    val context = LocalContext.current
    val notifyPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { _ ->
        vm.setNotifyUpdates(true)   // the view model sees a refusal (canPost() false) and says so
    }
    val exportFile = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/json")) { uri ->
        uri?.let(vm::exportSetup)
    }
    val importFile = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        uri?.let(vm::previewImport)
    }
    LaunchedEffect(Unit) { vm.refreshStorage() }

    Column {
        Panel(Modifier.fillMaxWidth()) {
            Column {
                LabelText("Show lock")
                VSpace(8.dp)
                Segmented(listOf("Off", "On"), if (state.showLock) 1 else 0, { vm.requestShowLock(it == 1) }, Modifier.fillMaxWidth())
                VSpace(8.dp)
                BodyText("For show time: nothing installs, updates or is removed. Opening apps still works.", maxLines = Int.MAX_VALUE)
            }
        }
        VSpace(12.dp)
        Panel(Modifier.fillMaxWidth()) {
            Column {
                LabelText("While open, check again")
                VSpace(8.dp)
                // The same choices and wording as the desktop launchers (shortest first); an unknown stored value
                // shows the default, which is also what it behaves as.
                val choices = UpdatePolicy.intervals
                val current = choices.indexOfFirst { it.first == state.autoCheckInterval }
                    .takeIf { it >= 0 } ?: choices.indexOfFirst { it.first == UpdatePolicy.DEFAULT_INTERVAL }
                Segmented(
                    choices.map { it.second },
                    current,
                    { vm.setAutoCheckInterval(choices[it].first) },
                    Modifier.fillMaxWidth(),
                )
                VSpace(8.dp)
                BodyText(
                    "While the launcher is open it checks for new versions this often. With notifications on, it also " +
                        "checks in the background on Wi-Fi, roughly this often — Android picks the exact time, and " +
                        "waits while the battery is low.",
                    maxLines = Int.MAX_VALUE,
                )
                VSpace(12.dp)
                LabelText("Notify me when updates are available")
                VSpace(8.dp)
                Segmented(listOf("Off", "On"), if (state.notifyUpdates) 1 else 0, { on ->
                    if (on == 1 && android.os.Build.VERSION.SDK_INT >= 33 &&
                        context.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) !=
                        android.content.pm.PackageManager.PERMISSION_GRANTED
                    ) {
                        notifyPermission.launch(android.Manifest.permission.POST_NOTIFICATIONS)
                    } else {
                        vm.setNotifyUpdates(on == 1)
                    }
                }, Modifier.fillMaxWidth())
            }
        }
        VSpace(12.dp)
        Panel(Modifier.fillMaxWidth()) {
            Column {
                LabelText("Setup")
                VSpace(8.dp)
                BodyText("Save which apps are installed, then set up another device the same way (any JB Theatre Tools reads the file).", maxLines = Int.MAX_VALUE)
                VSpace(12.dp)
                SecondaryButton("Export setup", Modifier.fillMaxWidth()) {
                    exportFile.launch(SetupProfile.suggestedFileName(LocalDate.now()))
                }
                VSpace(10.dp)
                SecondaryButton("Import setup", Modifier.fillMaxWidth(), enabled = !state.showLock && !state.busyAll) {
                    importFile.launch(arrayOf("application/json", "text/plain", "application/octet-stream"))
                }
                if (state.busyAll) {
                    // An import runs from here, so its Stop is here too (Update all's is on the Updates tab).
                    VSpace(10.dp)
                    SecondaryButton("Stop", Modifier.fillMaxWidth(), onClick = vm::stopAll)
                }
            }
        }
        VSpace(12.dp)
        Panel(Modifier.fillMaxWidth()) {
            Column {
                LabelText("Storage")
                VSpace(8.dp)
                BodyText(
                    state.storage?.let { (apps, cache) -> "Installed apps: ${ByteSize.format(apps)} · Download cache: ${ByteSize.format(cache)}" }
                        ?: "Measuring…",
                    maxLines = 2,
                )
                VSpace(12.dp)
                SecondaryButton("Clear download cache", Modifier.fillMaxWidth(), enabled = !state.showLock && !state.anyInstallRunning) {
                    vm.clearCache()
                }
            }
        }
        VSpace(12.dp)
        Panel(Modifier.fillMaxWidth()) {
            Column {
                LabelText("Recent activity")
                VSpace(8.dp)
                if (state.history.isEmpty()) {
                    BodyText("Nothing yet — installs, updates and uninstalls will be listed here.", maxLines = 2)
                } else {
                    val now = LocalDateTime.now()
                    state.history.takeLast(20).asReversed().forEach { e ->
                        // The date wraps ("12 Sep 2026" / "14:02") rather than being cut off at a large font scale.
                        Row(Modifier.fillMaxWidth().padding(vertical = 3.dp)) {
                            SmallText(
                                ActivityHistory.`when`(LocalDateTime.ofInstant(e.at, ZoneId.systemDefault()), now),
                                Modifier.width(116.dp), color = c.text3, weight = FontWeight.Normal, maxLines = 2,
                            )
                            HSpace(6.dp)
                            SmallText(
                                ActivityHistory.describe(e), Modifier.weight(1f),
                                color = if (e.action == "failed") c.danger else c.text2, weight = FontWeight.Normal, maxLines = 3,
                            )
                        }
                    }
                }
            }
        }
        VSpace(12.dp)
    }
}
