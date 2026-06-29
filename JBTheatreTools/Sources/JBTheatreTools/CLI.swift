import Foundation

/// Headless command-line interface — used for scripting and for verifying the catalog,
/// GitHub API access, asset resolution, install/uninstall, version listing, and the launcher
/// self-update check without the GUI.
///
/// Token resolution order: `--token <pat>`, then `$GITHUB_TOKEN`, then the Keychain.
///
///   JBTheatreTools --list                 [--token X] [--catalog path]
///   JBTheatreTools --installed
///   JBTheatreTools --releases <id>        [--token X]
///   JBTheatreTools --install  <id>        [--tag vX.Y.Z] [--token X]
///   JBTheatreTools --uninstall <id>
///   JBTheatreTools --launch   <id>
///   JBTheatreTools --self-check           [--token X]
///   JBTheatreTools --help
enum CLI {
    static let commands: Set<String> = [
        "--list", "--installed", "--releases", "--install", "--uninstall",
        "--launch", "--self-check", "--self-download", "--code-id", "--help", "-h",
    ]

    static func run(args: [String]) {
        // The command verb may appear anywhere (options like `--token X` can precede it), so find the
        // first recognised verb rather than assuming it's args[0] — otherwise `--token X --install helo`
        // would fall through and (in main.swift) launch the GUI instead of installing.
        guard let cmd = args.first(where: { commands.contains($0) }) else { printHelp(); return }
        if cmd == "--help" || cmd == "-h" { printHelp(); return }

        var token = ProcessInfo.processInfo.environment["GITHUB_TOKEN"]
        var catalogPath: String?
        var tag: String?
        var toApplications = false
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
            default:                   positional.append(a)
            }
            i += 1
        }
        if token == nil { token = TokenStore.load() }

        let catalog: Catalog
        do {
            catalog = try Catalog.load(explicitPath: catalogPath)
        } catch {
            fputs("error: could not load catalog: \(error.localizedDescription)\n", stderr)
            exit(1)
        }

        switch cmd {
        case "--list":       list(catalog: catalog, token: token)
        case "--installed":  installed(catalog: catalog)
        case "--releases":   releases(catalog: catalog, token: token, id: positional.first)
        case "--install":    install(catalog: catalog, token: token, id: positional.first, tag: tag, toApplications: toApplications)
        case "--uninstall":  uninstall(catalog: catalog, id: positional.first)
        case "--launch":     launch(catalog: catalog, id: positional.first)
        case "--self-check": selfCheck(catalog: catalog, token: token)
        case "--self-download": selfDownload(catalog: catalog, token: token, dir: positional.first)
        case "--code-id":    print(CodeIdentity.current())
        default:             printHelp()
        }
    }

    // MARK: - Commands

    private static func list(catalog: Catalog, token: String?) {
        print("JBTheatreTools — \(catalog.apps.count) apps (platform: macOS)\n")
        let im = InstallManager.shared
        runBlocking {
            let client = token.map { GitHubClient(token: $0) }
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
                    note = "no token — set $GITHUB_TOKEN or pass --token"
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
            if let rec = m[app.id] {
                print("  \(pad(app.name, 20))  \(pad(rec.version, 10))  \(rec.path)")
            }
        }
    }

    private static func releases(catalog: Catalog, token: String?, id: String?) {
        guard let app = appFor(id, catalog) else {
            fputs("error: pass an app id. Known ids: \(catalog.apps.map { $0.id }.joined(separator: ", "))\n", stderr)
            exit(1)
        }
        guard let token = token else { fputs("error: no token.\n", stderr); exit(1) }
        let client = GitHubClient(token: token)
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

    private static func install(catalog: Catalog, token: String?, id: String?, tag: String?, toApplications: Bool) {
        guard let app = appFor(id, catalog) else {
            fputs("error: pass an app id. Known ids: \(catalog.apps.map { $0.id }.joined(separator: ", "))\n", stderr)
            exit(1)
        }
        guard let token = token else {
            fputs("error: no token (set $GITHUB_TOKEN, pass --token, or save one in the app).\n", stderr)
            exit(1)
        }
        let client = GitHubClient(token: token)
        let im = InstallManager.shared
        runBlocking {
            do {
                let all = try await client.releases(owner: app.owner, repo: app.repo)
                let release = tag != nil
                    ? all.first { $0.tagName == tag }
                    : AppState.latest(from: all)
                guard let rel = release else { fputs("error: version \(tag ?? "latest") not found.\n", stderr); exit(1) }
                guard let asset = rel.assets.first(where: { $0.name == app.macAssetName }) else {
                    fputs("error: release \(rel.tagName) has no macOS asset.\n", stderr); exit(1)
                }
                print("Downloading \(asset.name) (\(byteString(asset.size))) @ \(rel.tagName)…")
                let zip = im.cacheDir.appendingPathComponent("\(app.id)-\(rel.tagName).zip")
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
                    let reason = verification == .noManifest
                        ? "the release publishes no SHA256SUMS"
                        : "\(asset.name) isn't listed in the release's SHA256SUMS"
                    AppLog.shared.log("cli: install \(app.id) \(rel.tagName) BLOCKED (strict) — \(reason)")
                    fputs("error: refusing to install \(app.name) \(rel.tagName) — \(reason). (Re-run with --tag \(rel.tagName) to install it anyway, unverified.)\n", stderr)
                    exit(1)
                }
                switch verification {
                case .verified:       print("Verified \(asset.name) (sha256).")
                case .noManifest:     print("⚠︎ \(asset.name) installed unverified (older tag; no SHA256SUMS).")
                case .assetNotListed: print("⚠︎ \(asset.name) installed unverified (older tag; not in SHA256SUMS).")
                }
                let dest = try im.install(app: app, version: rel.tagName, downloadedZip: zip, toApplications: toApplications)
                try? FileManager.default.removeItem(at: zip)
                AppLog.shared.log("cli: installed \(app.id) \(rel.tagName)\(toApplications ? " (Applications)" : "")\(verification == .verified ? " (sha256 ok)" : " (unverified)")")
                print("Installed \(app.name) \(rel.tagName) → \(dest.path)")
            } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    private static func uninstall(catalog: Catalog, id: String?) {
        guard let app = appFor(id, catalog) else { fputs("error: pass an app id.\n", stderr); exit(1) }
        do {
            try InstallManager.shared.uninstall(app.id)
            AppLog.shared.log("cli: uninstalled \(app.id)")
            print("Uninstalled \(app.name).")
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
        let client = GitHubClient(token: token)
        runBlocking {
            do {
                let info = try await client.latestRelease(owner: s.owner, repo: s.repo)
                let newer = isNewer(info.tagName, than: current)
                print("JB Theatre Tools: current v\(current), latest \(info.tagName) → \(newer ? "UPDATE AVAILABLE" : "up to date")")
            } catch GitHubError.noRelease {
                print("JB Theatre Tools: current v\(current), no launcher release published yet")
            } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    private static func selfDownload(catalog: Catalog, token: String?, dir: String?) {
        guard let s = catalog.selfInfo else { fputs("error: no `self` entry in catalog.\n", stderr); exit(1) }
        let destDir = dir ?? NSTemporaryDirectory()
        let client = GitHubClient(token: token)
        runBlocking {
            do {
                let info = try await client.latestRelease(owner: s.owner, repo: s.repo)
                guard let asset = info.assets.first(where: { $0.name == s.macAssetName }) else {
                    fputs("error: release \(info.tagName) has no macOS asset.\n", stderr); exit(1)
                }
                let dest = URL(fileURLWithPath: destDir).appendingPathComponent(asset.name)
                print("Downloading \(asset.name) (\(byteString(asset.size))) @ \(info.tagName)…")
                try await client.downloadAsset(owner: s.owner, repo: s.repo, assetId: asset.id, to: dest)
                print("Saved → \(dest.path)")
            } catch { fputs("error: \(error.localizedDescription)\n", stderr); exit(1) }
        }
    }

    // MARK: - Helpers

    private static func appFor(_ id: String?, _ catalog: Catalog) -> CatalogApp? {
        guard let id = id else { return nil }
        return catalog.apps.first { $0.id == id }
    }

    private static func isNewer(_ a: String, than b: String) -> Bool {
        func parts(_ s: String) -> [Int] {
            var t = s.trimmingCharacters(in: .whitespaces)
            if t.hasPrefix("v") || t.hasPrefix("V") { t.removeFirst() }
            return t.split(separator: ".").map { Int($0.prefix { $0.isNumber }) ?? 0 }
        }
        let pa = parts(a), pb = parts(b)
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }

    private static func printHelp() {
        print("""
        JBTheatreTools — theatre/AV app launcher (headless CLI)

          --list                 List the catalog with installed & latest versions
          --installed            Show locally installed apps
          --releases  <id>       List all available versions of an app
          --install   <id>       Download & install (latest, or --tag vX.Y.Z for an older one)
          --uninstall <id>       Remove an installed app
          --launch    <id>       Launch an installed app
          --self-check           Check whether a newer launcher release exists
          --help                 This help

        Options: --token <pat>       GitHub PAT (else $GITHUB_TOKEN, else Keychain)
                 --tag <vX.Y.Z>      Install a specific release (with --install)
                 --to-applications   Install into the Applications folder (with --install)
                 --catalog <path>    Use a specific catalog.json

        Note: a token passed via --token is visible to other local users (process list / shell
              history). Prefer $GITHUB_TOKEN or the saved Keychain token where possible.
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
