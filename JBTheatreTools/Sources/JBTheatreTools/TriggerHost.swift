import Foundation
import Darwin

/// What a launch of this binary is: the headless trigger host, the command line, or the app's window.
enum LaunchMode: Equatable {
    case triggerHost, cli, gui

    /// `--trigger-host` wins over everything (Stagehand starts it with exactly that); then the CLI's verbs; else the GUI.
    static func of(_ args: [String]) -> LaunchMode {
        if args.contains(TriggerRegistration.hostArg) { return .triggerHost }
        if args.contains(where: { CLI.commands.contains($0) }) { return .cli }
        return .gui
    }
}

/// The launcher's registration with Stagehand, which keeps a hidden `--trigger-host` copy running so Dashboard buttons
/// keep working when JB Theatre Tools is closed (and after a restart). Plain files in Stagehand's state folder:
/// - `triggers/jbtheatretools.json` = {app_id, name, exe, args, registered} — written when the trigger is switched on
///   (and at every start while it's on, so `exe` follows an update), removed when it's switched off;
/// - `triggers/_jbtheatretools-host.json` = {pid, port, started} — written by the hidden copy while IT answers presses,
///   so Settings can say who is answering (Stagehand skips files that start with "_").
/// Both are written atomically and readable by this user only (0600).
enum TriggerRegistration {
    static let appId = "jbtheatretools"
    static let name = "JB Theatre Tools"
    static let hostArg = "--trigger-host"

    /// Stagehand's state folder: ~/Library/Application Support/JBTheatreTools/stagehand/, or <STAGEHAND_HOME>/stagehand.
    static func stateDir(environment: [String: String] = ProcessInfo.processInfo.environment,
                         home: URL = FileManager.default.homeDirectoryForCurrentUser) -> URL {
        if let h = environment["STAGEHAND_HOME"], !h.isEmpty {
            return URL(fileURLWithPath: h, isDirectory: true).appendingPathComponent("stagehand", isDirectory: true)
        }
        return home.appendingPathComponent("Library/Application Support/JBTheatreTools/stagehand", isDirectory: true)
    }

    static func triggersDir(_ stateDir: URL) -> URL { stateDir.appendingPathComponent("triggers", isDirectory: true) }
    static func registrationURL(_ stateDir: URL) -> URL { triggersDir(stateDir).appendingPathComponent("\(appId).json") }
    static func hostMarkURL(_ stateDir: URL) -> URL { triggersDir(stateDir).appendingPathComponent("_\(appId)-host.json") }
    static func windowMarkURL(_ stateDir: URL) -> URL { triggersDir(stateDir).appendingPathComponent("_\(appId)-window.json") }

