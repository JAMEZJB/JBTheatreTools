package com.jamesbreedon.jbtheatretools.core

/**
 * BLAKE2b-512 (RFC 7693), unkeyed, 64-byte digest.
 *
 * minisign's default ("ED") signatures are Ed25519 over the BLAKE2b-512 hash of the signed file, and
 * the platform provides no BLAKE2b — so this is a straight transcription of the RFC (no key, no salt,
 * no tree hashing), matching the macOS launcher's `Blake2b.swift` byte for byte.
 */
object Blake2b {
    private val IV = longArrayOf(
        0x6a09e667f3bcc908uL.toLong(), 0xbb67ae8584caa73buL.toLong(),
        0x3c6ef372fe94f82buL.toLong(), 0xa54ff53a5f1d36f1uL.toLong(),
        0x510e527fade682d1uL.toLong(), 0x9b05688c2b3e6c1fuL.toLong(),
        0x1f83d9abfb41bd6buL.toLong(), 0x5be0cd19137e2179uL.toLong(),
    )

    private val SIGMA = arrayOf(
        intArrayOf(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
        intArrayOf(14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3),
        intArrayOf(11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4),
        intArrayOf(7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8),
        intArrayOf(9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13),
        intArrayOf(2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9),
        intArrayOf(12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11),
        intArrayOf(13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10),
        intArrayOf(6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5),
        intArrayOf(10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0),
        intArrayOf(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15),
        intArrayOf(14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3),
    )

    /** The 64-byte BLAKE2b digest of [message]. */
    fun hash512(message: ByteArray): ByteArray {
        val h = IV.copyOf()
        h[0] = h[0] xor 0x01010000L xor 64L   // parameter block: digest 64, key 0, fanout 1, depth 1
        var t = 0L                            // bytes compressed so far
        var offset = 0
        // Every full block except the last: the final block — even for an empty message — is always
        // compressed with the "final" flag set.
        while (message.size - offset > 128) {
            t += 128
            compress(h, message, offset, t, false)
            offset += 128
        }
        val last = ByteArray(128)
        val remaining = message.size - offset
        System.arraycopy(message, offset, last, 0, remaining)
        t += remaining.toLong()
        compress(h, last, 0, t, true)

        val out = ByteArray(64)
        for (i in 0 until 8) {
            var w = h[i]
            for (j in 0 until 8) {
                out[i * 8 + j] = (w and 0xFF).toByte()
                w = w ushr 8
            }
        }
        return out
    }

    private fun compress(h: LongArray, block: ByteArray, blockOffset: Int, t: Long, final: Boolean) {
        val m = LongArray(16)
        for (i in 0 until 16) {
            var w = 0L
            for (j in 0 until 8) {
                w = w or ((block[blockOffset + i * 8 + j].toLong() and 0xFF) shl (8 * j))
            }
            m[i] = w
        }
        val v = LongArray(16)
        System.arraycopy(h, 0, v, 0, 8)
        System.arraycopy(IV, 0, v, 8, 8)
        v[12] = v[12] xor t                   // low word of the byte counter (high word stays 0)
        if (final) v[14] = v[14].inv()
        for (r in 0 until 12) {
            val s = SIGMA[r]
            g(v, 0, 4, 8, 12, m[s[0]], m[s[1]])
            g(v, 1, 5, 9, 13, m[s[2]], m[s[3]])
            g(v, 2, 6, 10, 14, m[s[4]], m[s[5]])
            g(v, 3, 7, 11, 15, m[s[6]], m[s[7]])
            g(v, 0, 5, 10, 15, m[s[8]], m[s[9]])
            g(v, 1, 6, 11, 12, m[s[10]], m[s[11]])
            g(v, 2, 7, 8, 13, m[s[12]], m[s[13]])
            g(v, 3, 4, 9, 14, m[s[14]], m[s[15]])
        }
        for (i in 0 until 8) h[i] = h[i] xor v[i] xor v[i + 8]
    }

    private fun g(v: LongArray, a: Int, b: Int, c: Int, d: Int, x: Long, y: Long) {
        v[a] = v[a] + v[b] + x
        v[d] = java.lang.Long.rotateRight(v[d] xor v[a], 32)
        v[c] = v[c] + v[d]
        v[b] = java.lang.Long.rotateRight(v[b] xor v[c], 24)
        v[a] = v[a] + v[b] + y
        v[d] = java.lang.Long.rotateRight(v[d] xor v[a], 16)
        v[c] = v[c] + v[d]
        v[b] = java.lang.Long.rotateRight(v[b] xor v[c], 63)
    }
}
