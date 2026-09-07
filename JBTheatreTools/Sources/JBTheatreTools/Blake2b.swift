import Foundation

/// BLAKE2b-512 (RFC 7693), unkeyed, 64-byte digest.
///
/// minisign's default ("ED") signatures are Ed25519 over the BLAKE2b-512 hash of the signed file, and
/// neither CryptoKit nor CommonCrypto provides BLAKE2b — so this is a straight transcription of the
/// RFC (no key, no salt, no tree hashing). It is exercised by unit tests against the RFC's known
/// answers, Python's hashlib on block boundaries, and the suite's real signed release manifests.
enum Blake2b {
    private static let iv: [UInt64] = [
        0x6a09_e667_f3bc_c908, 0xbb67_ae85_84ca_a73b, 0x3c6e_f372_fe94_f82b, 0xa54f_f53a_5f1d_36f1,
        0x510e_527f_ade6_82d1, 0x9b05_688c_2b3e_6c1f, 0x1f83_d9ab_fb41_bd6b, 0x5be0_cd19_137e_2179,
    ]

    private static let sigma: [[Int]] = [
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
        [11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4],
        [7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8],
        [9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13],
        [2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9],
        [12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11],
        [13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10],
        [6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5],
        [10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0],
        [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
        [14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3],
    ]

    /// The 64-byte BLAKE2b digest of `message`.
    static func hash512(_ message: Data) -> Data {
        var h = iv
        h[0] ^= 0x0101_0000 ^ 64          // parameter block: digest length 64, key length 0, fanout 1, depth 1
        let bytes = [UInt8](message)
        var t: UInt64 = 0                 // bytes compressed so far (inputs here are far below 2^64)
        var offset = 0
        // Every full block except the last: the final block — even for an empty message — is always
        // compressed with the "final" flag set.
        while bytes.count - offset > 128 {
            t &+= 128
            compress(&h, Array(bytes[offset..<(offset + 128)]), counter: t, final: false)
            offset += 128
        }
        var last = Array(bytes[offset...])
        t &+= UInt64(last.count)
        last += [UInt8](repeating: 0, count: 128 - last.count)
        compress(&h, last, counter: t, final: true)

        var out = Data(capacity: 64)
        for word in h {
            var le = word.littleEndian
            withUnsafeBytes(of: &le) { out.append(contentsOf: $0) }
        }
        return out
    }

    private static func compress(_ h: inout [UInt64], _ block: [UInt8], counter t: UInt64, final: Bool) {
        var m = [UInt64](repeating: 0, count: 16)
        for i in 0..<16 {
            var w: UInt64 = 0
            for j in 0..<8 { w |= UInt64(block[i * 8 + j]) << (8 * UInt64(j)) }
            m[i] = w
        }
        var v = h + iv
        v[12] ^= t                        // low word of the byte counter (the high word stays 0)
        if final { v[14] = ~v[14] }
        for r in 0..<12 {
            let s = sigma[r]
            g(&v, 0, 4, 8, 12, m[s[0]], m[s[1]])
            g(&v, 1, 5, 9, 13, m[s[2]], m[s[3]])
            g(&v, 2, 6, 10, 14, m[s[4]], m[s[5]])
            g(&v, 3, 7, 11, 15, m[s[6]], m[s[7]])
            g(&v, 0, 5, 10, 15, m[s[8]], m[s[9]])
            g(&v, 1, 6, 11, 12, m[s[10]], m[s[11]])
            g(&v, 2, 7, 8, 13, m[s[12]], m[s[13]])
            g(&v, 3, 4, 9, 14, m[s[14]], m[s[15]])
        }
        for i in 0..<8 { h[i] ^= v[i] ^ v[i + 8] }
    }

    @inline(__always)
    private static func g(_ v: inout [UInt64], _ a: Int, _ b: Int, _ c: Int, _ d: Int, _ x: UInt64, _ y: UInt64) {
        v[a] = v[a] &+ v[b] &+ x
        v[d] = rotr(v[d] ^ v[a], 32)
        v[c] = v[c] &+ v[d]
        v[b] = rotr(v[b] ^ v[c], 24)
        v[a] = v[a] &+ v[b] &+ y
        v[d] = rotr(v[d] ^ v[a], 16)
        v[c] = v[c] &+ v[d]
        v[b] = rotr(v[b] ^ v[c], 63)
    }

    @inline(__always)
    private static func rotr(_ x: UInt64, _ n: UInt64) -> UInt64 { (x >> n) | (x << (64 - n)) }
}
