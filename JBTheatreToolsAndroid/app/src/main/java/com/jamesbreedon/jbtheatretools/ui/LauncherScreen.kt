package com.jamesbreedon.jbtheatretools.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import com.jamesbreedon.jbtheatretools.core.AppStatus
import com.jamesbreedon.jbtheatretools.core.Appearance
import com.jamesbreedon.jbtheatretools.core.InstallProgress

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

        Row(Modifier.weight(1f).fillMaxWidth()) {
            // Medium: an 80dp nav rail. Expanded: the desktop launcher's 220dp sidebar (§4).
            if (medium) NavRail(state.tab, vm::selectTab, state.updateCount)
            if (expanded) Sidebar(state.tab, vm::selectTab, state.updateCount, vm.launcherVersion)

            Box(Modifier.weight(1f).fillMaxHeight()) {
                when (state.tab) {
                    Tab.APPS -> if (expanded) {
                        GroupedList(vm, state, gutter)
                    } else {
                        TileGrid(vm, state, gutter, medium)
                    }

                    Tab.UPDATES -> UpdatesList(vm, state, gutter)
                    Tab.ABOUT -> AboutScreen(vm, state, gutter)
                }
            }
        }

        // Pinned primary (§3): one per screen, full width, 48 high.
        if (state.tab == Tab.UPDATES && state.updateCount > 0) {
            Box(
                Modifier.fillMaxWidth().background(c.surface)
                    .padding(start = gutter, end = gutter, top = 12.dp, bottom = 12.dp),
            ) {
                PrimaryButton(
                    text = if (state.busyAll) "Updating…" else "Update all (${state.updateCount})",
                    modifier = Modifier.fillMaxWidth(),
                    enabled = !state.busyAll,
                    onClick = vm::updateAll,
                )
            }
        }

        if (!expanded && !medium) {
            BottomNav(state.tab, vm::selectTab, state.updateCount, bottomInset)
        } else {
            Box(Modifier.height(bottomInset))
        }
    }

    // Long-press actions (§7): Update / Remove / Open release notes.
    state.sheetFor?.let { app ->
        val status = state.statuses.firstOrNull { it.app.id == app.id }
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
                        status?.latestVersion?.let { MonoText("v$it") }
                    }
                }
                VSpace(16.dp)
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
                if (status?.canInstall == true) {
                    SecondaryButton(
                        if (status.isInstalled) "Update" else "Install",
                        Modifier.fillMaxWidth(),
                    ) { vm.install(app) }
                    VSpace(10.dp)
                }
                SecondaryButton("Open release notes", Modifier.fillMaxWidth()) {
                    openUrl(context, vm.releaseNotesUrl(app))
                    vm.showSheet(null)
                }
                if (status?.isInstalled == true) {
                    VSpace(10.dp)
                    GhostButton("Remove", Modifier.fillMaxWidth()) { vm.askRemove(app) }
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
                BodyText("The app is uninstalled from this device. Its saved settings go with it.")
                VSpace(16.dp)
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    SecondaryButton("Cancel", Modifier.weight(1f)) { vm.askRemove(null) }
                    DangerButton("Remove", Modifier.weight(1f)) { vm.confirmRemove(app) }
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
            ) { BodyText(message, color = c.text, maxLines = 2) }
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
            if (status.hasUpdate) {
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
            MonoText("v$version", color = if (status.isInstalled) c.text3 else c.text3)
        } else {
            SmallText(status.note ?: "", color = c.text3, weight = FontWeight.Normal, align = TextAlign.Center)
        }
        if (progress != null && progress.phase != InstallProgress.Phase.DONE &&
            progress.phase != InstallProgress.Phase.FAILED
        ) {
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
                AppRow(status, state.progress[status.app.id], vm)
            }
        }
        val uncategorised = visible.filter { it.app.category == null }
        if (uncategorised.isNotEmpty()) {
            item(key = "header-other") { SectionHeader("Other") }
            items(uncategorised, key = { it.app.id }) { status ->
                AppRow(status, state.progress[status.app.id], vm)
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun AppRow(status: AppStatus, progress: InstallProgress?, vm: LauncherViewModel) {
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
                when {
                    status.hasUpdate -> {
                        MonoText("v${status.installedVersion} → v${status.latestVersion}", color = c.warn)
                        VSpace(6.dp)
                        PrimaryButton("Update", Modifier.width(120.dp)) { vm.install(status.app) }
                    }

                    status.isInstalled -> {
                        MonoText("v${status.installedVersion}", color = c.text3)
                        VSpace(6.dp)
                        SecondaryButton("Open", Modifier.width(120.dp)) { vm.openApp(status.app) }
                    }

                    status.canInstall -> {
                        MonoText("v${status.latestVersion}", color = c.text3)
                        VSpace(6.dp)
                        SecondaryButton("Install", Modifier.width(120.dp)) { vm.install(status.app) }
                    }

                    else -> SmallText(status.note ?: "No release", color = c.text3, weight = FontWeight.Normal)
                }
            }
        }
        if (progress != null && progress.phase != InstallProgress.Phase.DONE) {
            VSpace(8.dp)
            SmallText(progressLine(progress), color = c.text2, weight = FontWeight.Normal)
            VSpace(4.dp)
            HouseProgress(progress.fraction.toFloat(), Modifier.fillMaxWidth())
        }
    }
}

