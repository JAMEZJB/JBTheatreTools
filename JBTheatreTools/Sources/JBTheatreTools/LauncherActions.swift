import Foundation

/// What the launcher's actions need from the window (AppState, or a test stand-in) — closures, all called on the main
/// actor (see `AppState.runTriggerAction`), as if the person had pressed the button.
struct LauncherActionHost {
    var showLock: () -> Bool
    /// Turns show lock on exactly as ⌘L does; returns how many downloads it stopped.
    var turnShowLockOn: (_ source: String) -> Int
    /// The catalog's apps, in catalog order, each with its install slots (the Full edition first).
    var apps: () -> [LauncherActions.App]
    /// The app ids ticked in Settings → Dashboard web trigger (only these can be opened).
    var tickedApps: () -> Set<String>
    var installedVersion: (_ slotKey: String) -> String?
    /// The slot is being downloaded, verified, installed or removed right now.
    var slotBusy: (_ slotKey: String) -> Bool
    /// A person has the slot's app open (a headless helper — a Claude connector — doesn't count).
    var isOpen: (_ slotKey: String) -> Bool
    var launch: (_ slotKey: String) throws -> Void
    var facts: () -> LauncherActions.Facts
    var log: (_ line: String) -> Void
}

/// The launcher's three web-trigger actions — the same as Windows (Core/LauncherActions.cs); none takes a value from the
/// request:
/// - `open-<app id>` (saved: only apps ticked in Settings) — opens an installed app like its Open button; not installed →
///   422, being installed or updated → 409, already open → 200 and nothing starts.
/// - `show-lock-on` (fixed) — show lock ON, as ⌘L. There is deliberately no "off": a person at the computer releases it.
/// - `status` (fixed, read-only, never goes online) — show lock, apps installed, updates waiting from the last check,
///   the launcher's version.
enum LauncherActions {
    static let statusId = "status"
    static let showLockOnId = "show-lock-on"
    static let openPrefix = "open-"

    /// One install slot: its manifest key and the edition suffix it is shown with (" (Full)", or "").
    struct Slot: Equatable {
        let key: String
        let suffix: String
    }

    struct App: Equatable {
        let id: String
        let name: String
        let slots: [Slot]
    }

    struct Facts {
        let showLock: Bool
        let installed: Int
        let updates: Int
        let lastCheck: Date?
        let version: String
    }

    /// GET /actions: the two fixed actions, then one "open-…" per ticked app (catalog order).
    static func listing(_ host: LauncherActionHost) -> [WebTriggerAction] {
        var list = [WebTriggerAction(id: statusId, name: "Launcher status"), WebTriggerAction(id: showLockOnId, name: "Show lock on")]
        let ticked = host.tickedApps()
        for app in host.apps() where ticked.contains(app.id) && WebTriggerContract.validActionId(openPrefix + app.id) {
            list.append(WebTriggerAction(id: openPrefix + app.id, name: "Open " + app.name))
        }
        return list
    }

    static func norm(_ v: String) -> String {
        let t = v.trimmingCharacters(in: .whitespaces)
        return t.hasPrefix("v") || t.hasPrefix("V") ? String(t.dropFirst()) : t
    }

