import SwiftUI
import AppKit

extension Color {
    /// JB Theatre Tools suite accent (signed-off palette): purple #AF52DE.
    /// Applied via `.tint(...)` (this SwiftPM app has no asset catalog for an AccentColor asset).
    static let jbAccent = Color(red: 175 / 255, green: 82 / 255, blue: 222 / 255)
    /// Shared house "selector" colour (slate-blue #6E8299) for pop-up dropdowns & overflow menus, so the
    /// purple accent stays reserved for primary actions / header / icon (house-style rule 21).
    static let selectorBlue = Color(red: 110 / 255, green: 130 / 255, blue: 153 / 255)
    /// Subtle fill behind a hovered row (adapts to light & dark via the primary label colour).
    static let jbRowHover = Color.primary.opacity(0.06)
    /// Hairline separator drawn between rows.
    static let jbHairline = Color.primary.opacity(0.10)
    /// A raised "card" surface for grid tiles — the window's control background (light card / dark card).
    static let jbSurface = Color(nsColor: .controlBackgroundColor)
}

/// User-selectable window appearance. `.system` follows macOS.
enum AppAppearance: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }

    var label: String {
        switch self {
        case .system: return "System"
        case .light: return "Light"
        case .dark: return "Dark"
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: return nil
        case .light: return .light
        case .dark: return .dark
        }
    }
}

/// When the launcher automatically checks for updates.
enum UpdateCheckMode: String, CaseIterable, Identifiable {
    case everyLaunch, manual, never
    var id: String { rawValue }

    var label: String {
        switch self {
        case .everyLaunch: return "Every launch"
        case .manual: return "Manual only"
        case .never: return "Never"
        }
    }
}

/// What the window's close (X) button does. Default = quit, on both platforms (house convention).
/// `keepRunning` leaves the app alive — in the Dock on macOS, in the system tray on Windows.
enum CloseBehavior: String, CaseIterable, Identifiable {
    case quit, keepRunning
    var id: String { rawValue }

    var label: String {
        switch self {
        case .quit: return "Quit"
        case .keepRunning: return "Keep running"
        }
    }
}

/// How the app catalog is laid out: a detailed list, or a compact icon grid.
enum AppViewMode: String, CaseIterable, Identifiable {
    case list, grid
    var id: String { rawValue }
    var label: String { self == .list ? "List" : "Grid" }
    var symbol: String { self == .list ? "list.bullet" : "square.grid.2x2" }
}

/// Drag timing shared by the row and section drops.
enum DragTiming {
    /// The drop-settle glide (floating card → landing slot). `easeOut` over this exact duration finishes with
    /// no tail/overshoot, so the real row/section is revealed the instant the card lands — no perceptible gap.
    static let dropSettle: Double = 0.16
}

/// Drag payload for reordering whole category SECTIONS. Prefixed with a control char so a section-header
/// drag can never be confused with a row/tile drag (which carry a bare app id) sharing the `String` type.
enum CategoryDrag {
    static let prefix = "\u{2}cat\u{2}"
    static func token(_ key: String) -> String { prefix + key }
    static func key(_ token: String) -> String? {
        token.hasPrefix(prefix) ? String(token.dropFirst(prefix.count)) : nil
    }
}

