package com.jamesbreedon.jbtheatretools.core

import java.io.File
import java.security.MessageDigest

/** SHA256SUMS manifest parsing — the shapes `sha256sum` / `shasum` actually produce. */
object Sha256Sums {
    /**
     * The hash listed for [assetName] in a SHA256SUMS text, or null when it isn't listed. Handles
     * "hash  name", "hash *name" (binary-mode marker), tab separators and CRLF line endings. The
     * name must match exactly (case-sensitive, whole name).
     */
    fun expected(assetName: String, sumsText: String): String? {
        for (raw in sumsText.split('\n')) {
            val line = raw.trim()
            val sep = line.indexOfFirst { it == ' ' || it == '\t' }
            if (sep <= 0) continue
            val hash = line.substring(0, sep)
            var name = line.substring(sep + 1).trim()
            if (name.startsWith("*")) name = name.substring(1)   // sha256sum "binary mode" marker
            if (name == assetName) return hash
        }
        return null
    }

    /** Streams [file] through SHA-256 and returns the lowercase hex digest. */
    fun sha256Hex(file: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buf = ByteArray(1 shl 16)
            while (true) {
                val n = input.read(buf)
                if (n <= 0) break
                md.update(buf, 0, n)
            }
        }
        return md.digest().toHex()
    }

    fun sha256Hex(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).toHex()

    /** Constant-time-ish, case-insensitive hex comparison. */
    fun hexEquals(a: String, b: String): Boolean = a.equals(b, ignoreCase = true)

    internal fun ByteArray.toHex(): String {
        val out = StringBuilder(size * 2)
        for (b in this) {
            val v = b.toInt() and 0xFF
            out.append("0123456789abcdef"[v ushr 4])
            out.append("0123456789abcdef"[v and 0x0F])
        }
        return out.toString()
    }
}