    /// This launcher's program: the .app's Contents/MacOS binary.
    static var launcherExecutable: String {
        (Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0])).resolvingSymlinksInPath().path
    }

    private static func iso(_ date: Date) -> String {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f.string(from: date)
    }

    static func document(exe: String, now: Date) -> [String: Any] {
        ["app_id": appId, "name": name, "exe": exe, "args": [hostArg], "registered": iso(now)]
    }

    /// Writes the registration. False (and logged by the caller) when it couldn't be written.
    @discardableResult
    static func register(exe: String, stateDir: URL, now: Date = Date()) -> Bool {
        guard let data = try? JSONSerialization.data(withJSONObject: document(exe: exe, now: now),
                                                     options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        else { return false }
        return (try? writePrivate(data, to: registrationURL(stateDir))) != nil
    }

    /// Removes the registration (switched off). True if there was one.
    @discardableResult
    static func unregister(stateDir: URL) -> Bool {
        (try? FileManager.default.removeItem(at: registrationURL(stateDir))) != nil
    }

    /// Atomic and private: a temp file created 0600 next to it (not ending ".json", so Stagehand never reads a half
    /// file), then renamed over the old one. The folder is made this-user-only.
    static func writePrivate(_ data: Data, to url: URL) throws {
        let fm = FileManager.default
        let dir = url.deletingLastPathComponent()
        if !fm.fileExists(atPath: dir.path) {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        }
        let tmp = dir.appendingPathComponent(".\(url.lastPathComponent).tmp\(getpid())")
        try? fm.removeItem(at: tmp)
        guard fm.createFile(atPath: tmp.path, contents: data, attributes: [.posixPermissions: 0o600]) else {
            throw CocoaError(.fileWriteUnknown)
        }
        chmod(tmp.path, 0o600)   // createFile's attributes go through the umask on some systems
        guard rename(tmp.path, url.path) == 0 else {
            try? fm.removeItem(at: tmp)
            throw CocoaError(.fileWriteUnknown)
        }
    }

    // MARK: - Who is answering presses

    static func markHost(pid: Int32, port: Int, stateDir: URL, now: Date = Date()) {
        let doc: [String: Any] = ["pid": Int(pid), "port": port, "started": iso(now)]
        guard let data = try? JSONSerialization.data(withJSONObject: doc, options: [.sortedKeys]) else { return }
        try? writePrivate(data, to: hostMarkURL(stateDir))
    }

    /// Removes the mark only when it is still this process's.
    static func clearHost(pid: Int32, stateDir: URL) { clearMark(hostMarkURL(stateDir), pid: pid) }

    private static func markedPid(_ url: URL) -> Int32? {
        guard let data = try? Data(contentsOf: url),
              let doc = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let pid = doc["pid"] as? Int else { return nil }
        return Int32(truncatingIfNeeded: pid)
    }

    private static func clearMark(_ url: URL, pid: Int32) {
        guard markedPid(url) == pid else { return }
        try? FileManager.default.removeItem(at: url)
    }

    /// The launcher itself is running (window open, or kept running in the Dock): it answers presses, so the hidden copy
    /// lets go of the port — the window's show lock, downloads and installs are then never bypassed.
    static func markWindow(pid: Int32, stateDir: URL) {
        guard let data = try? JSONSerialization.data(withJSONObject: ["pid": Int(pid)], options: [.sortedKeys]) else { return }
        try? writePrivate(data, to: windowMarkURL(stateDir))
    }

    static func clearWindow(pid: Int32, stateDir: URL) { clearMark(windowMarkURL(stateDir), pid: pid) }

    /// A launcher (not this process) is running: its mark is there and that pid is still this launcher's program.
    static func windowRunning(stateDir: URL, ownPid: Int32 = getpid(), isLauncher: (Int32) -> Bool = isLauncherProcess) -> Bool {
        guard let pid = markedPid(windowMarkURL(stateDir)), pid != ownPid else { return false }
        return isLauncher(pid)
    }

    /// The pid is alive and runs this launcher's program (a crashed launcher's pid reused by something else doesn't count).
    static func isLauncherProcess(_ pid: Int32) -> Bool {
        guard pidAlive(pid) else { return false }
        // KERN_PROCARGS2: argc, then the executable path (as InstallGuard.processArguments reads it).
        var mib: [Int32] = [CTL_KERN, KERN_PROCARGS2, pid]
        var size = 0
        guard sysctl(&mib, 3, nil, &size, nil, 0) == 0, size > MemoryLayout<Int32>.size else { return false }
        var buffer = [UInt8](repeating: 0, count: size)
        guard sysctl(&mib, 3, &buffer, &size, nil, 0) == 0 else { return false }
        let path = buffer[MemoryLayout<Int32>.size..<size].prefix { $0 != 0 }
        return URL(fileURLWithPath: String(decoding: path, as: UTF8.self)).resolvingSymlinksInPath().path == launcherExecutable
    }

    static func pidAlive(_ pid: Int32) -> Bool {
        guard pid > 0 else { return false }
        return kill(pid, 0) == 0 || errno == EPERM
    }

    /// The hidden copy is listening right now (its mark is there and its process is alive).
    static func backgroundAnswering(stateDir: URL, alive: (Int32) -> Bool = pidAlive) -> Bool {
        guard let data = try? Data(contentsOf: hostMarkURL(stateDir)),
              let doc = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let pid = doc["pid"] as? Int else { return false }
        return alive(Int32(pid))
    }
}

