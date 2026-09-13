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
            VStack(alignment: .leading, spacing: 18) {
                let pinned = state.pinnedDisplayRows
                let main = state.mainDisplayRows
                if !pinned.isEmpty {
                    gridSection("Pinned", rows: pinned)
                    gridSection("All apps", rows: main)
                } else {
                    gridSection(nil, rows: main)
                }
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    @ViewBuilder
    private func gridSection(_ title: String?, rows: [AppState.Row]) -> some View {
        if !rows.isEmpty {
            VStack(alignment: .leading, spacing: 10) {
                if let title { groupLabel(title) }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 132), spacing: 12)],
                          alignment: .leading, spacing: 12) {
                    ForEach(rows) { row in AppGridTile(row: row) }
                }
            }
        }
    }

    /// Uppercase slate-blue section heading (matches the signed-off prototype's group labels).
    private func groupLabel(_ text: String) -> some View {
        Text(text.uppercased())
            .font(.system(size: 10.5, weight: .bold))
            .tracking(0.8)
            .foregroundStyle(Color.selectorBlue)
            .padding(.horizontal, 10)
            .padding(.top, 8)
            .padding(.bottom, 1)
            .frame(maxWidth: .infinity, alignment: .leading)
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
    /// Each visible row's global frame. Plain (not published) and written from `onPreferenceChange`, so
    /// updating it — which happens on every scroll frame — never re-renders the list (that was the "slow
    /// scroll" bug). The drag gesture reads it to map the cursor to a target slot.
    var frames: [String: CGRect] = [:]
}

/// The working row order during a drag. Reordered LOCALLY (this array only), so the shared model isn't
/// mutated mid-drag — nothing outside the list re-renders, so the drag never janks. Committed on drop.
struct DragOrder { var pinned: [String]; var main: [String] }

/// Reports each visible row's global frame so the drag can map the cursor's Y to a target slot.
struct RowFrameKey: PreferenceKey {
    static var defaultValue: [String: CGRect] = [:]
    static func reduce(value: inout [String: CGRect], nextValue: () -> [String: CGRect]) {
        value.merge(nextValue()) { _, new in new }
    }
}

/// The detailed list, modelled on the web prototype (SortableJS): during a drag the rows are reordered in a
/// LOCAL array — the shared model is never touched until drop — so nothing outside this view re-renders and
/// the drag stays smooth; a separate floating card follows the cursor. A plain `VStack` (only 17 rows, so
/// non-lazy is fine) sidesteps the known LazyVStack-in-ScrollView stutter.
struct ReorderableList: View {
    @EnvironmentObject var state: AppState
    @State private var cursor = DragCursor()
    @State private var draggingId: String?
    @State private var order: DragOrder?

    private var pinnedIds: [String] { order?.pinned ?? state.pinnedDisplayRows.map(\.id) }
    private var mainIds: [String] { order?.main ?? state.mainDisplayRows.map(\.id) }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 2) {
                if !pinnedIds.isEmpty {
                    label("Pinned")
                    ForEach(pinnedIds, id: \.self) { rowView($0) }
                    label("All apps")
                }
                ForEach(mainIds, id: \.self) { rowView($0) }
            }
            .padding(.horizontal, 8)
            .padding(.vertical, 6)
        }
        // The floating card is drawn over the (non-scrolling) viewport; everything is measured in GLOBAL
        // space so the gesture, the frames and the card agree. `origin` converts the global cursor to local.
        .overlay {
            GeometryReader { geo in
                FloatingCard(cursor: cursor, draggingId: draggingId, origin: geo.frame(in: .global).origin)
            }
            .allowsHitTesting(false)
        }
        // Store frames on the (non-observed) cursor object — writing them never re-renders the list, so this
        // fires freely on scroll without bogging it, and the frames are always current when a drag starts.
        .onPreferenceChange(RowFrameKey.self) { cursor.frames = $0 }
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

    private func label(_ text: String) -> some View {
        Text(text.uppercased())
            .font(.system(size: 10.5, weight: .bold)).tracking(0.8)
            .foregroundStyle(Color.selectorBlue)
            .padding(.horizontal, 10).padding(.top, 8).padding(.bottom, 1)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// The lifted card that follows the cursor. Observes only `DragCursor`, and its position is never animated,
/// so it stays glued to the pointer. `cursor.point` is global; `origin` is this overlay's global top-left.
struct FloatingCard: View {
    @EnvironmentObject var state: AppState
    @ObservedObject var cursor: DragCursor
    let draggingId: String?
    let origin: CGPoint

    var body: some View {
        ZStack(alignment: .topLeading) {
            if let id = draggingId, let row = state.rows.first(where: { $0.id == id }) {
                DragPreviewCard(id: id, displayName: row.displayName)
                    .offset(x: cursor.point.x - origin.x - 16, y: cursor.point.y - origin.y - 22)
                    .transaction { $0.animation = nil }   // never ease the card — keep it glued to the cursor
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
    @Binding var order: DragOrder?
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
                    order = DragOrder(pinned: state.pinnedDisplayRows.map(\.id),
                                      main: state.mainDisplayRows.map(\.id))
                }
                cursor.point = value.location           // moves ONLY the floating card (isolated re-render)
                reorderLocally(toY: value.location.y)   // reorders the LOCAL array — no shared-state churn
            }
            .onEnded { _ in
                if let o = order { state.applyDragOrder(pinnedOrder: o.pinned, mainOrder: o.main) }
                order = nil
                draggingId = nil
            }
    }

    /// Moves the dragged row within its group in the LOCAL order to the slot the cursor is over.
    private func reorderLocally(toY y: CGFloat) {
        guard var o = order else { return }
        let pinned = state.isPinned(row.id)
        var ids = pinned ? o.pinned : o.main
        guard let cur = ids.firstIndex(of: row.id) else { return }
        var target = ids.count - 1
        for (i, id) in ids.enumerated() {
            if let f = cursor.frames[id], y < f.midY { target = i; break }
        }
        guard target != cur else { return }
        ids.move(fromOffsets: IndexSet(integer: cur), toOffset: target > cur ? target + 1 : target)
        if pinned { o.pinned = ids } else { o.main = ids }
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
            Button(state.isPinned(row.id) ? "Unpin from Top" : "Pin to Top") { state.togglePin(row.id) }
            Button("Move Up") { state.moveRow(row.id, up: true) }
                .disabled(!state.canMove(row.id, up: true))
            Button("Move Down") { state.moveRow(row.id, up: false) }
                .disabled(!state.canMove(row.id, up: false))
            Button("Hide from List") { state.setHidden(row.id, true) }
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
            guard let dragged = items.first else { return false }
            withAnimation(.spring(response: 0.28, dampingFraction: 0.82)) {
                state.moveRow(dragged, onto: row.id)
            }
            return true
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
