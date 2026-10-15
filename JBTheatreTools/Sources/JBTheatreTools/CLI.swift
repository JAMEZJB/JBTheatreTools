import Foundation

/// Headless command-line interface — used for scripting and for verifying the catalog,
/// GitHub API access, asset resolution, install/uninstall, version listing, and the launcher
/// self-update check without the GUI.
///
/// Token resolution order: `--token <pat>`, then `$GITHUB_TOKEN`, then the Keychain.
///
///   JBTheatreTools --list                 [--json] [--token X] [--catalog path]
///   JBTheatreTools --installed
///   JBTheatreTools --releases <id>        [--token X]
///   JBTheatreTools --install  <id>        [--tag vX.Y.Z] [--edition E] [--token X]
///   JBTheatreTools --download <id> --tag vX.Y.Z --asset <name> --to <folder> [--edition E] [--json]
///   JBTheatreTools --uninstall <id>       [--edition E]
///   JBTheatreTools --launch   <id>
///   JBTheatreTools --self-check           [--token X]
///   JBTheatreTools --backup-all  <file>   [--include-passwords] [--passphrase-stdin] [--apps a,b] [--no-launcher]
///   JBTheatreTools --restore-all <file>   [--no-passwords] [--passphrase-stdin] [--apps a,b] [--no-launcher]
///   JBTheatreTools --help
enum CLI {
    static let commands: Set<String> = [
        "--list", "--installed", "--releases", "--install", "--download", "--uninstall",
        "--launch", "--self-check", "--self-download", "--self-update", "--code-id", "--help", "-h",
        "--backup-all", "--restore-all",
    ]

    /// Download-server override (`--server` / `--server-pass`): when set, every client the CLI builds
    /// goes through the relay with Basic auth instead of GitHub with a Bearer token.
    private static var serverBase: String?
    private static var serverPass: String?

    /// True when server-relay auth is fully configured for this invocation.
    private static var serverConfigured: Bool { serverBase != nil && serverPass != nil }

    /// The client for this invocation: the server relay when configured, else direct GitHub.
    private static func makeClient(token: String?) -> GitHubClient {
        if let base = serverBase, let pass = serverPass {
            return GitHubClient(serverBase: base, passphrase: pass)
        }
        return GitHubClient(token: token)
    }

    /// Whether this invocation has ANY usable download auth (token or server pair).
    private static func hasAuth(token: String?) -> Bool { token != nil || serverConfigured }

