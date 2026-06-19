import Foundation

struct ReleaseAsset: Decodable {
    let id: Int
    let name: String
    let size: Int
}

struct ReleaseInfo: Decodable, Identifiable {
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
    case http(Int)
    case assetNotFound(String)
    case badResponse

    var errorDescription: String? {
        switch self {
        case .noRelease: return "No published release found."
        case .http(let c): return "GitHub returned HTTP \(c)."
        case .assetNotFound(let n): return "Release has no asset named “\(n)”."
        case .badResponse: return "Unexpected response from GitHub."
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
final class GitHubClient: NSObject {
    private let token: String?
    private let lock = NSLock()
    private var contexts: [Int: DownloadContext] = [:]

    private lazy var session: URLSession = {
        let queue = OperationQueue()
        queue.maxConcurrentOperationCount = 1
        return URLSession(configuration: .default, delegate: self, delegateQueue: queue)
    }()

    /// `token` may be nil for unauthenticated calls against public repos (e.g. the self-update check).
    init(token: String?) {
        self.token = token
        super.init()
    }

    private func apiRequest(_ url: URL, accept: String) -> URLRequest {
        var req = URLRequest(url: url)
        if let token = token, !token.isEmpty {
            req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        }
        req.setValue(accept, forHTTPHeaderField: "Accept")
        req.setValue("2022-11-28", forHTTPHeaderField: "X-GitHub-Api-Version")
        req.setValue("JBTheatreTools", forHTTPHeaderField: "User-Agent")
        return req
    }

    /// Fetches the latest (non-prerelease) release. Throws `.noRelease` on 404.
    func latestRelease(owner: String, repo: String) async throws -> ReleaseInfo {
        let url = URL(string: "https://api.github.com/repos/\(owner)/\(repo)/releases/latest")!
        let req = apiRequest(url, accept: "application/vnd.github+json")
        let (data, resp) = try await session.data(for: req)
        guard let http = resp as? HTTPURLResponse else { throw GitHubError.badResponse }
        if http.statusCode == 404 { throw GitHubError.noRelease }
        guard http.statusCode == 200 else { throw GitHubError.http(http.statusCode) }
        return try JSONDecoder().decode(ReleaseInfo.self, from: data)
    }

    /// Fetches all (non-draft) releases, newest first — used for the version picker.
    func releases(owner: String, repo: String) async throws -> [ReleaseInfo] {
        let url = URL(string: "https://api.github.com/repos/\(owner)/\(repo)/releases?per_page=50")!
        let req = apiRequest(url, accept: "application/vnd.github+json")
        let (data, resp) = try await session.data(for: req)
        guard let http = resp as? HTTPURLResponse else { throw GitHubError.badResponse }
        if http.statusCode == 404 { throw GitHubError.noRelease }
        guard http.statusCode == 200 else { throw GitHubError.http(http.statusCode) }
        return try JSONDecoder().decode([ReleaseInfo].self, from: data).filter { !$0.draft }
    }

    /// Downloads a release asset by id to `dest`, reporting fractional progress (0…1).
    func downloadAsset(owner: String, repo: String, assetId: Int, to dest: URL,
                       progress: (@Sendable (Double) -> Void)? = nil) async throws {
        let url = URL(string: "https://api.github.com/repos/\(owner)/\(repo)/releases/assets/\(assetId)")!
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