struct ContentView: View {
    @EnvironmentObject var state: AppState
    @AppStorage("theatre.appearance") private var appearance: AppAppearance = .system
    @AppStorage("theatre.viewMode") private var viewMode: AppViewMode = .list
    @AppStorage("theatre.updateMode") private var updateMode: UpdateCheckMode = .everyLaunch
    @AppStorage("theatre.closeBehavior") private var closeBehavior: CloseBehavior = .quit
    @AppStorage("theatre.installToApplications") private var installToApplications = false
    @AppStorage("theatre.authMode") private var authMode: AuthMode = .server
    @State private var showSettings = false
    @State private var refreshing = false
    @State private var updatingAll = false
    @State private var downloadingAll = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Divider()
            content
            Divider()
            credit
        }
        .frame(minWidth: 600, minHeight: 440)
        .tint(.jbAccent)
        // Intercept the window's close button so "keep running" can hide instead of quit.
        // The closure reads the live setting from UserDefaults at close time.
        .background(WindowCloseConfigurator(shouldKeepRunning: {
            UserDefaults.standard.string(forKey: "theatre.closeBehavior") == CloseBehavior.keepRunning.rawValue
        }))
        .preferredColorScheme(appearance.colorScheme)
        .sheet(isPresented: $showSettings) {
            SettingsView(appearance: $appearance, updateMode: $updateMode, closeBehavior: $closeBehavior,
                         installToApplications: $installToApplications, authMode: $authMode)
                .environmentObject(state)
        }
        .sheet(isPresented: $state.showKeychainExplainer, onDismiss: { state.acknowledgeKeychainExplainer() }) {
            KeychainExplainerView { state.acknowledgeKeychainExplainer() }
        }
        .task { await firstRefresh() }
    }

    private var header: some View {
        HStack(alignment: .center, spacing: 12) {
            Image(systemName: "theatermasks.fill")
                .font(.system(size: 26))
                .foregroundStyle(.tint)
            VStack(alignment: .leading, spacing: 1) {
                Text("JB Theatre Tools").font(.headline)
                Text("Install, update & launch the JB tool suite")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if state.hasVisibleRows {
                Picker("View", selection: $viewMode) {
                    ForEach(AppViewMode.allCases) { mode in
                        Image(systemName: mode.symbol).help(mode.label).tag(mode)
                    }
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
                .help("Switch between list and grid view")
            }
            if state.hasVisibleRows, state.hasCredentials, state.hasAnyToDownload(includeFull: false) {
                Menu {
                    if state.updatesAvailable > 0 {
                        Button {
                            Task { await updateAllAction() }
                        } label: { Label("Update all (\(state.updatesAvailable))", systemImage: "arrow.up.circle") }
                        Divider()
                    }
                    Button {
                        Task { await downloadAllAction(includeFull: false) }
                    } label: { Label("Download all apps", systemImage: "square.and.arrow.down") }
                    if state.hasFullVariants {
                        Button {
                            Task { await downloadAllAction(includeFull: true) }
                        } label: { Label("Download all — including Full editions", systemImage: "square.and.arrow.down.on.square") }
                    }
                } label: {
                    if downloadingAll || updatingAll { ProgressView().controlSize(.small) }
                    else { Label("Download All", systemImage: "arrow.down.circle.fill") }
                }
                .menuStyle(.button)
                .buttonStyle(.borderedProminent)
                .fixedSize()
                .disabled(downloadingAll || updatingAll || refreshing)
                .help("Install or update every app in one go")
            }
            Button {
                Task { await refreshAll() }
            } label: {
                if refreshing { ProgressView().controlSize(.small) }
                else { Label("Refresh", systemImage: "arrow.clockwise") }
            }
            .disabled(refreshing || updatingAll || !state.hasCredentials)
            Button { showSettings = true } label: {
                Label("Settings", systemImage: "gearshape")
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 12)
    }

    @ViewBuilder
    private var content: some View {
        if let err = state.globalError {
            banner(err, systemImage: "exclamationmark.triangle.fill", tint: .red)
            Spacer()
        } else {
            if let v = state.launcherUpdateAvailable { launcherBanner(v) }
            if !state.hasCredentials { credentialsBanner }
            if state.hasCredentials, state.noAppsAccessible {
                banner(authMode == .token
                        ? "This token can’t access any apps. Check the token’s repository access in Settings, or ask James."
                        : "No apps are reachable right now. Check the passphrase in Settings, or ask James.",
                       systemImage: "lock.fill", tint: .orange)
                Spacer()
            } else if !state.hasVisibleRows {
                banner(state.hasHiddenApps
                        ? "Every app is hidden. Show them again from Settings → Hidden apps."
                        : "No apps to show yet. Press Refresh, or check your access in Settings.",
                       systemImage: "eye.slash", tint: .gray)
                Spacer()
            } else if viewMode == .grid {
                gridView
            } else {
                listView
            }
        }
    }

    /// Detailed list layout — a custom gesture-driven reorderable list (see `ReorderableList`). Uses a
    /// `DragGesture` rather than the drag-and-drop system: the dragged row lifts and tracks the cursor at
    /// the display's frame rate, and other rows slide aside. (The drag-and-drop system delivered hover
    /// events only ~7×/sec and trailed a system drag image, which is what felt laggy.)
    private var listView: some View {
        ReorderableList()
    }

    /// Compact icon-grid layout: an icon + name tile per app, pinned apps first. Tiles drag-reorder on
    /// drop (a lifted card + drop-target highlight); a click launches (when installed) or installs; the
    /// full action set lives in the tile's right-click menu.
    private var gridView: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                ForEach(state.displayGroups) { group in
                    VStack(alignment: .leading, spacing: 10) {
                        CategorySectionHeader(group: group)
                        if !state.isCollapsed(group.key) {
                            LazyVGrid(columns: [GridItem(.adaptive(minimum: 132), spacing: 12)],
                                      alignment: .leading, spacing: 12) {
                                ForEach(group.rows) { row in AppGridTile(row: row) }
                            }
                        }
                    }
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private func launcherBanner(_ version: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: "arrow.down.circle.fill").foregroundStyle(Color.jbAccent)
            VStack(alignment: .leading, spacing: 1) {
                Text("JB Theatre Tools \(version) is available").font(.callout).bold()
                Text(state.launcherDownloadMessage ?? "You're running v\(state.currentVersion).")
                    .font(.caption).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            Button {
                Task { await state.downloadLauncherUpdate() }
            } label: {
                if state.launcherDownloading { ProgressView().controlSize(.small) }
                else { Text("Download Update") }
            }
            .disabled(state.launcherDownloading)
        }
        .padding(12)
        .background(Color.jbAccent.opacity(0.12))
    }

    private var credentialsBanner: some View {
        HStack(spacing: 10) {
            Image(systemName: "key.fill").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: 1) {
                Text(state.credentialsPrompt).font(.callout).bold()
                Text(authMode == .token
                     ? "Settings → paste a fine-grained PAT (Contents: read)."
                     : "Settings → enter the suite passphrase (ask James).")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Button("Open Settings") { showSettings = true }
        }
        .padding(12)
        .background(Color.orange.opacity(0.12))
    }

    private var credit: some View {
        Text("Created by: James Breedon & Claude Code  ·  v\(state.currentVersion)")
            .font(.caption2)
            .foregroundColor(.secondary)
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 6)
    }

    private func banner(_ text: String, systemImage: String, tint: Color) -> some View {
        HStack(spacing: 10) {
            Image(systemName: systemImage).foregroundStyle(tint)
            Text(text).font(.callout)
            Spacer()
        }
        .padding(12)
        .background(tint.opacity(0.12))
    }

    private func firstRefresh() async {
        guard updateMode == .everyLaunch, !refreshing else { return }
        if state.hasCredentials { await refreshAll() }
        await state.checkLauncherUpdate()
    }

    private func refreshAll() async {
        refreshing = true
        await state.refreshAll()
        refreshing = false
    }

    private func updateAllAction() async {
        updatingAll = true
        await state.updateAll()
        updatingAll = false
    }

    private func downloadAllAction(includeFull: Bool) async {
        downloadingAll = true
        await state.downloadAll(includeFull: includeFull)
        downloadingAll = false
    }
}

/// A small 2×3 dotted drag handle (the "grip"), shown on row hover — grab it to reorder.
struct DragGrip: View {
    var body: some View {
        VStack(spacing: 3) {
            ForEach(0..<3, id: \.self) { _ in
                HStack(spacing: 3) {
                    Circle().frame(width: 3, height: 3)
                    Circle().frame(width: 3, height: 3)
                }
            }
        }
        .foregroundStyle(Color.selectorBlue)
    }
}

/// The lifted card that follows the cursor during a drag (the `.onDrag` preview) — a compact,
/// shadowed chip with the app's icon and name, shared by the list and grid.
struct DragPreviewCard: View {
    let id: String
    let displayName: String

    var body: some View {
        HStack(spacing: 10) {
            AppIconImage(id: id, displayName: displayName, size: 30)
            Text(displayName).font(.system(size: 13.5, weight: .semibold)).lineLimit(1)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .background(RoundedRectangle(cornerRadius: 11, style: .continuous).fill(.regularMaterial))
        .overlay(RoundedRectangle(cornerRadius: 11, style: .continuous).strokeBorder(Color.jbHairline))
        .shadow(color: .black.opacity(0.30), radius: 14, y: 7)
        .frame(minWidth: 200, alignment: .leading)
    }
}

/// Holds the live cursor point during a drag. A reference type kept in `@State` (which does NOT subscribe to
/// its `objectWillChange`), so updating `point` 60×/sec re-renders ONLY the floating card that observes it —
/// never the list, rows, or header. That isolation is what keeps the card glued to the pointer.
final class DragCursor: ObservableObject {
    @Published var point: CGPoint = .zero
    /// True only during the brief drop-settle: the card is animating from the release point into the
    /// landing slot. While dragging it's false, so `point` changes stay un-eased (card glued to the cursor).
    @Published var settling = false
    /// Each visible row's global frame. Plain (not published) and written from `onPreferenceChange`, so
    /// updating it — which happens on every scroll frame — never re-renders the list (that was the "slow
    /// scroll" bug). The drag gesture reads it to map the cursor to a target slot.
    var frames: [String: CGRect] = [:]
    /// Each section header's global frame (keyed by group key). Same rationale as `frames`; the live section
    /// drag reads these + the section's row frames to compute each section's midpoint.
    var headerFrames: [String: CGRect] = [:]
}

/// The one group being dragged: its key (Pinned sentinel or a category) and its live-reordered ids.
/// Reordered LOCALLY during a drag — the shared model isn't touched until drop, so nothing outside the list
/// re-renders and the drag never janks.
struct DragGroupOrder { var key: String; var ids: [String] }

/// Reports each visible row's global frame so the drag can map the cursor's Y to a target slot.
struct RowFrameKey: PreferenceKey {
    static var defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { _, new in new }
    }
}

/// Reports each section header's global frame (keyed by group key), so a live section drag can map the
/// cursor's Y to a target section.
struct HeaderFrameKey: PreferenceKey {
    static var defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { _, new in new }
    }
}