    static func run(_ host: LauncherActionHost, id: String, source: String, now: Date) -> (Int, [String: Any]) {
        let t0 = Date()
        func secs() -> Double { Date().timeIntervalSince(t0) }
        if id == statusId {
            let f = host.facts()
            var details: [String: Any] = [
                "show_lock": f.showLock, "apps_installed": f.installed, "launcher_version": norm(f.version),
                "updates_waiting": f.lastCheck == nil ? NSNull() as Any : f.updates as Any,
                "last_check": NSNull(),
            ]
            if let last = f.lastCheck {
                let iso = ISO8601DateFormatter()
                iso.formatOptions = [.withInternetDateTime]
                details["last_check"] = iso.string(from: last)
            }
            return (200, WebTriggerContract.reply(ok: true, action: id, name: "Launcher status", summary: statusLine(f, now: now),
                                                  seconds: secs(), details))
        }
        if id == showLockOnId {
            if host.showLock() {
                return (200, WebTriggerContract.reply(ok: true, action: id, name: "Show lock on", summary: "Show lock is already on",
                                                      seconds: secs(), ["show_lock": true, "changed": false]))
            }
            let stopped = host.turnShowLockOn(source)
            let summary = "Show lock on" + (stopped > 0 ? " (\(stopped) download\(stopped == 1 ? "" : "s") stopped)" : "")
            return (200, WebTriggerContract.reply(ok: true, action: id, name: "Show lock on", summary: summary, seconds: secs(),
                                                  ["show_lock": true, "changed": true, "downloads_stopped": stopped]))
        }
        if id.hasPrefix(openPrefix) {
            let appId = String(id.dropFirst(openPrefix.count))
            guard let app = host.apps().first(where: { $0.id == appId }), host.tickedApps().contains(appId) else {
                return WebTriggerContract.notFound(id)
            }
            let name = "Open " + app.name
            let installed = app.slots.compactMap { slot in host.installedVersion(slot.key).map { (slot: slot, version: $0) } }
            guard let pick = installed.first else {
                return fail(422, id, name, "\(app.name) isn't installed on this computer", secs())
            }
            if app.slots.contains(where: { host.slotBusy($0.key) }) {
                return fail(409, id, name, "\(app.name) is being installed or updated — try again when it has finished", secs())
            }
            if installed.contains(where: { host.isOpen($0.slot.key) }) {
                return (200, WebTriggerContract.reply(ok: true, action: id, name: name, summary: "\(app.name) is already open",
                                                      seconds: secs(), ["started": false]))
            }
            do {
                try host.launch(pick.slot.key)
            } catch {
                host.log("launch \(pick.slot.key) (\(source)) failed: \(error.localizedDescription)")
                return fail(502, id, name, "Couldn't open \(app.name): \(error.localizedDescription)", secs())
            }
            host.log("launched \(pick.slot.key) (\(source))")
            let v = norm(pick.version)
            return (200, WebTriggerContract.reply(ok: true, action: id, name: name, summary: "Opened \(app.name)\(pick.slot.suffix) \(v)",
                                                  seconds: secs(), ["started": true, "version": v]))
        }
        return WebTriggerContract.notFound(id)
    }

    private static func fail(_ status: Int, _ id: String, _ name: String, _ summary: String, _ seconds: Double) -> (Int, [String: Any]) {
        var r = WebTriggerContract.reply(ok: false, action: id, name: name, summary: summary, seconds: seconds)
        r["error"] = summary + "."
        return (status, r)
    }

    /// "Show lock off · 11 apps installed · 2 updates waiting (last check 14 min ago) · launcher 1.36.0".
    static func statusLine(_ f: Facts, now: Date) -> String {
        var parts = [f.showLock ? "Show lock on" : "Show lock off", "\(f.installed) app\(f.installed == 1 ? "" : "s") installed"]
        if let last = f.lastCheck {
            let n = f.updates == 0 ? "no updates" : f.updates == 1 ? "1 update" : "\(f.updates) updates"
            parts.append("\(n) waiting (last check \(checkAge(last, now: now)))")
        } else {
            parts.append("no update check yet")
        }
        parts.append("launcher " + norm(f.version))
        return parts.joined(separator: " · ")
    }

    /// "just now", "14 min ago", "3 hours ago", "2 days ago".
    static func checkAge(_ last: Date, now: Date) -> String {
        let s = now.timeIntervalSince(last)
        if s < 60 { return "just now" }
        if s < 3600 { return "\(Int(s / 60)) min ago" }
        if s < 48 * 3600 { let h = Int(s / 3600); return h == 1 ? "1 hour ago" : "\(h) hours ago" }
        return "\(Int(s / 86400)) days ago"
    }

    /// The Settings status line: "Off", where it listens and an example address, waiting for a taken port, or why it isn't
    /// listening.
    static func describe(enabled: Bool, bind: String, running: Bool, address: String, port: Int, error: String,
                         waiting: Bool = false, background: Bool = false) -> (text: String, problem: Bool) {
        if !enabled { return ("Off. Nothing listens until you switch it on.", false) }
        if !running && background {
            return ("The background copy is listening on port \(port). This window takes over if it stops.", false)
        }
        if !running && waiting { return (WebTriggerContract.waitingText(port: port), true) }
        if !running { return (error.isEmpty ? "Not listening." : error, true) }
        let ip = WebTriggerContract.parseIP(bind)
        let host = ip?.isUnspecified == true ? "<this computer's address>" : ip?.isV6 == true ? "[\(bind)]" : bind
        return ("Listening on \(address). Try: POST http://\(host):\(port)/actions/status?reply=text", false)
    }

