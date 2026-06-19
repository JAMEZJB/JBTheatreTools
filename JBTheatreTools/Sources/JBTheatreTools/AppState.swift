import Foundation
import SwiftUI

/// Observable view model backing the launcher UI. All work runs on the main actor;
/// network calls suspend rather than block, so the UI stays responsive.
@MainActor
final class AppState: ObservableObject {

    enum Status: Equatable {
        case unknown
        case checking
        case noRelease
        case missingAsset
        case notInstalled
        case upToDate
        case updateAvailable
        case error(String)
    }

    struct Row: Identifiable {
        let app: CatalogApp
        var id: String { app.id }
        var latest: String?
        var latestAssetId: Int?
        var installed: String?
        var status: Status = .unknown
        var busy: Bool = false
        var progress: Double = 0
    }

    @Published var rows: [Row] = []
    @Published var hasToken: Bool = TokenStore.load() != nil
    @Published var globalError: String?

    init() {
        do {
            let catalog = try Catalog.load()
            rows = catalog.apps.map {
                Row(app: $0, installed: InstallManager.shared.installedVersion($0.id))
            }
        } catch {
            globalError = "Could not load app catalog: \(error.localizedDescription)"
        }
    }

    // MARK: - Token

    func setToken(_ token: String) {
        let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        TokenStore.save(trimmed)
        hasToken = true
    }

    func clearToken() {
        TokenStore.clear()
        hasToken = false
        for i in rows.indices {
            rows[i].status = .unknown
            rows[i].latest = nil
            rows[i].latestAssetId = nil
        }
    }

    // MARK: - Refresh

    func refreshAll() async {
        guard let token = TokenStore.load() else { return }
        let client = GitHubClient(token: token)
        for i in rows.indices {
            await refresh(index: i, client: client)
        }
    }

    private func refresh(index: Int, client: GitHubClient) async {
        let app = rows[index].app
        rows[index].status = .checking
        rows[index].installed = InstallManager.shared.installedVersion(app.id)
        do {
            let info = try await client.latestRelease(owner: app.owner, repo: app.repo)
            let assetId = info.assets.first { $0.name == app.macAssetName }?.id
            rows[index].latest = info.tagName
            rows[index].latestAssetId = assetId
            rows[index].status = Self.status(installed: rows[index].installed, latest: info.tagName, hasAsset: assetId != nil)
        } catch GitHubError.noRelease {
            rows[index].latest = nil
            rows[index].status = .noRelease
        } catch {
            rows[index].status = .error(error.localizedDescription)
        }
    }

    private static func status(installed: String?, latest: String, hasAsset: Bool) -> Status {
        guard hasAsset else { return .missingAsset }
        guard let installed = installed else { return .notInstalled }
        return versionsEqual(installed, latest) ? .upToDate : .updateAvailable
    }

    private static func versionsEqual(_ a: String, _ b: String) -> Bool {
        func norm(_ s: String) -> String {
            var t = s.trimmingCharacters(in: .whitespaces)
            if t.hasPrefix("v") || t.hasPrefix("V") { t.removeFirst() }
            return t
        }
        return norm(a) == norm(b)
    }

    // MARK: - Install / update / launch

    func install(_ id: String) async {
        guard let token = TokenStore.load(),
              let i = rows.firstIndex(where: { $0.id == id }),
              let assetId = rows[i].latestAssetId,
              let latest = rows[i].latest else { return }
        let app = rows[i].app
        rows[i].busy = true
        rows[i].progress = 0
        defer { rows[i].busy = false }

        let client = GitHubClient(token: token)
        let zipDest = InstallManager.shared.cacheDir
            .appendingPathComponent("\(app.id)-\(latest).zip")
        let appId = id
        do {
            try await client.downloadAsset(owner: app.owner, repo: app.repo, assetId: assetId, to: zipDest) { p in
                Task { @MainActor [weak self] in
                    guard let self = self,
                          let j = self.rows.firstIndex(where: { $0.id == appId }) else { return }
                    self.rows[j].progress = p
                }
            }
            try InstallManager.shared.install(app: app, version: latest, downloadedZip: zipDest)
            try? FileManager.default.removeItem(at: zipDest)
            rows[i].installed = latest
            rows[i].status = .upToDate
        } catch {
            rows[i].status = .error(error.localizedDescription)
        }
    }

    func launch(_ id: String) {
        guard let i = rows.firstIndex(where: { $0.id == id }) else { return }
        do {
            try InstallManager.shared.launch(app: rows[i].app)
        } catch {
            rows[i].status = .error(error.localizedDescription)
        }
    }
}