/// The lifted chip that follows the cursor while dragging a whole category SECTION (shown by `FloatingCard`).
struct CategoryDragChip: View {
    let title: String
    let count: Int

    var body: some View {
        HStack(spacing: 8) {
            Image(systemName: "line.3.horizontal").font(.system(size: 12, weight: .bold))
            Text(title.uppercased()).font(.system(size: 11, weight: .bold)).tracking(0.6)
            Text("\(count)").font(.system(size: 10, weight: .semibold)).opacity(0.6)
        }
        .foregroundStyle(Color.selectorBlue)
        .padding(.horizontal, 12).padding(.vertical, 8)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(.regularMaterial))
        .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color.jbHairline))
        .shadow(color: .black.opacity(0.28), radius: 12, y: 6)
    }
}

/// One line in the flattened list: a section header (by group key) or an app row (by id). Flattening the
/// whole list into ONE `ForEach` with stable per-item identity is what lets a row *glide* when it changes
/// section (e.g. on Pin) rather than teleporting: the `.row(id)` identity survives the move between sections,
/// so SwiftUI animates the position change instead of removing-and-reinserting it.
enum ListItem: Hashable {
    case header(String)   // group key
    case row(String)      // app id
}

/// The detailed list, modelled on the web prototype (SortableJS): during a drag the rows are reordered in a
/// LOCAL array — the shared model is never touched until drop — so nothing outside this view re-renders and
/// the drag stays smooth; a separate floating card follows the cursor. A plain `VStack` (only ~17 rows, so
/// non-lazy is fine) sidesteps the known LazyVStack-in-ScrollView stutter.
struct ReorderableList: View {
    @EnvironmentObject var state: AppState
    @State private var cursor = DragCursor()
    @State private var draggingId: String?
    @State private var order: DragGroupOrder?
    @State private var draggingCategory: String?
    @State private var catOrder: [String]?

    /// Groups in render order — reordered by the live section-drag order while a section is dragged (Pinned
    /// always stays first), else the model order.
    private func orderedGroups(_ groups: [AppState.DisplayGroup]) -> [AppState.DisplayGroup] {
        guard let co = catOrder else { return groups }
        let byKey = Dictionary(groups.map { ($0.key, $0) }, uniquingKeysWith: { a, _ in a })
        var out: [AppState.DisplayGroup] = []
        if let pinned = groups.first(where: { $0.key == AppState.pinnedGroupKey }) { out.append(pinned) }
        for k in co { if let g = byKey[k] { out.append(g) } }
        for g in groups where g.key != AppState.pinnedGroupKey && !co.contains(g.key) { out.append(g) }
        return out
    }

    /// The flattened render order: each group's header, then (unless collapsed) its rows — in the live row
    /// drag order for the group currently being row-dragged, else model order.
    private func items(_ groups: [AppState.DisplayGroup]) -> [ListItem] {
        var out: [ListItem] = []
        for g in groups {
            out.append(.header(g.key))
            guard !state.isCollapsed(g.key) else { continue }
            let ids = (order?.key == g.key) ? (order?.ids ?? g.rows.map(\.id)) : g.rows.map(\.id)
            out.append(contentsOf: ids.map { ListItem.row($0) })
        }
        return out
    }