    static func run(args: [String]) {
        // The command verb may appear anywhere (options like `--token X` can precede it), so find the
        // first recognised verb rather than assuming it's args[0] — otherwise `--token X --install helo`
        // would fall through and (in main.swift) launch the GUI instead of installing.
        guard let cmd = args.first(where: { commands.contains($0) }) else { printHelp(); return }
        if cmd == "--help" || cmd == "-h" { printHelp(); return }
        // Show lock (set in the app) pauses installs, updates and uninstalls — the command line honours it too, and
        // refuses before touching the Keychain.
        if UserDefaults.standard.bool(forKey: AppState.showLockKey) || ClaudeSettingsFile.suiteShowLock() == true,
           cmd == "--install" || cmd == "--uninstall" || cmd == "--restore-all" {
            fputs("error: show lock is on — installs, updates, uninstalls and restores are paused. Turn it off in JB Theatre Tools (⌘L) first.\n", stderr)
            exit(1)
        }

        var token = ProcessInfo.processInfo.environment["GITHUB_TOKEN"]
        var catalogPath: String?
        var tag: String?
        var toApplications = false
        var json = false
        var assetName: String?
        var toFolder: String?
        var edition: String?
        var positional: [String] = []
        var skippedCmd = false
        var i = 0
        while i < args.count {
            let a = args[i]
            if !skippedCmd && a == cmd { skippedCmd = true; i += 1; continue }
            switch a {
            case "--token":            i += 1; token = i < args.count ? args[i] : nil
            case "--catalog":          i += 1; catalogPath = i < args.count ? args[i] : nil
            case "--tag":              i += 1; tag = i < args.count ? args[i] : nil
            case "--to-applications":  toApplications = true
            case "--json":             json = true
            case "--asset":            i += 1; assetName = i < args.count ? args[i] : nil
            case "--to":               i += 1; toFolder = i < args.count ? args[i] : nil
            case "--edition":          i += 1; edition = i < args.count ? args[i] : nil
            case "--server":           i += 1; serverBase = i < args.count ? args[i] : nil
            case "--server-pass":      i += 1; serverPass = i < args.count ? args[i] : nil
            default:                   positional.append(a)
            }
            i += 1
        }
        // Only verbs that talk to GitHub need credentials. Local verbs (--installed / --uninstall /
        // --launch / --code-id) must never touch the Keychain: a read there can raise an OS prompt that
        // a headless run can't answer (audit F14).
        // --self-update: the launcher's repo is public — only credentials given on the command line are used.
        let needsAuth = !["--installed", "--uninstall", "--launch", "--code-id", "--self-update",
                          "--backup-all", "--restore-all"].contains(cmd)
        if needsAuth, token == nil { token = TokenStore.load() }

        let catalog: Catalog
        do {
            catalog = try Catalog.load(explicitPath: catalogPath)
        } catch {
            fputs("error: could not load catalog: \(error.localizedDescription)\n", stderr)
            exit(1)
        }
        // Normalise the shared install manifest the same way the GUI does (v1.15.0 → per-variant slots),
        // so the CLI sees the same slots as the app.
        InstallManager.shared.migrateVariantSlots(catalog.apps)
        if cmd == "--install" || cmd == "--uninstall" { InstallManager.shared.followConnectors(catalog.apps) }

        // Resolve download auth: explicit server flags win (either flag implies server mode, the URL
        // defaulting to the built-in relay); then a usable token; then the GUI-configured server mode
        // (built-in URL + saved passphrase) so a machine set up in the app works with no flags at all.
        if !needsAuth {
            // Local verb — dispatch with no credentials resolved (and no Keychain access).
        } else if serverBase != nil || serverPass != nil {
            serverBase = serverBase ?? AppState.serverOverride ?? catalog.downloadServer
            if serverPass == nil { serverPass = ServerAuthStore.load() }
        } else if token == nil, AppState.authMode == .server,
                  let base = AppState.serverOverride ?? catalog.downloadServer,
                  let pass = ServerAuthStore.load() {
            serverBase = base
            serverPass = pass
        }

        switch cmd {
        case "--list":       if json { listJSON(catalog: catalog, token: token) } else { list(catalog: catalog, token: token) }
        case "--installed":  installed(catalog: catalog)
        case "--releases":   releases(catalog: catalog, token: token, id: positional.first)
        case "--install":    install(catalog: catalog, token: token, id: positional.first, tag: tag, edition: edition, toApplications: toApplications)
        case "--download":
            exit(download(catalog: catalog, token: token, id: positional.first, tag: tag, assetName: assetName,
                          folder: toFolder, edition: edition, json: json))
        case "--uninstall":  uninstall(catalog: catalog, id: positional.first, edition: edition)
        case "--launch":     launch(catalog: catalog, id: positional.first)
        case "--self-check": selfCheck(catalog: catalog, token: token)
        case "--self-download": selfDownload(catalog: catalog, token: token, dir: positional.first)
        case "--self-update": selfUpdate(catalog: catalog, token: token)
        case "--code-id":    print(CodeIdentity.current())
        case "--backup-all", "--restore-all":
            exit(backupCommand(cmd, catalog: catalog, args: args, file: positional.first))
        default:             printHelp()
        }
    }

    // MARK: - Commands

    /// One app in `--list --json`, before the release lookup: the same shape as the Windows CLI's. `assets` is every
    /// platform's file name; `editions` (Light/Full apps only, the first is the default) gives each edition's.
    static func listEntry(_ app: CatalogApp, installed: String?) -> [String: Any] {
        var o: [String: Any] = ["id": app.id, "name": app.name, "installed": installed ?? NSNull(), "latest": NSNull(),
                                "asset": app.macAssetName ?? NSNull(), "size": NSNull(), "status": "no-auth",
                                "assets": app.assets, "editions": NSNull()]
        if app.hasVariants, let vs = app.variants {
            o["editions"] = vs.map { v -> [String: Any] in
                ["id": v.id, "label": v.label, "default": app.isDefaultVariant(v.id),
                 "slot": app.installKey(variantId: v.id), "assets": v.assets]
            }
        }
        return o
    }

