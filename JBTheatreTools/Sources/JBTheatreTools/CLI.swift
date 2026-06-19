import Foundation

/// Headless command-line interface — used for scripting and for verifying the catalog,
/// GitHub API access, asset resolution, and the download/install path without the GUI.
///
/// Token resolution order: `--token <pat>`, then `$GITHUB_TOKEN`, then the Keychain.
///
///   JBTheatreTools --list            [--token X] [--catalog path]
///   JBTheatreTools --installed
///   JBTheatreTools --install <id>    [--token X]
///   JBTheatreTools --launch  <id>
///   JBTheatreTools --help
enum CLI {
    static let commands: Set<String> = ["--list", "--installed", "--install", "--launch", "--help", "-h"]

    static func run(args: [String]) {
        var args = args
        let cmd = args.removeFirst()
        if cmd == "--help" || cmd == "-h" { printHelp(); return }

        var token = ProcessInfo.processInfo.environment["GITHUB_TOKEN"]
        var catalogPath: String?
        var positional: [String] = []
        var i = 0
        while i < args.count {
            switch args[i] {
            case "--token":   i += 1; token = i < args.count ? args[i] : nil
            case "--catalog": i += 1; catalogPath = i < args.count ? args[i] : nil
            default:          positional.append(args[i])
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
        case "--list":      list(catalog: catalog, token: token)
        case "--installed": installed(catalog: catalog)
        case "--install":   install(catalog: catalog, token: token, id: positional.first)
        case "--launch":    launch(catalog: catalog, id: positional.first)
        default:            printHelp()
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
                        let info = try await client.latestRelease(owner: app.owner, repo: app.repo)
                        latest = info.tagName
                        if let a = info.assets.first(where: { $0.name == app.macAssetName }) {
                            note = "\(a.name) (\(byteString(a.size)))"
                        } else {
                            note = "⚠︎ no macOS asset (\(app.macAssetName ?? "?"))"
                        }
                    } catch GitHubError.noRelease {
                        latest = "none"; note = "no published release"
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

    private static func install(catalog: Catalog, token: String?, id: String?) {
        guard let id = id, let app = catalog.apps.first(where: { $0.id == id }) else {
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
                let info = try await client.latestRelease(owner: app.owner, repo: app.repo)
                guard let asset = info.assets.first(where: { $0.name == app.macAssetName }) else {
                    fputs("error: release \(info.tagName) has no macOS asset.\n", stderr); exit(1)
                }
                print("Downloading \(asset.name) (\(byteString(asset.size))) @ \(info.tagName)…")
                let zip = im.cacheDir.appendingPathComponent("\(app.id)-\(info.tagName).zip")
                // Delegate callbacks are serialised, so an @unchecked Sendable box is safe here.
                final class PctBox: @unchecked Sendable { var last = -1 }
                let pctBox = PctBox()
                try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: asset.id, to: zip) { p in
                    let pct = Int(p * 100)
                    if pct != pctBox.last, pct % 10 == 0 { pctBox.last = pct; print("  \(pct)%") }
                }
                let dest = try im.install(app: app, version: info.tagName, downloadedZip: zip)
                try? FileManager.default.removeItem(at: zip)
                print("Installed \(app.name) \(info.tagName) → \(dest.path)")
            } catch {
                fputs("error: \(error.localizedDescription)\n", stderr); exit(1)
            }
        }
    }

    private static func launch(catalog: Catalog, id: String?) {
        guard let id = id, let app = catalog.apps.first(where: { $0.id == id }) else {
            fputs("error: pass an app id.\n", stderr); exit(1)
        }
        do {
            try InstallManager.shared.launch(app: app)
            print("Launched \(app.name).")
        } catch {
            fputs("error: \(error.localizedDescription)\n", stderr); exit(1)
        }
    }

    // MARK: - Helpers

    private static func printHelp() {
        print("""
        JBTheatreTools — theatre/AV app launcher (headless CLI)

          --list                 List the catalog with installed & latest versions
          --installed            Show locally installed apps
          --install <id>         Download & install an app's latest macOS build
          --launch  <id>         Launch an installed app
          --help                 This help

        Options: --token <pat>   GitHub PAT (else $GITHUB_TOKEN, else Keychain)
                 --catalog <path> Use a specific catalog.json
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
