import Foundation

struct ReleaseAsset: Decodable, Sendable {
    let id: Int
    let name: String
    let size: Int
}

struct ReleaseInfo: Decodable, Identifiable, Sendable {
    let tagName: String
    let assets: [ReleaseAsset]
    let prerelease: Bool
    let draft: Bool
    var id: String { tagName }
    enum CodingKeys: String, CodingKey {
        case tagName = "tag_name"
        case assets, prerelease, draft
    }
}

enum GitHubError: LocalizedError {
    case noRelease
    /// The token can't see this repository (private + not in the token's scope, or nonexistent).
    case notAccessible
    /// The token itself is invalid or expired (HTTP 401).
    case unauthorized
    case http(Int)
    case assetNotFound(String)
    case badResponse
    /// The request URL couldn't be formed (e.g. a malformed relay base). Guards against a force-unwrap
    /// crash on a bad `apiBase` (audit F2).
    case badURL

    var errorDescription: String? {
        switch self {
        case .noRelease: return "No published release found."
        case .notAccessible: return "This token can’t access that repository."
        case .unauthorized: return "GitHub token is invalid or expired."
        case .http(let c): return "GitHub returned HTTP \(c)."
        case .assetNotFound(let n): return "Release has no asset named “\(n)”."
        case .badResponse: return "Unexpected response from GitHub."
        case .badURL: return "The download-server address is invalid. Check it in Settings."
        }
    }
}

/// Talks to the GitHub REST API with a personal access token.
///
/// Private release assets cannot be fetched from `browser_download_url`; you must hit the API
/// asset endpoint with `Accept: application/octet-stream`, follow the 302 to the signed S3 URL,
/// and **strip the Authorization header on that cross-host redirect** — S3 rejects a request
/// that carries both a Bearer header and its own signed query params. We do that in the
/// `willPerformHTTPRedirection` delegate below.
/// `@unchecked Sendable`: `refreshAll` shares one client across concurrent `releases()` calls. Those use
/// only `URLSession.data(for:)` (thread-safe) and never touch the mutable download state (`contexts`, which
/// is `lock`-guarded and used only by the download-delegate path). So concurrent checks are safe.
final class GitHubClient: NSObject, @unchecked Sendable {
    /// Base of the GitHub REST API — `https://api.github.com` for direct (PAT) access, or the
    /// download-server relay's API root in server mode (the relay forwards the same paths to GitHub
    /// with its own server-side token, so every endpoint shape below is identical in both modes).
    private let apiBase: String
    /// Full `Authorization` header value: `Bearer <PAT>` (direct), `Basic <…>` (server mode), or nil.
    private let authValue: String?
    private let lock = NSLock()
    private var contexts: [Int: DownloadContext] = [:]

    private var sessionCreated = false
    private lazy var session: URLSession = {
        sessionCreated = true
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        return URLSession(configuration: .default, delegate: self, delegateQueue: queue)
    }()

    /// A `URLSession` with a delegate strongly retains that delegate (this client) until it's invalidated;
    /// AppState builds a fresh client per refresh/install/self-check, so without this each client + its
    /// session + connection pool would live for the whole process (audit F7). Only touch `session` if it
    /// was actually created (don't spin one up just to tear it down).
    deinit { if sessionCreated { session.finishTasksAndInvalidate() } }

    /// Direct GitHub access. `token` may be nil for unauthenticated calls against public repos
    /// (e.g. the self-update check).
    init(token: String?) {
        apiBase = "https://api.github.com"
        authValue = (token?.isEmpty == false) ? "Bearer \(token!)" : nil
        super.init()
    }

    /// Download-server (relay) access: same API paths, sent to the relay with HTTP Basic auth
    /// (fixed username "suite" + the suite passphrase). The relay injects its own GitHub token
    /// server-side and passes responses through — including the 302 to S3, which we follow with the
    /// Authorization header stripped exactly as in direct mode.
    init(serverBase: String, passphrase: String) {
        var base = serverBase.trimmingCharacters(in: .whitespacesAndNewlines)
        while base.hasSuffix("/") { base.removeLast() }
        apiBase = base
        // Normalise the passphrase before auth so entry is case- and spacing-insensitive; the relay
        // recognises the same normalised phrases. (An empty result — e.g. all-punctuation — just won't match.)
        let pass = Passphrase.normalize(passphrase)
        authValue = "Basic " + Data("suite:\(pass)".utf8).base64EncodedString()
        super.init()
    }

    private func apiRequest(_ url: URL, accept: String) -> URLRequest {
        var req = URLRequest(url: url)
        if let authValue = authValue {
            req.setValue(authValue, forHTTPHeaderField: "Authorization")
        }
        req.setValue(accept, forHTTPHeaderField: "Accept")
        req.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        req.setValue("JBTheatreTools", forHTTPHeaderField: "User-Agent")
        return req
    }