    var body: some View {
        let groups = orderedGroups(state.displayGroups)
        let byKey = Dictionary(groups.map { ($0.key, $0) }, uniquingKeysWith: { a, _ in a })
        return ScrollView {
            VStack(alignment: .leading, spacing: 2) {
                ForEach(items(groups), id: \.self) { item in
                    switch item {
                    case .header(let key):
                        if let g = byKey[key] {
                            ListSectionHeader(group: g, cursor: cursor,
                                              draggingCategory: $draggingCategory, catOrder: $catOrder)
                                .background(
                                    GeometryReader { geo in
                                        Color.clear.preference(key: HeaderFrameKey.self,
                                                               value: [key: geo.frame(in: .global)])
                                    }
                                )
                        }
                    case .row(let id):
                        rowView(id).transition(.opacity)
                    }
                }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
        }
        // The floating card is drawn over the (non-scrolling) viewport; everything is measured in GLOBAL
        // space so the gesture, the frames and the card agree. `origin` converts the global cursor to local.
        .overlay {
            GeometryReader { geo in
                FloatingCard(cursor: cursor, draggingId: draggingId, draggingCategory: draggingCategory,
                             origin: geo.frame(in: .global).origin)
            }
            .allowsHitTesting(false)
        }
        // Store frames on the (non-observed) cursor object — writing them never re-renders the list, so this
        // fires freely on scroll without bogging it, and the frames are always current when a drag starts.
        .onPreferenceChange(RowFrameKey.self) { cursor.frames = $0 }
        .onPreferenceChange(HeaderFrameKey.self) { cursor.headerFrames = $0 }
    }

    @ViewBuilder
    private func rowView(_ id: String) -> some View {
        if let row = state.rows.first(where: { $0.id == id }) {
            AppRowView(row: row, cursor: cursor, draggingId: $draggingId, order: $order)
                .background(
                    GeometryReader { geo in
                        Color.clear.preference(key: RowFrameKey.self, value: [id: geo.frame(in: .global)])
                    }
                )
        }
    }
}

/// The tap-to-collapse part of a section header: a disclosure chevron + title + app count. Shared by the
/// list and grid headers; clicking it folds/unfolds the section (persisted, per-machine).
struct SectionCollapseLabel: View {
    @EnvironmentObject var state: AppState
    let group: AppState.DisplayGroup
    private var collapsed: Bool { state.isCollapsed(group.key) }

    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: "chevron.right")
                .font(.system(size: 9, weight: .black))
                .rotationEffect(.degrees(collapsed ? 0 : 90))
                .foregroundStyle(Color.selectorBlue)
            Text(group.title.uppercased())
                .font(.system(size: 10.5, weight: .bold)).tracking(0.8)
                .foregroundStyle(Color.selectorBlue)
            Text("\(group.rows.count)")
                .font(.system(size: 10, weight: .semibold))
                .foregroundStyle(Color.selectorBlue.opacity(0.6))
                .padding(.horizontal, 5).padding(.vertical, 0.5)
                .background(Capsule().fill(Color.selectorBlue.opacity(0.12)))
        }
        .contentShape(Rectangle())
        .onTapGesture {
            withAnimation(.spring(response: 0.34, dampingFraction: 0.86)) { state.toggleCollapsed(group.key) }
        }
        .help(collapsed ? "Show \(group.title)" : "Hide \(group.title)")
    }
}

/// The always-visible "REORDER ⣿" grip pill (visual only) — the caller attaches the drag (custom gesture in
/// the list, `.draggable` in the grid). Always shown so it's clearly grab-able, and the sole drag source so
/// dragging a section never fights the collapse tap.
struct ReorderPill: View {
    let hovering: Bool
    var body: some View {
        HStack(spacing: 5) {
            Text("REORDER")
                .font(.system(size: 8.5, weight: .bold)).tracking(0.6)
                .foregroundStyle(Color.selectorBlue.opacity(hovering ? 0.9 : 0.45))
            DragGrip().frame(width: 14).opacity(hovering ? 0.95 : 0.55)
        }
        .padding(.horizontal, 7).padding(.vertical, 3)
        .background(Capsule().fill(Color.selectorBlue.opacity(hovering ? 0.12 : 0.06)))
        .contentShape(Capsule())
    }
}

/// The GRID's section header — collapse label + a `.draggable` REORDER pill; the whole header is a drop
/// target. (The grid keeps the system drag-and-drop; the LIST uses the custom live drag below.)
struct CategorySectionHeader: View {
    @EnvironmentObject var state: AppState
    let group: AppState.DisplayGroup
    @State private var hovering = false
    @State private var isDropTarget = false
    private var isPinned: Bool { group.key == AppState.pinnedGroupKey }

    var body: some View {
        let bar = HStack(spacing: 6) {
            SectionCollapseLabel(group: group)
            Spacer(minLength: 8)
            if !isPinned {
                ReorderPill(hovering: hovering)
                    .draggable(CategoryDrag.token(group.key)) {
                        CategoryDragChip(title: group.title, count: group.rows.count)
                    }
                    .help("Drag onto another section to move “\(group.title)”")
            }
        }
        .padding(.horizontal, 10).padding(.top, 9).padding(.bottom, 3)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(RoundedRectangle(cornerRadius: 8, style: .continuous)
            .fill(isDropTarget ? Color.jbAccent.opacity(0.14) : Color.clear))
        .onHover { hovering = $0 }

        if isPinned {
            bar
        } else {
            bar.dropDestination(for: String.self) { dropped, _ in
                guard let src = dropped.first.flatMap(CategoryDrag.key) else { return false }
                withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) {
                    state.moveCategory(src, onto: group.key)
                }
                return true
            } isTargeted: { isDropTarget = $0 }
        }
    }
}

/// The LIST's section header — same visual, but the REORDER pill carries the SAME custom live-drag as the app
/// rows: a floating chip follows the cursor and the sections reorder live as you pass each one's midpoint
/// (no need to hit the header line), committing on drop with a settle animation.
struct ListSectionHeader: View {
    @EnvironmentObject var state: AppState
    let group: AppState.DisplayGroup
    let cursor: DragCursor
    @Binding var draggingCategory: String?
    @Binding var catOrder: [String]?
    @State private var hovering = false

    private var isPinned: Bool { group.key == AppState.pinnedGroupKey }
    private var isDragging: Bool { draggingCategory == group.key }