/// Stagehand, the suite app that keeps the hidden copies running.
enum StagehandLink {
    static let ensureArgs = ["--trigger-hosts", "--ensure"]

    /// The program inside an installed Stagehand: a .app's Contents/MacOS/<CFBundleExecutable>; a plain file as it is.
    static func executable(installed: URL?) -> URL? {
        guard let installed else { return nil }
        guard installed.pathExtension == "app" else {
            return FileManager.default.isExecutableFile(atPath: installed.path) ? installed : nil
        }
        let plist = installed.appendingPathComponent("Contents/Info.plist")
        let name = (NSDictionary(contentsOf: plist)?["CFBundleExecutable"] as? String) ?? installed.deletingPathExtension().lastPathComponent
        let exe = installed.appendingPathComponent("Contents/MacOS").appendingPathComponent(name)
        return FileManager.default.isExecutableFile(atPath: exe.path) ? exe : nil
    }

    static func installedExecutable() -> URL? {
        executable(installed: InstallManager.shared.installedPath(AppState.stagehandId))
    }

    /// Asks Stagehand to make sure its supervisor runs (`--trigger-hosts --ensure`): started and left to itself, never
    /// waited on, never fatal. False when it couldn't even start.
    @discardableResult
    static func ensure(_ exe: URL, spawn: (URL, [String]) throws -> Void = spawnDetached, log: (String) -> Void) -> Bool {
        do {
            try spawn(exe, ensureArgs)
            log("web trigger: asked Stagehand to keep the background copy running")
            return true
        } catch {
            log("web trigger: couldn't ask Stagehand to keep the background copy running (\(error.localizedDescription))")
            return false
        }
    }

    static func spawnDetached(_ exe: URL, _ args: [String]) throws {
        let proc = Process()
        proc.executableURL = exe
        proc.arguments = args
        proc.standardInput = FileHandle.nullDevice
        proc.standardOutput = FileHandle.nullDevice
        proc.standardError = FileHandle.nullDevice
        try proc.run()   // Foundation reaps it when it exits; nothing waits here
    }
}

/// The trigger's settings as saved by the window (UserDefaults), read fresh by the hidden copy.
struct TriggerSettings: Equatable {
    var enabled: Bool
    var bind: String
    var port: Int

    static func current(_ d: UserDefaults = .standard) -> TriggerSettings {
        let p = d.integer(forKey: AppState.webTriggerPortKey)
        return TriggerSettings(enabled: d.bool(forKey: AppState.webTriggerEnabledKey),
                               bind: d.string(forKey: AppState.webTriggerBindKey) ?? WebTriggerContract.defaultBind,
                               port: p == 0 ? WebTriggerContract.defaultPort : p)
    }
}

/// The hidden copy's loop: every few seconds it re-reads the saved settings — switched off → it lets go of the port
/// and ends (exit 0); otherwise it keeps owning the port (waiting while the launcher's window has it, taking over when
/// the window quits). Marks itself in Stagehand's folder while it answers presses.
@MainActor
final class TriggerHostRunner {
    let owner: WebTriggerOwner
    private let settings: () -> TriggerSettings
    private let readToken: () -> String
    private let stateDir: URL
    private let log: (String) -> Void
    private let pid: Int32
    private let windowOpen: () -> Bool
    private var marked = false
    private var lastNote = ""

