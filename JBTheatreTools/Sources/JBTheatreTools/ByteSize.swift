import Foundation

/// Human byte counts for download sizes, storage use and the disk-space check — the same wording on macOS,
/// Windows and Android. Decimal units (1 KB = 1000 bytes, as Finder shows them); one decimal under 10
/// ("4.2 MB"), whole numbers from 10 up ("450 MB"). A value that rounds up to 1000 moves up a unit ("1.0 MB",
/// never "1000 KB").
enum ByteSize {
    private static let units = ["KB", "MB", "GB", "TB"]

    static func format(_ bytes: Int64) -> String {
        let b = max(0, bytes)
        if b < 1000 { return b == 1 ? "1 byte" : "\(b) bytes" }
        var v = Double(b) / 1000
        var u = 0
        while true {
            if v < 10 {
                let r = round(v, places: 1)
                if r < 10 { return String(format: "%.1f", r) + " " + units[u] }
            }
            let n = round(v, places: 0)
            if n < 1000 || u == units.count - 1 { return "\(Int64(n)) \(units[u])" }
            v /= 1000
            u += 1
        }
    }

    /// Half away from zero on the DECIMAL value (as the Windows and Android builds round), so 9.95 → 10.0.
    private static func round(_ v: Double, places: Int) -> Double {
        var d = Decimal(string: String(v)) ?? Decimal(v)
        var r = Decimal()
        NSDecimalRound(&r, &d, places, .plain)
        return NSDecimalNumber(decimal: r).doubleValue
    }

    /// Sum that never overflows (saturates) and ignores negative "unknown" sizes.
    static func sum<S: Sequence>(_ sizes: S) -> Int64 where S.Element == Int64 {
        var total: Int64 = 0
        for s in sizes where s > 0 {
            total = s > Int64.max - total ? Int64.max : total + s
        }
        return total
    }
}