    /// Settings: who answers a press right now. "" while the trigger is off.
    static func answering(enabled: Bool, windowOwns: Bool, background: Bool, stagehandInstalled: Bool) -> String {
        guard enabled else { return "" }
        if windowOwns { return "Answering presses: this window." }
        if background { return "Answering presses: the background copy." }
        return stagehandInstalled ? "Answering presses: not running — open Stagehand."
                                  : "Answering presses: not running — " + stagehandMissing
    }

    /// Settings, when Stagehand (which keeps the background copy running) isn't installed.
    static let stagehandMissing = "Install Stagehand so dashboard buttons keep working when JB Theatre Tools is closed."
    /// Settings: what keeps the buttons working with the window closed.
    static let backgroundNote = "Dashboard buttons keep working when JB Theatre Tools is closed: Stagehand keeps a hidden copy running for them."
}

/// The short guide behind "How it works…" in Settings — the same words as Windows (WebTriggerGuide in
/// Core/LauncherActions.cs), with the Mac's names for the keys and the Keychain.
enum WebTriggerGuide {
    struct Section: Hashable {
        let heading: String
        let body: String
    }

    static func sections(port: Int) -> [Section] {
        let can: [String] = [
            "status — one line: show lock, apps installed, updates waiting from the last check (it never goes online) and this launcher's version.",
            "show-lock-on — turns show lock on for this computer, the same as ⌘L: installs, updates and downloads stop, and Stagehand and every app see the lock. There is no \"off\" button on purpose: a person at the computer turns it off.",
            "open-<app> — opens an app you ticked under \"Apps a button may open\", e.g. open-dmxtools. If it's already open, nothing new starts.",
        ]
        let on: [String] = [
            "Tick \"Switch on the web trigger\". Listen on: 127.0.0.1 when Node-RED runs on this computer (the safest); one of this computer's own addresses when the dashboard runs on another one; 0.0.0.0 (every network) needs a token. "
                + "Port: \(port) unless something else uses it. Token (optional): a shared word the button must send; it's kept in your Keychain and never shown again.",
            "Dashboard buttons keep working when JB Theatre Tools is closed, and after a restart: Stagehand keeps a hidden copy of the launcher running for them (install Stagehand for this). While JB Theatre Tools is running it answers presses itself; when it quits, the hidden copy takes over within a few seconds. Switching the trigger off stops the hidden copy too.",
            "If another program — or a copy of JB Theatre Tools that is still closing after an update — has the port, it waits and tries again every \(Int(WebTriggerContract.retrySeconds)) seconds; Settings shows \"Waiting for port \(port)\" until it gets it.",
        ]
        let button: [String] = [
            "In Show Dashboard add a Custom control of the kind \"Saved action\": this computer's address (127.0.0.1 when it's the same computer), port \(port), and the action id, e.g. open-dmxtools.",
            "It sends POST http://127.0.0.1:\(port)/actions/open-dmxtools?reply=text with the body {} — or {\"token\": \"your token\"} when one is set — and shows the one-line reply on Last command.",
            "GET http://127.0.0.1:\(port)/actions lists the ids you can use. Give show-lock-on a confirm text, e.g. \"Turn show lock on for this computer?\".",
        ]
        let replies: [String] = [
            "200 — done (or already so: the app was open, the lock was on).",
            "502 — it tried, but something failed (the app wouldn't open). The launcher's log says why.",
            "400 — not a JSON request. Send a JSON body: {} or {\"token\": \"…\"}.",
            "403 — wrong or missing token, or the request came from a web page (refused on purpose).",
            "404 — no action with that id, or that app isn't ticked here.",
            "409 — busy: another press is running, or that app is being installed or updated. Try again in a moment.",
            "422 — it can't be done as things are: that app isn't installed on this computer.",
            "503 — the web trigger has just been switched off.",
            "No answer at all — the trigger is off, JB Theatre Tools isn't running, or the address or port is wrong.",
        ]
        return [
            Section(heading: "What a button can do", body: can.joined(separator: "\n")),
            Section(heading: "Switch it on", body: on.joined(separator: "\n")),
            Section(heading: "The Show Dashboard button", body: button.joined(separator: " ")),
            Section(heading: "What the replies mean", body: replies.joined(separator: "\n")),
        ]
    }
}