    init(owner: WebTriggerOwner, settings: @escaping () -> TriggerSettings, readToken: @escaping () -> String,
         stateDir: URL, pid: Int32 = getpid(), windowOpen: @escaping () -> Bool = { false },
         log: @escaping (String) -> Void) {
        self.owner = owner
        self.windowOpen = windowOpen
        self.settings = settings
        self.readToken = readToken
        self.stateDir = stateDir
        self.pid = pid
        self.log = log
        owner.onChange = { [weak self] in self?.syncMark() }
    }

    private func note(_ line: String) {
        if line != lastNote { log(line) }
        lastNote = line
    }

    /// One pass. nil = keep going; a number = end with that exit code.
    func tick() async -> Int32? {
        let s = settings()
        guard s.enabled else {
            owner.stop()
            syncMark()
            log("the web trigger is switched off — the background copy stops")
            return 0
        }
        if windowOpen() {   // the launcher is running: it answers presses itself
            owner.stop()
            syncMark()
            note("the launcher is running — it answers presses")
            return nil
        }
        let word = readToken()
        owner.server.setToken(word)
        let bind = s.bind.trimmingCharacters(in: .whitespaces)
        let problem = WebTriggerContract.bindProblem(bind: bind, port: String(s.port), tokenSet: !word.isEmpty)
        if !problem.isEmpty {
            owner.stop()
            syncMark()
            note("not listening — \(problem)")
            return nil
        }
        let err = await owner.own(bind: bind, port: s.port)
        note(err.isEmpty ? "" : err)
        syncMark()
        return nil
    }

    func run(every seconds: TimeInterval) async -> Int32 {
        log("background copy started (pid \(pid))")
        let nanos = UInt64(max(seconds, 0.05) * 1_000_000_000)
        while true {
            if let code = await tick() { return code }
            try? await Task.sleep(nanoseconds: nanos)
        }
    }

    private func syncMark() {
        let owning = owner.state == .owning && owner.server.running
        if owning && !marked {
            TriggerRegistration.markHost(pid: pid, port: owner.server.port, stateDir: stateDir)
            marked = true
        } else if !owning && marked {
            TriggerRegistration.clearHost(pid: pid, stateDir: stateDir)
            marked = false
        }
    }
}

/// What the actions see of the launcher in the hidden copy: no window, no model — the saved settings, the install
/// manifest and the catalog, read again on every press. Opening an app launches it exactly as the window's Open does;
/// show lock on is the shared lock (this launcher's setting + claude.json, which the window and every app follow).
enum HeadlessTriggerHost {
    /// Where the hidden copy reads and writes (tests pass stand-ins).
    struct Deps {
        var defaults: UserDefaults = .standard
        var claudeURL: URL = ClaudeSettingsFile.url
        var installs: InstallManager = .shared
        /// nil = a person has the slot's app open (NSWorkspace); tests stub it.
        var isOpen: ((String) -> Bool)?
        /// nil = InstallManager's launch (`open`); tests stub it.
        var launch: ((String) throws -> Void)?
        var version: String = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0.0"
    }

    static func apps(_ catalog: Catalog) -> [LauncherActions.App] {
        catalog.apps.map { app in
            let editions: [String?] = app.hasVariants ? (app.variants ?? []).map(\.id).reversed() : [nil]
            return LauncherActions.App(id: app.id, name: app.name, slots: editions.map { vid in
                LauncherActions.Slot(key: app.installKey(variantId: vid), suffix: app.variantSuffix(vid))
            })
        }
    }

    static func make(catalog: Catalog, deps: Deps = Deps(), log: @escaping (String) -> Void) -> LauncherActionHost {
        make(apps: apps(catalog), deps: deps, log: log)
    }

