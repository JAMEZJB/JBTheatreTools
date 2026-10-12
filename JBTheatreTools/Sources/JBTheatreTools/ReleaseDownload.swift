import Foundation

/// `--download`: fetches one release asset (any platform's) plus that release's SHA256SUMS and SHA256SUMS.minisig into a
/// folder, for a PC that has no internet of its own (Stagehand copies the three files over and runs `--install-file`
/// there). The files are checked here with the same rules as `--install-file`: the signature must verify with the
/// embedded suite key for "<repo> <tag>", the asset must be listed in the signed SHA256SUMS with a matching hash, and its
/// size must be the one the release lists. Each file is downloaded under a hidden staging name in the folder and only
/// renamed into place once all three check out, so a failure leaves nothing behind (and never touches a file that was
/// already there).
enum ReleaseDownload {
    struct Result: Equatable {
        let path: URL
        let sums: URL
        let sig: URL
        let size: Int
        let sha256: String
    }

    enum Failure: LocalizedError, Equatable {
        case badAssetName(String)
        case missingFile(String)
        case sizeMismatch(String, expected: Int, got: Int)
        case notListed(String, release: String)
        case checksumMismatch(String, release: String)
        case badSignature(String)

        var errorDescription: String? {
            switch self {
            case .badAssetName(let n): return "“\(n)” isn't a release file name."
            case .missingFile(let n): return "The download of \(n) left no file."
            case .sizeMismatch(let n, let e, let g): return "\(n) is \(g) bytes but the release lists \(e)."
            case .notListed(let n, let r): return "\(n) isn't listed in \(r)'s signed SHA256SUMS."
            case .checksumMismatch(let n, let r): return "Checksum mismatch for \(n): the file doesn't match \(r)'s signed SHA256SUMS."
            case .badSignature(let why): return "Refusing the release's SHA256SUMS: \(why)"
            }
        }
    }

    static let sumsName = "SHA256SUMS"
    static let sigName = "SHA256SUMS.minisig"

    /// A release file name that is safe to write into the folder as is (no folders, no "..", not the manifest itself).
    static func isPlainAssetName(_ name: String) -> Bool {
        !name.isEmpty && name == PathSafe.component(name) && name != "." && name != ".."
            && name != sumsName && name != sigName && !name.hasPrefix(".")
    }

    /// Downloads `assetName`, SHA256SUMS and SHA256SUMS.minisig into `folder` and verifies them for "<repo> <tag>".
    /// `fetch(name, dest)` writes the release file `name` to `dest` (the CLI passes the launcher's own client).
    /// `expectedSize` is the size the release lists for the asset (0 = don't check).
    static func run(folder: URL, assetName: String, expectedSize: Int, repo: String, tag: String,
                    publicKey: String = Minisign.suitePublicKey,
                    fetch: (String, URL) async throws -> Void) async throws -> Result {
        guard isPlainAssetName(assetName) else { throw Failure.badAssetName(assetName) }
        let fm = FileManager.default
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let stamp = UUID().uuidString.prefix(8)
        let names = [assetName, sumsName, sigName]
        let staged = names.map { folder.appendingPathComponent(".\($0).download-\(stamp)") }
        defer { for url in staged { try? fm.removeItem(at: url) } }   // whatever happens, no staging file stays

        for (name, url) in zip(names, staged) {
            try await fetch(name, url)
            guard fm.fileExists(atPath: url.path) else { throw Failure.missingFile(name) }
        }
        let (size, sha) = try verify(file: staged[0], assetName: assetName, expectedSize: expectedSize,
                                     sums: staged[1], sig: staged[2], repo: repo, tag: tag, publicKey: publicKey)

        // All three check out: rename into place (same folder, so each rename is atomic). Should one fail, take back
        // the ones already placed, so the folder never holds an asset beside a SHA256SUMS from somewhere else.
        let finals = names.map { folder.appendingPathComponent($0) }
        var placed: [URL] = []
        do {
            for (from, to) in zip(staged, finals) {
                if fm.fileExists(atPath: to.path) {
                    _ = try fm.replaceItemAt(to, withItemAt: from)
                } else {
                    try fm.moveItem(at: from, to: to)
                }
                placed.append(to)
            }
        } catch {
            for url in placed { try? fm.removeItem(at: url) }
            throw error
        }
        return Result(path: finals[0], sums: finals[1], sig: finals[2], size: size, sha256: sha)
    }

    /// The `--install-file` rules for a file with its release's SHA256SUMS + .minisig beside it. Returns (size, sha256).
    static func verify(file: URL, assetName: String, expectedSize: Int, sums: URL, sig: URL,
                       repo: String, tag: String, publicKey: String = Minisign.suitePublicKey) throws -> (Int, String) {
        let release = "\(repo) \(tag)"
        let sumsData = try Data(contentsOf: sums)
        let sigText = String(decoding: try Data(contentsOf: sig), as: UTF8.self)
        do {
            try Minisign.verify(manifest: sumsData, minisig: sigText, expectedTrustedComment: release, publicKeyBase64: publicKey)
        } catch {
            throw Failure.badSignature(error.localizedDescription)
        }
        let attrs = try FileManager.default.attributesOfItem(atPath: file.path)
        let size = (attrs[.size] as? NSNumber)?.intValue ?? -1
        if expectedSize > 0, size != expectedSize { throw Failure.sizeMismatch(assetName, expected: expectedSize, got: size) }
        guard let expected = InstallManager.expectedSHA256(forAsset: assetName, inSums: String(decoding: sumsData, as: UTF8.self)) else {
            throw Failure.notListed(assetName, release: release)
        }
        let actual = try InstallManager.sha256Hex(of: file)
        guard actual.caseInsensitiveCompare(expected) == .orderedSame else { throw Failure.checksumMismatch(assetName, release: release) }
        return (size, actual)
    }
}