    /// Fetches the latest (non-prerelease) release. Throws `.noRelease` on 404.
    func latestRelease(owner: String, repo: String) async throws -> ReleaseInfo {
        guard let url = URL(string: "\(apiBase)/repos/\(owner)/\(repo)/releases/latest") else { throw GitHubError.badURL }
        let req = apiRequest(url, accept: "application/vnd.github+json")
        let (data, resp) = try await session.data(for: req)
        guard let http = resp as? HTTPURLResponse else { throw GitHubError.badResponse }
        if http.statusCode == 404 { throw GitHubError.noRelease }
        guard http.statusCode == 200 else { throw GitHubError.http(http.statusCode) }
        return try JSONDecoder().decode(ReleaseInfo.self, from: data)
    }

    /// Fetches all (non-draft) releases, newest first — used for the version picker and to gate
    /// which apps are shown. The list endpoint returns `200 []` for an accessible repo with no
    /// releases and `404` only when the token can't see the repo, so a 404 here means **no access**
    /// (not "no release") and a 401 means the token itself is bad.
    func releases(owner: String, repo: String) async throws -> [ReleaseInfo] {
        guard let url = URL(string: "\(apiBase)/repos/\(owner)/\(repo)/releases?per_page=50") else { throw GitHubError.badURL }
        let req = apiRequest(url, accept: "application/vnd.github+json")
        let (data, resp) = try await session.data(for: req)
        guard let http = resp as? HTTPURLResponse else { throw GitHubError.badResponse }
        if http.statusCode == 401 { throw GitHubError.unauthorized }
        if http.statusCode == 404 { throw GitHubError.notAccessible }
        guard http.statusCode == 200 else { throw GitHubError.http(http.statusCode) }
        return try JSONDecoder().decode([ReleaseInfo].self, from: data).filter { !$0.draft }
    }

    /// Downloads a release asset by id to `dest`, reporting fractional progress (0…1).
    func downloadAsset(owner: String, repo: String, assetId: Int, to dest: URL,
                       progress: (@Sendable (Double) -> Void)? = nil) async throws {
        guard let url = URL(string: "\(apiBase)/repos/\(owner)/\(repo)/releases/assets/\(assetId)") else { throw GitHubError.badURL }
        let req = apiRequest(url, accept: "application/octet-stream")
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let task = session.downloadTask(with: req)
            let ctx = DownloadContext(dest: dest, progress: progress, continuation: cont)
            lock.lock(); contexts[task.taskIdentifier] = ctx; lock.unlock()
            task.resume()
        }
    }

    private final class DownloadContext {
        let dest: URL
        let progress: (@Sendable (Double) -> Void)?
        let continuation: CheckedContinuation<Void, Error>
        var moveError: Error?
        init(dest: URL, progress: (@Sendable (Double) -> Void)?, continuation: CheckedContinuation<Void, Error>) {
            self.dest = dest
            self.progress = progress
            self.continuation = continuation
        }
    }

    private func context(for id: Int) -> DownloadContext? {
        lock.lock(); defer { lock.unlock() }
        return contexts[id]
    }

    private func removeContext(for id: Int) -> DownloadContext? {
        lock.lock(); defer { lock.unlock() }
        return contexts.removeValue(forKey: id)
    }
}

extension GitHubClient: URLSessionDownloadDelegate {
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didWriteData bytesWritten: Int64, totalBytesWritten: Int64,
                    totalBytesExpectedToWrite: Int64) {
        guard totalBytesExpectedToWrite > 0,
              let ctx = context(for: downloadTask.taskIdentifier) else { return }
        ctx.progress?(Double(totalBytesWritten) / Double(totalBytesExpectedToWrite))
    }

    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didFinishDownloadingTo location: URL) {
        guard let ctx = context(for: downloadTask.taskIdentifier) else { return }
        // Only move on success — on an error status the body is a JSON error blob we discard.
        if let http = downloadTask.response as? HTTPURLResponse, http.statusCode != 200 { return }
        let fm = FileManager.default
        do {
            try? fm.removeItem(at: ctx.dest)
            try fm.createDirectory(at: ctx.dest.deletingLastPathComponent(),
                                   withIntermediateDirectories: true)
            try fm.moveItem(at: location, to: ctx.dest)
        } catch {
            ctx.moveError = error
        }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let ctx = removeContext(for: task.taskIdentifier) else { return }
        if let http = task.response as? HTTPURLResponse, http.statusCode != 200 {
            ctx.continuation.resume(throwing: GitHubError.http(http.statusCode)); return
        }
        if let error = error { ctx.continuation.resume(throwing: error); return }
        if let moveError = ctx.moveError { ctx.continuation.resume(throwing: moveError); return }
        ctx.progress?(1.0)
        ctx.continuation.resume()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        var req = request
        req.setValue(nil, forHTTPHeaderField: "Authorization")
        completionHandler(req)
    }
}