    static func make(apps: [LauncherActions.App], deps: Deps, log: @escaping (String) -> Void) -> LauncherActionHost {
        let d = deps.defaults, installs = deps.installs, claude = deps.claudeURL
        func lockOn() -> Bool { d.bool(forKey: AppState.showLockKey) || ClaudeSettingsFile.suiteShowLock(at: claude) == true }
        return LauncherActionHost(
            showLock: lockOn,
            turnShowLockOn: { source in
                d.set(true, forKey: AppState.showLockKey)
                ClaudeSettingsFile.setSuiteShowLock(true, at: claude)
                log("show lock on (\(source))")
                return 0   // the hidden copy runs no downloads
            },
            apps: { apps },
            tickedApps: { Set(d.stringArray(forKey: AppState.webTriggerAppsKey) ?? []) },
            installedVersion: { installs.installedVersion($0) },
            slotBusy: { _ in false },
            isOpen: deps.isOpen ?? { installs.runningInstance($0) != nil },
            launch: deps.launch ?? { try installs.launch(installKey: $0) },
            facts: {
                let installed = apps.filter { $0.slots.contains { installs.installedVersion($0.key) != nil } }.count
                return LauncherActions.Facts(showLock: lockOn(), installed: installed, updates: 0, lastCheck: nil, version: deps.version)
            },
            log: log)
    }

    /// The trigger was switched off in the window: refused from the very next press (the loop lets go within 3 s).
    static func offReply() -> WebTriggerReply {
        WebTriggerReply(503, ["ok": false, "error": "The web trigger is off.", "summary": "the web trigger is off"])
    }

    /// What answers a press in the hidden copy: off → 503; one press at a time; the manifest read again (the window
    /// may have installed or removed apps); then the action, on the main actor.
    static func runner(host: LauncherActionHost, enabled: @escaping () -> Bool, reload: @escaping () -> Void) -> WebTriggerServer.Runner {
        let gate = OneAtATime()
        return { id, source in
            guard enabled() else { return offReply() }
            guard gate.tryEnter() else {
                let (status, reply) = WebTriggerContract.busy()
                return WebTriggerReply(status, reply)
            }
            defer { gate.exit() }
            return await MainActor.run {
                reload()
                let (status, reply) = LauncherActions.run(host, id: id, source: source, now: Date())
                return WebTriggerReply(status, reply)
            }
        }
    }
}

/// `JBTheatreTools --trigger-host`: ONLY the Dashboard web trigger, headless — no window, no Dock icon (the app object
/// is never made), no model. Started and kept alive by Stagehand.
enum TriggerHostMain {
    static func run() -> Never {
        let log: @Sendable (String) -> Void = { AppLog.shared.log("[host] " + $0) }
        guard let catalog = try? Catalog.load() else {
            log("couldn't read the app list — the background copy stops")
            exit(1)
        }
        Task { @MainActor in
            let host = HeadlessTriggerHost.make(catalog: catalog, log: log)
            let server = WebTriggerServer(
                run: HeadlessTriggerHost.runner(host: host, enabled: { TriggerSettings.current().enabled },
                                                reload: { InstallManager.shared.reloadFromDisk() }),
                listing: { await MainActor.run {
                    InstallManager.shared.reloadFromDisk()
                    return LauncherActions.listing(host)
                } }, log: log)
            let owner = WebTriggerOwner(server: server, retry: WebTriggerContract.retrySeconds, log: log)
            // The token is read again on every pass, straight from the Keychain and never with a dialog.
            let runner = TriggerHostRunner(owner: owner, settings: { TriggerSettings.current() },
                                           readToken: { WebTriggerStore.hiddenWord(WebTriggerStore.readForHiddenCopy()) },
                                           stateDir: TriggerRegistration.stateDir(),
                                           windowOpen: { TriggerRegistration.windowRunning(stateDir: TriggerRegistration.stateDir()) },
                                           log: log)
            let code = await runner.run(every: WebTriggerContract.retrySeconds)
            exit(code)
        }
        // The main RUN LOOP, not dispatchMain(): NSWorkspace's list of running apps (behind "already open") only updates
        // while the main run loop runs — under dispatchMain() it stays as it was at the first press.
        RunLoop.main.run()
        exit(0)
    }
}
