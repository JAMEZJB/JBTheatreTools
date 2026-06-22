import Foundation
import AppKit

/// Minimal append-only diagnostics log at `~/Library/Logs/JBTheatreTools/JBTheatreTools.log`.
/// Records installs / updates / uninstalls / launches and any refresh, install or relocation errors so
/// show-day failures can be diagnosed after the fact. Never logs the token value.
final class AppLog {
    static let shared = AppLog()

    let fileURL: URL
    private let fm = FileManager.default
    private let queue = DispatchQueue(label: "theatre.applog")

    init() {
        let dir = fm.urls(for: .libraryDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Logs/JBTheatreTools", isDirectory: true)
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        fileURL = dir.appendingPathComponent("JBTheatreTools.log")
        trimIfLarge()
    }

    /// Appends a timestamped line. Best-effort — logging never throws into the caller.
    func log(_ message: String) {
        queue.async { [fileURL, fm] in
            let line = "\(Self.stamp())  \(message)\n"
            guard let data = line.data(using: .utf8) else { return }
            if fm.fileExists(atPath: fileURL.path), let handle = try? FileHandle(forWritingTo: fileURL) {
                defer { try? handle.close() }
                _ = try? handle.seekToEnd()
                try? handle.write(contentsOf: data)
            } else {
                try? data.write(to: fileURL)
            }
        }
    }

    /// Opens the log in the user's default text viewer (creating it if it doesn't exist yet).
    func open() {
        if !fm.fileExists(atPath: fileURL.path) { try? Data().write(to: fileURL) }
        NSWorkspace.shared.open(fileURL)
    }

    /// Keep the log from growing without bound — on launch, if it's over ~1 MB keep just the tail.
    private func trimIfLarge() {
        guard let size = try? fm.attributesOfItem(atPath: fileURL.path)[.size] as? Int, size > 1_000_000,
              let data = try? Data(contentsOf: fileURL) else { return }
        try? data.suffix(200_000).write(to: fileURL)
    }

    private static func stamp() -> String {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return f.string(from: Date())
    }
}