@Composable
private fun UpdatesList(vm: LauncherViewModel, state: LauncherUiState, gutter: androidx.compose.ui.unit.Dp) {
    val c = House.colors
    val pending = state.statuses.filter { it.hasUpdate }
    val notInstalled = state.statuses.filter { !it.isInstalled && it.canInstall }
    LazyColumn(
        Modifier.fillMaxSize(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = gutter, end = gutter, top = 8.dp, bottom = 24.dp,
        ),
    ) {
        if (pending.isEmpty()) {
            item {
                Column(
                    Modifier.fillMaxWidth().padding(top = 40.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    Glyph(HouseIcons.Check, 28.dp, c.ok)
                    VSpace(10.dp)
                    TitleText("Everything is up to date")
                    VSpace(4.dp)
                    BodyText("${state.installedCount} apps installed", maxLines = 1)
                }
            }
        } else {
            item { SectionHeader("Updates available") }
            items(pending, key = { it.app.id }) { AppRow(it, state.progress[it.app.id], vm) }
        }
        if (notInstalled.isNotEmpty()) {
            item { SectionHeader("Not installed") }
            items(notInstalled, key = { it.app.id }) { AppRow(it, state.progress[it.app.id], vm) }
        }
    }
}

private fun progressLine(p: InstallProgress): String = when (p.phase) {
    InstallProgress.Phase.DOWNLOADING -> "Downloading — ${(p.fraction * 100).toInt()}%"
    InstallProgress.Phase.VERIFYING -> "Verifying signature and checksum…"
    InstallProgress.Phase.INSTALLING -> "Installing…"
    InstallProgress.Phase.DONE -> "Installed"
    InstallProgress.Phase.FAILED -> p.message ?: "Failed"
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
                )
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
        item {
            Panel(Modifier.fillMaxWidth()) {
                Column {
                    LabelText("Downloads")
                    VSpace(8.dp)
                    BodyText(
                        "Releases are verified before they install: the release's checksum list must " +
                            "carry the suite signature, the file must match its checksum, and the app " +
                            "must be signed with the suite certificate.",
                        maxLines = 6,
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
                    BodyText(
                        vm.repo.logText().lines().takeLast(6).joinToString("\n").ifBlank { "No entries yet." },
                        maxLines = 8,
                    )
                    VSpace(12.dp)
                    SecondaryButton("Share log", Modifier.fillMaxWidth()) { shareLog(context, vm) }
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
                        maxLines = 4,
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

private fun shareLog(context: android.content.Context, vm: LauncherViewModel) {
    runCatching {
        val intent = android.content.Intent(android.content.Intent.ACTION_SEND).apply {
            type = "text/plain"
            putExtra(android.content.Intent.EXTRA_SUBJECT, "JB Theatre Tools log")
            putExtra(android.content.Intent.EXTRA_TEXT, vm.repo.logText())
            addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(android.content.Intent.createChooser(intent, "Share log"))
    }
}