    var body: some View {
        HStack(spacing: 6) {
            SectionCollapseLabel(group: group)
            Spacer(minLength: 8)
            if !isPinned {
                ReorderPill(hovering: hovering)
                    .gesture(reorderGesture)
                    .help("Drag to move the “\(group.title)” section")
            }
        }
        .padding(.horizontal, 10).padding(.top, 9).padding(.bottom, 3)
        .frame(maxWidth: .infinity, alignment: .leading)
        .opacity(isDragging ? 0 : 1)
        .overlay { if isDragging { dropSlot } }
        .onHover { hovering = $0 }
    }

    /// Dashed placeholder shown in this header's slot while its section is the one being dragged.
    private var dropSlot: some View {
        RoundedRectangle(cornerRadius: 8, style: .continuous)
            .strokeBorder(Color.jbAccent, style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
            .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(Color.jbAccent.opacity(0.08)))
            .padding(.horizontal, 2)
    }

    private var reorderGesture: some Gesture {
        DragGesture(minimumDistance: 3, coordinateSpace: .global)
            .onChanged { value in
                if draggingCategory != group.key {
                    draggingCategory = group.key
                    catOrder = state.categoryOrderKeys
                }
                cursor.point = value.location          // moves ONLY the floating chip (isolated re-render)
                reorderCategories(toY: value.location.y)
            }
            .onEnded { _ in
                guard let order = catOrder else { draggingCategory = nil; return }
                // Settle the chip into the section's header slot, THEN commit + reveal — so a drop anywhere
                // floats home instead of snapping.
                if let slot = cursor.headerFrames[group.key] {
                    cursor.settling = true
                    withAnimation(.easeOut(duration: DragTiming.dropSettle)) {
                        cursor.point = CGPoint(x: slot.minX + 16, y: slot.minY + 14)
                    }
                    DispatchQueue.main.asyncAfter(deadline: .now() + DragTiming.dropSettle) {
                        state.setCategoryOrder(order)
                        catOrder = nil
                        draggingCategory = nil
                        cursor.settling = false
                    }
                } else {
                    state.setCategoryOrder(order)
                    catOrder = nil
                    draggingCategory = nil
                }
            }
    }

    /// Reorders the LOCAL category order so the dragged section sits where the cursor is — comparing the
    /// cursor Y to each section's vertical MIDPOINT (header top → its last row's bottom), so you don't have
    /// to drag exactly to the header line.
    private func reorderCategories(toY y: CGFloat) {
        guard var order = catOrder, let cur = order.firstIndex(of: group.key) else { return }
        func midY(_ key: String) -> CGFloat {
            guard let h = cursor.headerFrames[key] else { return .greatestFiniteMagnitude }
            let bottom = state.rowIds(inSection: key).compactMap { cursor.frames[$0]?.maxY }.max() ?? h.maxY
            return (h.minY + bottom) / 2
        }
        var target = order.count - 1
        for (i, key) in order.enumerated() where y < midY(key) { target = i; break }
        guard target != cur else { return }
        order.move(fromOffsets: IndexSet(integer: cur), toOffset: target > cur ? target + 1 : target)
        withAnimation(.easeOut(duration: 0.14)) { catOrder = order }
    }
}

