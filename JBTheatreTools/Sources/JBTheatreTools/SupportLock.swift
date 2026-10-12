import Foundation

/// One lock shared by every launcher process that uses a support folder: the window and a command-line install can
/// run at once on a Mac. Whoever holds it is the only one changing installed.json, the installed bundles or
/// connectors.json, and reads them from disk. It is a `flock` on `.lock` in the folder (an advisory lock on the open
/// file, so a crashed holder releases it with its descriptor, and two descriptors conflict even within one process).
/// Re-entrant on the holding thread: an install that commits its manifest brings connectors.json in step under the
/// same hold. `hold` takes a synchronous body, so a hold can never span an `await`; it may wait, so the window calls
/// it off the main thread.
final class SupportLock: @unchecked Sendable {
    static let defaultWait: TimeInterval = 120

    /// The other process kept the lock for the whole wait.
    struct Busy: LocalizedError {
        var errorDescription: String? {
            "Another JB Theatre Tools window or command is still installing. Try again when it has finished."
        }
    }

    let url: URL
    /// Threads of this process take turns here first (a recursive lock: the holder may hold again); only the
    /// outermost hold opens and locks the file. `depth` and `fd` are touched only by the thread holding `gate`.
    private let gate = NSRecursiveLock()
    private var depth = 0
    private var fd: Int32 = -1

    init(directory: URL) { url = directory.appendingPathComponent(".lock") }

    /// Runs `body` holding the lock, waiting up to `wait` seconds (default two minutes) for other threads and
    /// processes; then throws `Busy`.
    func hold<T>(wait: TimeInterval = SupportLock.defaultWait, _ body: () throws -> T) throws -> T {
        let deadline = Date(timeIntervalSinceNow: wait)
        guard gate.lock(before: deadline) else { throw Busy() }
        defer { gate.unlock() }
        if depth == 0 { fd = try lockFile(until: deadline) }
        depth += 1
        defer {
            depth -= 1
            if depth == 0 { Darwin.close(fd); fd = -1 }   // closing the descriptor releases the flock
        }
        return try body()
    }

    private func lockFile(until deadline: Date) throws -> Int32 {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let file = Darwin.open(url.path, O_RDWR | O_CREAT | O_CLOEXEC, 0o600)
        guard file >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        while flock(file, LOCK_EX | LOCK_NB) != 0 {
            let err = errno
            guard err == EWOULDBLOCK || err == EINTR else {
                Darwin.close(file)
                throw POSIXError(POSIXErrorCode(rawValue: err) ?? .EIO)
            }
            guard Date() < deadline else {
                Darwin.close(file)
                throw Busy()
            }
            usleep(50_000)
        }
        return file
    }
}