    /// --list --json: the catalog with what's installed, the build this Mac would get, and every platform's file names.
    private static func listJSON(catalog: Catalog, token: String?) {
        let im = InstallManager.shared
        var apps: [[String: Any]] = []
        runBlocking {
            let client: GitHubClient? = hasAuth(token: token) ? makeClient(token: token) : nil
            for app in catalog.apps {
                var o = listEntry(app, installed: im.installedVersion(app.id))
                if let client {
                    do {
                        let all = try await client.releases(owner: app.owner, repo: app.repo)
                        if let rel = AppState.latest(from: all) {
                            o["latest"] = rel.tagName
                            let a = rel.assets.first { $0.name == app.macAssetName }
                            o["size"] = a?.size ?? NSNull()
                            o["status"] = a != nil ? "ok" : "no-build"
                        } else { o["status"] = "no-release" }
                    } catch GitHubError.notAccessible { o["status"] = "no-access" }
                    catch GitHubError.unauthorized { o["status"] = "unauthorized" }
                    catch { o["status"] = "error"; o["error"] = error.localizedDescription }
                }
                apps.append(o)
            }
        }
        let doc: [String: Any] = ["schema": 1, "platform": "macos", "apps": apps]
        guard let data = try? JSONSerialization.data(withJSONObject: doc, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes]) else {
            exit(fail(ExitCode.failed, "couldn't write the list as JSON."))
        }
        print(String(decoding: data, as: UTF8.self))
    }

    private static func list(catalog: Catalog, token: String?) {
        print("JBTheatreTools — \(catalog.apps.count) apps (platform: macOS)\n")
        let im = InstallManager.shared
        runBlocking {
            let client: GitHubClient? = hasAuth(token: token) ? makeClient(token: token) : nil
            for app in catalog.apps {
                let installed = im.installedVersion(app.id) ?? "—"
                var latest = "?", note = ""
                if let client = client {
                    do {
                        // Use the releases LIST endpoint so we can tell "no access" (404 → hidden in
                        // the GUI) apart from "accessible but no release yet" (200 []).
                        let all = try await client.releases(owner: app.owner, repo: app.repo)
                        if let rel = AppState.latest(from: all) {
                            latest = rel.tagName
                            if let a = rel.assets.first(where: { $0.name == app.macAssetName }) {
                                note = "\(a.name) (\(byteString(a.size)))"
                            } else {
                                note = "⚠︎ no macOS asset (\(app.macAssetName ?? "?"))"
                            }
                        } else {
                            latest = "none"; note = "accessible, no release yet"
                        }
                    } catch GitHubError.notAccessible {
                        latest = "—"; note = "HIDDEN — token has no access to this repo"
                    } catch GitHubError.unauthorized {
                        latest = "—"; note = "token invalid or expired"
                    } catch {
                        latest = "error"; note = error.localizedDescription
                    }
                } else {
                    note = "no auth — set $GITHUB_TOKEN, pass --token, or --server + --server-pass"
                }
                print("  \(pad(app.name, 20))  installed=\(pad(installed, 8))  latest=\(pad(latest, 10))  \(note)")
            }
        }
    }

    private static func installed(catalog: Catalog) {
        let m = InstallManager.shared.manifest()
        if m.isEmpty { print("No apps installed."); return }
        print("Installed apps (\(InstallManager.shared.manifestURL.path)):\n")
        for app in catalog.apps {
            // Every install slot: the app itself, or one per variant for variant apps (Light + Full).
            let slots: [(key: String, label: String?)] = app.hasVariants
                ? (app.variants ?? []).map { (app.installKey(variantId: $0.id), $0.label) }
                : [(app.id, nil)]
            for slot in slots {
                if let rec = m[slot.key] {
                    let name = slot.label.map { "\(app.name) (\($0))" } ?? app.name
                    print("  \(pad(name, 20))  \(pad(rec.version, 10))  \(rec.path)")
                }
            }
        }
    }

    private static func releases(catalog: Catalog, token: String?, id: String?) {
        guard let app = appFor(id, catalog) else {
            fputs("error: pass an app id. Known ids: \(catalog.apps.map { $0.id }.joined(separator: ", "))\n", stderr)
            exit(1)
        }
        guard hasAuth(token: token) else { fputs("error: no auth (token or --server + --server-pass).\n", stderr); exit(1) }
        let client = makeClient(token: token)
        runBlocking {
            do {
                let all = try await client.releases(owner: app.owner, repo: app.repo)
                print("\(app.name) — \(all.count) release(s):\n")
                for rel in all {
                    let hasMac = rel.assets.contains { $0.name == app.macAssetName }
                    let flags = (rel.prerelease ? " [pre-release]" : "") + (hasMac ? "" : " [no macOS asset]")
                    print("  \(pad(rel.tagName, 12))\(flags)")
                }
            } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    private static func install(catalog: Catalog, token: String?, id: String?, tag: String?, edition: String?, toApplications: Bool) {
        guard let app = appFor(id, catalog) else {
            fputs("error: pass an app id. Known ids: \(catalog.apps.map { $0.id }.joined(separator: ", "))\n", stderr)
            exit(1)
        }
        guard let variant = app.resolveEdition(edition) else {
            exit(fail(ExitCode.usage, "\(app.name) has no edition \"\(edition ?? "")\". Editions: \(app.editionChoices)."))
        }
        let slot = app.installKey(variantId: variant)
        let appName = app.name + app.variantSuffix(variant)
        refuseIfOpen(slot, appName, "install")
        guard hasAuth(token: token) else {
            fputs("error: no auth (set $GITHUB_TOKEN, pass --token, save one in the app, or use --server + --server-pass).\n", stderr)
            exit(1)
        }
        let client = makeClient(token: token)
        let im = InstallManager.shared
        runBlocking {
            do {
                let all = try await client.releases(owner: app.owner, repo: app.repo)
                let release = tag != nil
                    ? all.first { VersionDisplay.equal($0.tagName, tag!) }
                    : AppState.latest(from: all)
                guard let rel = release else { fputs("error: version \(tag ?? "latest") not found.\n", stderr); exit(1) }
                guard let asset = rel.assets.first(where: { $0.name == app.macAssetName(variantId: variant) }) else {
                    fputs("error: release \(rel.tagName) has no macOS asset.\n", stderr); exit(1)
                }
                print("Downloading \(asset.name) (\(byteString(asset.size))) @ \(rel.tagName)…")
                let zip = im.cacheDir.appendingPathComponent("\(PathSafe.component(slot))-\(PathSafe.component(rel.tagName)).zip")
                final class PctBox: @unchecked Sendable { var last = -1 }
                let pctBox = PctBox()
                try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: asset.id, to: zip) { p in
                    let pct = Int(p * 100)
                    if pct != pctBox.last, pct % 10 == 0 { pctBox.last = pct; print("  \(pct)%") }
                }
                let verification = try await AppState.verifyDownload(zip, asset: asset, release: rel, app: app, client: client)
                // Strict for current releases (no --tag): must checksum-verify. An explicit --tag install
                // (older build) stays verify-if-present. A hash mismatch always aborts (throws above).
                if tag == nil, verification != .verified {
                    try? FileManager.default.removeItem(at: zip)
                    let reason = AppState.strictFailureReason(verification, assetName: asset.name)
                    AppLog.shared.log("cli: install \(slot) \(rel.tagName) BLOCKED (strict) — \(reason)")
                    fputs("error: refusing to install \(appName) \(rel.tagName) — \(reason). (Re-run with --tag \(rel.tagName) to install it anyway, unverified.)\n", stderr)
                    exit(1)
                }
                switch verification {
                case .verified:       print("Verified \(asset.name) (signed sha256).")
                case .unsigned:       print("⚠︎ \(asset.name) installed: sha256 ok but the manifest is UNSIGNED (older tag).")
                case .noManifest:     print("⚠︎ \(asset.name) installed unverified (older tag; no SHA256SUMS).")
                case .assetNotListed: print("⚠︎ \(asset.name) installed unverified (older tag; not in SHA256SUMS).")
                }
                let before = im.installedVersion(slot)
                let dest = try im.install(app: app, version: rel.tagName, downloadedZip: zip, toApplications: toApplications,
                                          variant: variant)
                try? FileManager.default.removeItem(at: zip)
                HistoryStore.add(app: app.id, name: appName, action: ActivityHistory.action(from: before, to: rel.tagName),
                                 from: before, to: rel.tagName)
                HistoryStore.flush()   // written before the process exits
                AppLog.shared.log("cli: installed \(slot) \(rel.tagName)\(toApplications ? " (Applications)" : "")\(verification == .verified ? " (sha256 ok)" : " (unverified)")")
                print("Installed \(appName) \(rel.tagName) → \(dest.path)")
            } catch InstallError.appRunning {   // opened while it downloaded: install() never replaces a running app
                exit(fail(ExitCode.appOpen, "\(appName) is open. Close it, then run this again (the command line never closes an open app)."))
            } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    /// Exit codes for the verbs another program drives (the same numbers as the Windows CLI's table).
    enum ExitCode {
        static let ok: Int32 = 0, failed: Int32 = 1, usage: Int32 = 2, noAccess: Int32 = 3, notFound: Int32 = 4
        static let download: Int32 = 5, rejected: Int32 = 6
        /// The app is open: the command line never installs over or removes it (the window asks first; nobody can here).
        static let appOpen: Int32 = 9
    }

    /// The command line never installs over or removes an app that's open (the window asks first; nobody can be asked
    /// here): exits 9 with a one-line error.
    private static func refuseIfOpen(_ slot: String, _ appName: String, _ doing: String) {
        guard InstallManager.shared.runningInstance(slot) != nil else { return }
        AppLog.shared.log("cli: \(doing) \(slot) refused — it's open")
        exit(fail(ExitCode.appOpen, "\(appName) is open. Close it, then run this again (the command line never closes an open app)."))
    }

    /// One "error: …" line on stderr; returns the exit code.
    private static func fail(_ code: Int32, _ message: String) -> Int32 {
        let line = message.split(whereSeparator: \.isNewline).map { $0.trimmingCharacters(in: .whitespaces) }.joined(separator: " ")
        fputs("error: \(line)\n", stderr)
        return code
    }

    private static func exitCode(for error: Error) -> Int32 {
        switch error {
        case ReleaseDownload.Failure.missingFile: return ExitCode.download
        case ReleaseDownload.Failure.badAssetName: return ExitCode.usage
        case is ReleaseDownload.Failure, is Minisign.VerifyError:
            return ExitCode.rejected
        case GitHubError.unauthorized, GitHubError.notAccessible: return ExitCode.noAccess
        case GitHubError.noRelease, GitHubError.assetNotFound: return ExitCode.notFound
        case is GitHubError, is URLError: return ExitCode.download
        default: return ExitCode.failed
        }
    }

    /// --download <id> --tag vX.Y.Z --asset <name> --to <folder> [--json]: fetches one build of a release (any
    /// platform's, e.g. a Windows exe from this Mac) with SHA256SUMS + SHA256SUMS.minisig into <folder>, verified as
    /// --install-file will check them, for a PC without internet. Uses this launcher's saved credentials (never printed).
    private static func download(catalog: Catalog, token: String?, id: String?, tag: String?, assetName: String?,
                                 folder: String?, edition: String?, json: Bool) -> Int32 {
        let usage = "usage: --download <id> --tag vX.Y.Z --asset <file name> --to <folder> [--edition <edition>] [--json]"
        guard let id else { return fail(ExitCode.usage, usage) }
        guard let app = appFor(id, catalog) else {
            return fail(ExitCode.usage, "unknown app id \"\(id)\". Known ids: \(catalog.apps.map { $0.id }.joined(separator: ", "))")
        }
        guard let tag, !tag.isEmpty else { return fail(ExitCode.usage, "--download needs --tag (the release, e.g. v1.16.2).") }
        guard let assetName, !assetName.isEmpty else { return fail(ExitCode.usage, "--download needs --asset (the build's file name, e.g. ShowDashboard-Windows-x64.exe).") }
        guard ReleaseDownload.isPlainAssetName(assetName) else { return fail(ExitCode.usage, "“\(assetName)” isn't a release file name.") }
        guard let folder, !folder.isEmpty else { return fail(ExitCode.usage, "--download needs --to <folder>.") }
        // --edition: the file must be one of that edition's builds (any platform's), so a Light name can't stand in for Full.
        if edition != nil {
            guard let variant = app.resolveEdition(edition) else {
                return fail(ExitCode.usage, "\(app.name) has no edition \"\(edition!)\". Editions: \(app.editionChoices).")
            }
            let names = Set(app.assets(variantId: variant).values)
            guard names.contains(assetName) else {
                return fail(ExitCode.usage, "\(assetName) isn't a build of \(app.name)\(app.variantSuffix(variant)). Its builds: \(names.sorted().joined(separator: ", ")).")
            }
        }
        guard hasAuth(token: token) else {
            return fail(ExitCode.noAccess, "no auth (save a token or the suite passphrase in the app, set $GITHUB_TOKEN, or pass --server + --server-pass).")
        }
        let dir = URL(fileURLWithPath: (folder as NSString).expandingTildeInPath).standardizedFileURL
        let client = makeClient(token: token)
        var code = ExitCode.failed
        runBlocking {
            do {
                let all = try await client.releases(owner: app.owner, repo: app.repo)
                guard let rel = all.first(where: { VersionDisplay.equal($0.tagName, tag) }) else {
                    code = fail(ExitCode.notFound, "\(app.name) has no release \(tag).")
                    return
                }
                guard let asset = rel.assets.first(where: { $0.name == assetName }) else {
                    let builds = rel.assets.map(\.name).filter(ReleaseDownload.isPlainAssetName)
                    code = fail(ExitCode.notFound, "\(app.name) \(rel.tagName) has no file named \(assetName). It has: \(builds.joined(separator: ", ")).")
                    return
                }
                let files = [asset] + [ReleaseDownload.sumsName, ReleaseDownload.sigName].compactMap { n in rel.assets.first { $0.name == n } }
                guard files.count == 3 else {
                    code = fail(ExitCode.rejected, "\(app.name) \(rel.tagName) isn't signed (no SHA256SUMS + SHA256SUMS.minisig), so a PC can't check it.")
                    return
                }
                if !json { print("Downloading \(asset.name) (\(byteString(asset.size))) @ \(rel.tagName)…") }
                let result = try await ReleaseDownload.run(folder: dir, assetName: asset.name, expectedSize: asset.size,
                                                           repo: app.repo, tag: rel.tagName) { name, dest in
                    guard let a = files.first(where: { $0.name == name }) else { throw GitHubError.assetNotFound(name) }
                    try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: a.id, to: dest)
                }
                AppLog.shared.log("cli: downloaded \(app.id) \(rel.tagName) \(asset.name) (sha256 ok)")
                if json {
                    let out: [String: Any] = ["path": result.path.path, "sums": result.sums.path, "sig": result.sig.path,
                                              "size": result.size, "sha256": result.sha256]
                    let data = try JSONSerialization.data(withJSONObject: out, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
                    print(String(decoding: data, as: UTF8.self))
                } else {
                    print("Verified \(asset.name) (signed sha256, \(app.repo) \(rel.tagName)).")
                    print("Saved → \(result.path.path)")
                    print("      + \(result.sums.lastPathComponent), \(result.sig.lastPathComponent)")
                }
                code = ExitCode.ok
            } catch {
                AppLog.shared.log("cli: download \(app.id) \(tag) \(assetName) failed: \(error.localizedDescription)")
                code = fail(exitCode(for: error), error.localizedDescription)
            }
        }
        return code
    }

    private static func uninstall(catalog: Catalog, id: String?, edition: String?) {
        guard let app = appFor(id, catalog) else { fputs("error: pass an app id.\n", stderr); exit(1) }
        guard let variant = app.resolveEdition(edition) else {
            exit(fail(ExitCode.usage, "\(app.name) has no edition \"\(edition ?? "")\". Editions: \(app.editionChoices)."))
        }
        let slot = app.installKey(variantId: variant)
        let appName = app.name + app.variantSuffix(variant)
        refuseIfOpen(slot, appName, "uninstall")
        do {
            let before = InstallManager.shared.installedVersion(slot)
            try InstallManager.shared.uninstall(slot)
            if let before {
                HistoryStore.add(app: app.id, name: appName, action: "uninstall", from: before)
                HistoryStore.flush()   // written before the process exits
            }
            AppLog.shared.log("cli: uninstalled \(slot)")
            print("Uninstalled \(appName).")
        } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
    }

    private static func launch(catalog: Catalog, id: String?) {
        guard let app = appFor(id, catalog) else { fputs("error: pass an app id.\n", stderr); exit(1) }
        do {
            try InstallManager.shared.launch(app: app)
            AppLog.shared.log("cli: launched \(app.id)")
            print("Launched \(app.name).")
        } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
    }

    private static func selfCheck(catalog: Catalog, token: String?) {
        guard let s = catalog.selfInfo else { fputs("error: no `self` entry in catalog.\n", stderr); exit(1) }
        let current = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0.0"
        let client = makeClient(token: token)
        runBlocking {
            do {
                let info = try await client.latestRelease(owner: s.owner, repo: s.repo)
                let newer = AppState.versionIsNewer(info.tagName, than: current)
                print("JB Theatre Tools: current v\(current), latest \(info.tagName) → \(newer ? "UPDATE AVAILABLE" : "up to date")")
            } catch GitHubError.noRelease {
                print("JB Theatre Tools: current v\(current), no launcher release published yet")
            } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    private static func selfDownload(catalog: Catalog, token: String?, dir: String?) {
        guard let s = catalog.selfInfo else { fputs("error: no `self` entry in catalog.\n", stderr); exit(1) }
        let destDir = dir ?? NSTemporaryDirectory()
        let client = makeClient(token: token)
        runBlocking {
            do {
                let info = try await client.latestRelease(owner: s.owner, repo: s.repo)
                guard let asset = info.assets.first(where: { $0.name == s.macAssetName }) else {
                    fputs("error: release \(info.tagName) has no macOS asset.\n", stderr); exit(1)
                }
                let dest = URL(fileURLWithPath: destDir).appendingPathComponent(PathSafe.component(asset.name))
                print("Downloading \(asset.name) (\(byteString(asset.size))) @ \(info.tagName)…")
                try await client.downloadAsset(owner: s.owner, repo: s.repo, assetId: asset.id, to: dest)
                // Strict verify, exactly like the GUI self-update (audit F5): size + suite-signed
                // SHA256SUMS + hash. A failure deletes the download and exits non-zero.
                let verification = try await AppState.verifyDownload(dest, asset: asset, release: info,
                                                                     app: CatalogApp.forSelf(s), client: client)
                guard verification == .verified else {
                    try? FileManager.default.removeItem(at: dest)
                    fputs("error: refusing to keep \(asset.name) — \(AppState.strictFailureReason(verification, assetName: asset.name)).\n", stderr)
                    exit(1)
                }
                print("Verified \(asset.name) (signed sha256).")
                print("Saved → \(dest.path)")
            } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    /// --self-update: the launcher's in-place update, headless — this app bundle is replaced where it is (same name),
    /// verified like every download; no restart (the next start runs the new version and bins the old copy).
    private static func selfUpdate(catalog: Catalog, token: String?) {
        guard let s = catalog.selfInfo else { fputs("error: no `self` entry in catalog.\n", stderr); exit(1) }
        // The launcher's own show lock, or the suite-wide one set from Stagehand or an app (claude.json).
        if UserDefaults.standard.bool(forKey: AppState.showLockKey) || ClaudeSettingsFile.suiteShowLock() == true {
            fputs("error: show lock is on — turn it off in JB Theatre Tools (⌘L) first.\n", stderr); exit(1)
        }
        let current = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "1.0.0"
        let bundle = Bundle.main.bundleURL.resolvingSymlinksInPath()   // a symlinked install: replace the real bundle
        guard bundle.pathExtension == "app" else { fputs("error: run this from inside the JB Theatre Tools app.\n", stderr); exit(1) }
        if SelfUpdate.isTranslocated(bundle) { fputs("error: \(SelfUpdate.Failure.translocated.localizedDescription)\n", stderr); exit(1) }
        let client = hasAuth(token: token) ? makeClient(token: token) : GitHubClient(token: nil)
        runBlocking {
            do {
                // The same pick the window makes on this Mac (Development builds on or off).
                let releases = try await client.releases(owner: s.owner, repo: s.repo)
                let dev = AppState.devChannel
                let pick = AppState.latest(from: releases, devChannel: dev)
                guard let target = pick, AppState.versionIsNewer(target.tagName, than: current)
                        || (!dev && AppState.isDevTag(current) && !VersionDisplay.equal(target.tagName, current)) else {
                    print("JB Theatre Tools v\(current) is up to date."); return
                }
                let staged = try await SelfUpdate.download(s, target: target, client: client)
                defer { SelfUpdate.discard(staged) }
                let old = try SelfUpdate.swap(newBundle: staged.bundle, into: bundle)
                AppLog.shared.log("cli: self-update \(current) -> \(target.tagName) at \(bundle.path)")
                print("Updated JB Theatre Tools v\(current) -> \(target.tagName) in place: \(bundle.path)")
                print("The next start runs it. The previous version is kept beside it as \(old.lastPathComponent) (delete it any time).")
            } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    // MARK: - Helpers

    private static func appFor(_ id: String?, _ catalog: Catalog) -> CatalogApp? {
        guard let id = id else { return nil }
        return catalog.apps.first { $0.id == id }
    }

    private static func printHelp() {
        print("""
        JBTheatreTools — theatre/AV app launcher (headless CLI)

          --list                 List the catalog with installed & latest versions (--json: by catalog id,
                                 with every platform's file name per edition)
          --installed            Show locally installed apps
          --releases  <id>       List all available versions of an app
          --install   <id>       Download & install (latest, or --tag vX.Y.Z for an older one)
          --download  <id> --tag vX.Y.Z --asset <file name> --to <folder>
                                 Fetch one build of a release (any platform's) with its SHA256SUMS and
                                 SHA256SUMS.minisig into <folder>, verified, for a PC without internet
                                 (it installs them with --install-file). --json: path, sums, sig, size, sha256
          --uninstall <id>       Remove an installed app
          --launch    <id>       Launch an installed app
          --self-check           Check whether a newer launcher release exists
          --self-update          Update this launcher in place (verified; the next start runs it)
          --backup-all  <file>   Back up every installed app's settings (+ this launcher's) into one file
          --restore-all <file>   Restore them from that file (installed apps only; quit each app first)
          --help                 This help

        Options: --token <pat>       GitHub PAT (else $GITHUB_TOKEN, else Keychain)
                 --server <url>      Download-server (relay) base URL — use instead of a token
                 --server-pass <p>   Suite passphrase for --server (else the saved Keychain one)
                 --tag <vX.Y.Z>      Install a specific release (with --install)
                 --to-applications   Install into the Applications folder (with --install)
                 --edition <e>       An app's Light or Full edition (light / full; default: its first) — with
                                     --install, --uninstall and --download
                 --catalog <path>    Use a specific catalog.json
                 --json              Machine-readable answer (with --list, --download)
                 --include-passwords Also back up saved passwords (with --backup-all)
                 --no-passwords      Leave this computer's passwords as they are (with --restore-all)
                 --passphrase-stdin  Read the backup passphrase from one line of stdin
                                     (else $JBTT_SETTINGS_PASSPHRASE; never on the command line)
                 --apps <a,b>        Only these app ids;  --no-launcher: leave the launcher's own settings out

        Note: a secret passed via --token / --server-pass is visible to other local users (process
              list / shell history). Prefer $GITHUB_TOKEN or the saved Keychain values where possible.

        Exit codes (--download, --list --json): 0 ok · 1 failed · 2 usage · 3 no access · 4 not found
                    5 download failed (retry) · 6 verification failed · 9 the app is open (--install and
                    --uninstall never close it: close it, then retry). Errors are one line on stderr, "error: ".
        """)
    }

    private static func runBlocking(_ body: @escaping () async -> Void) {
        let sem = DispatchSemaphore(value: 0)
        Task { await body(); sem.signal() }
        sem.wait()
    }

    private static func pad(_ s: String, _ width: Int) -> String {
        s.count >= width ? s : s + String(repeating: " ", count: width - s.count)
    }

    private static func byteString(_ bytes: Int) -> String {
        let f = ByteCountFormatter()
        f.countStyle = .file
        return f.string(fromByteCount: Int64(bytes))
    }
}