/// The lifted card that follows the cursor. Observes only `DragCursor`, and its position is never animated,
/// so it stays glued to the pointer. `cursor.point` is global; `origin` is this overlay's global top-left.
struct FloatingCard: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var cursor: DragCursor
    let draggingId: String?
    let draggingCategory: String?
    let origin: CGPoint

    var body: some View {
        ZStack(alignment: .topLeading) {
            if let id = draggingId, let row = state.rows.first(where: { $0.id == id }) {
                DragPreviewCard(id: id, displayName: row.displayName)
                    .offset(x: cursor.point.x - origin.x - 16, y: cursor.point.y - origin.y - 22)
                    // Glue the card to the cursor while dragging (strip any inherited animation); but DON'T
                    // strip it during the drop-settle, so the card springs into its landing slot.
                    .transaction { if !cursor.settling { $0.animation = nil } }
                    .allowsHitTesting(false)
            } else if let key = draggingCategory,
                      let g = state.displayGroups.first(where: { $0.key == key }) {
                CategoryDragChip(title: g.title, count: g.rows.count)
                    .offset(x: cursor.point.x - origin.x - 16, y: cursor.point.y - origin.y - 14)
                    .transaction { if !cursor.settling { $0.animation = nil } }
                    .allowsHitTesting(false)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }
}

/// A single catalog row: grip, icon, name/blurb/version, status pill, and action buttons. Grabbing the grip
/// starts the drag: the floating card follows the cursor, this row becomes a dashed placeholder, and the
/// other rows slide aside — driven by reordering a LOCAL array, so there's no shared-state churn and it
/// stays smooth. The order is committed to the model on drop.
struct AppRowView: View {
    @EnvironmentObject var state: AppState
    let row: AppState.Row
    let cursor: DragCursor
    @Binding var draggingId: String?
    @Binding var order: DragGroupOrder?
    @State private var hovering = false
    @State private var confirmingUninstall = false

    private var isDragging: Bool { draggingId == row.id }

    var body: some View {
        HStack(alignment: .center, spacing: 11) {
            grip
            AppIconImage(id: row.id, displayName: row.displayName, size: 38)
            infoColumn
            Spacer(minLength: 8)
            statusBadge
            actions
        }
        .opacity(isDragging ? 0 : 1)
        .padding(.vertical, 9)
        .padding(.horizontal, 10)
        .frame(maxWidth: .infinity)
        .background(
            RoundedRectangle(cornerRadius: 11, style: .continuous)
                .fill(hovering && !isDragging ? Color.jbRowHover : Color.clear)
        )
        .overlay(alignment: .bottom) {
            Rectangle().fill(Color.jbHairline)
                .frame(height: 1)
                .padding(.leading, 57)
                .padding(.trailing, 10)
                .opacity(hovering || isDragging ? 0 : 1)
        }
        .overlay { if isDragging { dropSlot } }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .confirmationDialog("Uninstall \(row.displayName)?",
                            isPresented: $confirmingUninstall, titleVisibility: .visible) {
            Button("Uninstall", role: .destructive) { state.uninstall(row.id) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the installed app from your Mac. You can reinstall it anytime.")
        }
    }

    /// The dashed accent placeholder shown in this row's slot while it's the one being dragged.
    private var dropSlot: some View {
        RoundedRectangle(cornerRadius: 11, style: .continuous)
            .strokeBorder(Color.jbAccent, style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
            .background(RoundedRectangle(cornerRadius: 11, style: .continuous).fill(Color.jbAccent.opacity(0.08)))
            .padding(.horizontal, 4)
            .padding(.vertical, 1)
    }

    /// Grip handle: fades in on hover, and carries the reorder `DragGesture`. On macOS a click-drag doesn't
    /// scroll (scrolling is wheel/trackpad), so a plain gesture here doesn't fight the ScrollView.
    private var grip: some View {
        DragGrip()
            .frame(width: 16)
            .opacity(hovering || isDragging ? 0.85 : 0.0)
            .contentShape(Rectangle())
            .gesture(reorderGesture)
            .help("Drag to reorder")
    }

    private var reorderGesture: some Gesture {
        DragGesture(minimumDistance: 3, coordinateSpace: .global)
            .onChanged { value in
                if draggingId != row.id {
                    draggingId = row.id
                    let key = state.groupKey(row)   // Pinned, or this row's category — the only group that reorders
                    let ids = state.displayGroups.first { $0.key == key }?.rows.map(\.id) ?? [row.id]
                    order = DragGroupOrder(key: key, ids: ids)
                }
                cursor.point = value.location           // moves ONLY the floating card (isolated re-render)
                reorderLocally(toY: value.location.y)   // reorders the LOCAL array — no shared-state churn
            }
            .onEnded { _ in
                guard let o = order else { draggingId = nil; return }
                // The dragged row (drawn at opacity 0) still occupies its committed slot in the layout, so
                // its reported frame IS the landing slot. Spring the floating card into that slot, THEN
                // commit the order and reveal the row — so a drop from anywhere (even an invalid spot) floats
                // home instead of snapping back.
                if let slot = cursor.frames[row.id] {
                    let key = o.key, ids = o.ids
                    // `easeOut` (no spring tail/overshoot) finishes EXACTLY at `dropSettle`, so we reveal the
                    // row the instant the card arrives — no post-flight "sit". Reveal + commit happen in one
                    // synchronous block → the card vanishes and the row appears in the same frame, seamlessly.
                    cursor.settling = true
                    withAnimation(.easeOut(duration: DragTiming.dropSettle)) {
                        cursor.point = CGPoint(x: slot.minX + 16, y: slot.minY + 22)
                    }
                    DispatchQueue.main.asyncAfter(deadline: .now() + DragTiming.dropSettle) {
                        state.applyGroupOrder(key: key, orderedIds: ids)
                        order = nil
                        draggingId = nil
                        cursor.settling = false
                    }
                } else {
                    state.applyGroupOrder(key: o.key, orderedIds: o.ids)
                    order = nil
                    draggingId = nil
                }
            }
    }

    /// Moves the dragged row within its group's LOCAL order to the slot the cursor is over.
    private func reorderLocally(toY y: CGFloat) {
        guard var o = order else { return }
        var ids = o.ids
        guard let cur = ids.firstIndex(of: row.id) else { return }
        var target = ids.count - 1
        for (i, id) in ids.enumerated() {
            if let f = cursor.frames[id], y < f.midY { target = i; break }
        }
        guard target != cur else { return }
        ids.move(fromOffsets: IndexSet(integer: cur), toOffset: target > cur ? target + 1 : target)
        o.ids = ids
        withAnimation(.easeOut(duration: 0.10)) { order = o }
    }

    private var infoColumn: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 5) {
                Text(row.displayName).font(.system(size: 13.5, weight: .semibold)).lineLimit(1)
                if state.isPinned(row.id) {
                    Image(systemName: "pin.fill").font(.system(size: 9)).foregroundStyle(Color.selectorBlue)
                }
            }
            Text(row.app.blurb).font(.system(size: 11.5)).foregroundStyle(.secondary).lineLimit(1)
            versionLine
            whatsNewLine
            variantToggle
            if row.busy {
                ProgressView(value: row.progress)
                    .frame(maxWidth: 240)
                    .controlSize(.small)
            }
        }
    }

    private var versionLine: some View {
        HStack(spacing: 6) {
            Text("Installed: \(installedText)")
            Text("·").foregroundStyle(.secondary)
            Text("Latest: \(row.latest ?? "—")")
        }
        .font(.caption2)
        .foregroundStyle(.secondary)
    }

    /// The installed version of the SELECTED variant's slot, annotated with that variant's label for
    /// apps that ship variants (each variant is its own install; the toggle picks which one is shown).
    private var installedText: String {
        guard let v = row.installed else { return "—" }
        if let variant = state.selectedVariantLabel(row.app) { return "\(v) (\(variant))" }
        return v
    }

    /// A compact Standard/Full toggle, shown inline only for apps that ship variants.
    @ViewBuilder
    private var variantToggle: some View {
        if row.app.hasVariants, let vs = row.app.variants {
            Picker("", selection: Binding(
                get: { state.selectedVariantId(row.app) ?? vs.first?.id ?? "" },
                set: { state.setVariant(row.id, $0) }
            )) {
                ForEach(vs) { Text($0.label).tag($0.id) }
            }
            .pickerStyle(.segmented)
            .labelsHidden()
            .controlSize(.mini)
            .fixedSize()
            .tint(.selectorBlue)
            .disabled(row.busy)
        }
    }

    /// One-line "what's new" for the app's current release, shown only when the catalog carries it.
    /// Labelled "New in vX.Y.Z:" when a version is present, else "What's new:".
    @ViewBuilder
    private var whatsNewLine: some View {
        if let note = row.app.whatsNew, !note.isEmpty {
            (
                Text(whatsNewLabel).fontWeight(.semibold)
                + Text(" ") + Text(note)
            )
            .font(.caption2)
            .foregroundStyle(Color.selectorBlue)
            .lineLimit(2)
            .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var whatsNewLabel: String {
        if let v = row.app.whatsNewVersion, !v.isEmpty { return "New in \(v):" }
        return "What's new:"
    }

    @ViewBuilder
    private var statusBadge: some View {
        switch row.status {
        case .checking:
            ProgressView().controlSize(.small)
        case .upToDate:
            badge("Up to date", color: .green)
        case .updateAvailable:
            badge("Update", color: .jbAccent)
        case .notInstalled:
            badge("Not installed", color: .secondary)
        case .installed:
            badge("Installed", color: .secondary)
        case .noRelease:
            badge("No release", color: .secondary)
        case .missingAsset:
            badge("No macOS build", color: .orange)
        case .error(let msg):
            badge("Error", color: .red).help(msg)
        case .noAccess:
            // Row is filtered out of the list; nothing to show.
            EmptyView()
        case .unknown:
            EmptyView()
        }
    }

    private func badge(_ text: String, color: Color) -> some View {
        Text(text)
            .font(.caption2).bold()
            .padding(.horizontal, 9).padding(.vertical, 3)
            .background(color.opacity(0.15))
            .foregroundStyle(color)
            .clipShape(Capsule())
    }

    @ViewBuilder
    private var actions: some View {
        HStack(spacing: 8) {
            // Install / Update / Retry — depends on the checked status.
            switch row.status {
            case .notInstalled:
                installButton(title: "Install")
            case .updateAvailable:
                installButton(title: "Update")
            case .error:
                installButton(title: row.installed == nil ? "Install" : "Retry")
            default:
                EmptyView()
            }
            // Launch — available whenever something is installed, even before a refresh has run.
            if row.installed != nil { launchButton }
            rowMenu   // every visible row has the ⋯ menu (reordering is always available)
        }
        .controlSize(.small)
    }

    /// Overflow menu: pin/hide/reorder, pick a variant, install a specific (older) version, or uninstall.
    private var rowMenu: some View {
        Menu {
            AppMenuButtons(row: row, requestUninstall: { confirmingUninstall = true })
        } label: {
            Image(systemName: "ellipsis.circle")
        }
        .menuStyle(.borderlessButton)
        .tint(.selectorBlue)   // house rule 21: selectors/menus are slate-blue, not the purple accent
        .fixedSize()
        .disabled(row.busy)
        .help("Variant, reorder, other versions & uninstall")
    }

    private func installButton(title: String) -> some View {
        Button(title) {
            Task { await state.install(row.id) }
        }
        .buttonStyle(.borderedProminent)
        .disabled(row.busy || row.latestAssetId == nil)
    }

    private var launchButton: some View {
        Button("Launch") { state.launch(row.id) }
            .buttonStyle(.bordered)
            .disabled(row.busy)
    }
}

/// The app's icon at a given size: the installed app's REAL icon once installed; else the per-app icon
/// bundled in the launcher (Resources/<id>.png); else a tinted monogram tile. Shared by list & grid.
struct AppIconImage: View {
    let id: String
    let displayName: String
    var size: CGFloat = 40

    /// `NSWorkspace.icon(forFile:)` is a comparatively heavy call; cache the result per path so a redraw
    /// (e.g. during a download) doesn't re-resolve it every time (audit F12). A path's icon is stable for
    /// the session; a reinstall reuses the same path, which is fine.
    private static let iconCache = NSCache<NSString, NSImage>()
    private static func installedIcon(_ path: String) -> NSImage {
        if let hit = iconCache.object(forKey: path as NSString) { return hit }
        let img = NSWorkspace.shared.icon(forFile: path)
        iconCache.setObject(img, forKey: path as NSString)
        return img
    }

    var body: some View {
        if let path = InstallManager.shared.installedPath(id)?.path {
            Image(nsImage: Self.installedIcon(path))
                .resizable().interpolation(.high)
                .frame(width: size, height: size)
        } else if let bundled = Self.bundledIcon(id) {
            Image(nsImage: bundled)
                .resizable().interpolation(.high)
                .frame(width: size, height: size)
        } else {
            RoundedRectangle(cornerRadius: size * 0.22, style: .continuous)
                .fill(Color.jbAccent.opacity(0.15))
                .frame(width: size, height: size)
                .overlay(
                    Text(displayName.first.map { String($0).uppercased() } ?? "•")
                        .font(.system(size: size * 0.45, weight: .semibold))
                        .foregroundStyle(Color.jbAccent)
                )
        }
    }

    /// Cache decoded bundled icons too (audit F12 cached only the installed-app icon) — otherwise a
    /// not-installed row re-reads and re-decodes its PNG from the app bundle on every redraw, which during
    /// a drag-reorder (all rows redraw per frame) stutters the main thread.
    private static let bundledCache = NSCache<NSString, NSImage>()
    static func bundledIcon(_ id: String) -> NSImage? {
        if let hit = bundledCache.object(forKey: id as NSString) { return hit }
        guard let url = Bundle.main.url(forResource: id, withExtension: "png"),
              let img = NSImage(contentsOf: url) else { return nil }
        bundledCache.setObject(img, forKey: id as NSString)
        return img
    }
}

/// The per-row action menu, shared by the list row's ⋯ button and the grid tile's right-click menu:
/// pin/hide/reorder, pick a variant (Standard/Full), install a specific version, Dock/alias, uninstall.
struct AppMenuButtons: View {
    @EnvironmentObject var state: AppState
    let row: AppState.Row
    /// Called when the user picks Uninstall — the host view shows its own confirmation dialog.
    var requestUninstall: () -> Void

    var body: some View {
        Group {
            Button(state.isPinned(row.id) ? "Unpin from Top" : "Pin to Top") {
                withAnimation(.spring(response: 0.42, dampingFraction: 0.82)) { state.togglePin(row.id) }
            }
            Button("Move Up") {
                withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) { state.moveRow(row.id, up: true) }
            }
            .disabled(!state.canMove(row.id, up: true))
            Button("Move Down") {
                withAnimation(.spring(response: 0.3, dampingFraction: 0.85)) { state.moveRow(row.id, up: false) }
            }
            .disabled(!state.canMove(row.id, up: false))
            Button("Hide from List") {
                withAnimation(.easeInOut(duration: 0.22)) { state.setHidden(row.id, true) }
            }
            if row.app.hasVariants, let vs = row.app.variants {
                Divider()
                Picker("Variant", selection: Binding(
                    get: { state.selectedVariantId(row.app) ?? vs.first?.id ?? "" },
                    set: { state.setVariant(row.id, $0) }
                )) {
                    ForEach(vs) { Text($0.label).tag($0.id) }
                }
            }
            if !row.releases.isEmpty {
                Divider()
                Section("Install version") {
                    ForEach(row.releases) { rel in
                        Button { Task { await state.install(row.id, tag: rel.tagName) } }
                            label: { Text(versionLabel(rel)) }
                    }
                }
            }
            if row.installed != nil {
                Divider()
                Button(state.isDockPinned(row.id) ? "Remove from Dock" : "Add to Dock") {
                    state.toggleDockPin(row.id)
                }
                Button(state.hasDesktopAlias(row.id) ? "Remove Desktop Alias" : "Add Desktop Alias") {
                    state.toggleDesktopAlias(row.id)
                }
                Divider()
                Button("Uninstall \(row.displayName)", role: .destructive) { requestUninstall() }
            }
        }
    }

    private func versionLabel(_ rel: ReleaseInfo) -> String {
        var s = rel.tagName
        if rel.prerelease { s += " (pre-release)" }
        if rel.tagName == row.installed { s += "  ✓ installed" }
        return s
    }
}

/// One tile in the grid view: a large icon + name and a compact status line, on a raised card. A click
/// launches the app (if installed) or installs it; the full action set lives in the right-click menu.
/// Dragging a tile lifts a card and reorders it within its pin group on drop (the drop target highlights).
struct AppGridTile: View {
    @EnvironmentObject var state: AppState
    let row: AppState.Row
    @State private var hovering = false
    @State private var isDropTarget = false
    @State private var confirmingUninstall = false

    var body: some View {
        VStack(spacing: 8) {
            AppIconImage(id: row.id, displayName: row.displayName, size: 52)
            Text(row.displayName)
                .font(.system(size: 12.5, weight: .semibold))
                .multilineTextAlignment(.center)
                .lineLimit(2)
                .frame(height: 30)
            statusCaption
        }
        .frame(maxWidth: .infinity)
        .frame(height: 140)
        .padding(8)
        .background(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .fill(Color.jbSurface)
                .shadow(color: .black.opacity(hovering ? 0.14 : 0.05),
                        radius: hovering ? 6 : 2, y: hovering ? 3 : 1)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(Color.jbHairline)
        )
        .overlay(alignment: .topTrailing) {
            if state.isPinned(row.id) {
                Image(systemName: "pin.fill").font(.caption2)
                    .foregroundStyle(Color.selectorBlue).padding(8)
            }
        }
        .overlay(alignment: .bottom) {
            if row.busy {
                ProgressView(value: row.progress).controlSize(.small)
                    .padding(.horizontal, 12).padding(.bottom, 8)
            }
        }
        .overlay {
            if isDropTarget {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .strokeBorder(Color.jbAccent, style: StrokeStyle(lineWidth: 2, dash: [5, 4]))
                    .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(Color.jbAccent.opacity(0.08)))
            }
        }
        .contentShape(Rectangle())
        .onHover { hovering = $0 }
        .onTapGesture { primaryAction() }
        .draggable(row.id) {
            DragPreviewCard(id: row.id, displayName: row.displayName)
        }
        .dropDestination(for: String.self) { items, _ in
            // Only a tile drag (a bare app id) reorders here — ignore a section-header drag (category token).
            guard let dragged = items.first, CategoryDrag.key(dragged) == nil else { return false }
            var moved = false
            withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                moved = state.moveRow(dragged, onto: row.id)
            }
            // Return whether it moved: a drop onto another section is a no-op, so `false` floats the tile home.
            return moved
        } isTargeted: { isDropTarget = $0 }
        .contextMenu { AppMenuButtons(row: row, requestUninstall: { confirmingUninstall = true }) }
        .confirmationDialog("Uninstall \(row.displayName)?",
                            isPresented: $confirmingUninstall, titleVisibility: .visible) {
            Button("Uninstall", role: .destructive) { state.uninstall(row.id) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("This removes the installed app from your Mac. You can reinstall it anytime.")
        }
        .help(tooltip)
    }

    /// Click behaviour: launch when installed, otherwise install (when the status allows it).
    private func primaryAction() {
        if row.busy { return }
        if row.installed != nil { state.launch(row.id); return }
        switch row.status {
        case .notInstalled, .updateAvailable, .error: Task { await state.install(row.id) }
        default: break
        }
    }

    @ViewBuilder
    private var statusCaption: some View {
        let variant = state.selectedVariantLabel(row.app)
        Text(captionText(variant: variant))
            .font(.caption2)
            .foregroundStyle(captionColor)
            .lineLimit(1)
    }

    private func captionText(variant: String?) -> String {
        let base: String
        switch row.status {
        case .updateAvailable: base = "Update"
        case .upToDate:        base = row.installed ?? "Installed"
        case .installed:       base = row.installed ?? "Installed"
        case .notInstalled:    base = "Install"
        case .noRelease:       base = "No release"
        case .missingAsset:    base = "No macOS build"
        case .checking:        base = "Checking…"
        case .error:           base = "Error"
        default:               base = row.installed ?? " "
        }
        if let v = variant, row.app.hasVariants { return "\(base) · \(v)" }
        return base
    }

    private var captionColor: Color {
        switch row.status {
        case .updateAvailable: return .jbAccent
        case .upToDate:        return .green
        case .missingAsset, .error: return .orange
        default:               return .secondary
        }
    }

    private var tooltip: String {
        var t = row.displayName
        if let v = row.installed {
            t += " — installed \(v)"
            if let vl = state.selectedVariantLabel(row.app) { t += " (\(vl))" }
        } else if row.status == .notInstalled {
            t += " — click to install"
        }
        return t
    }
}
