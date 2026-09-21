package com.jamesbreedon.jbtheatretools.core

import java.security.MessageDigest

/**
 * The suite's Android signing certificate, pinned fail-closed.
 *
 * One key signs every app in the suite and the launcher itself. Before an APK is handed to
 * `PackageInstaller`, its signing certificate's SHA-256 must equal [SUITE_CERT_SHA256]; anything else
 * is refused and deleted, so a tampered or third-party APK can never be installed by this launcher —
 * even if it somehow reached the release feed with a matching checksum.
 *
 * Rotation = a launcher release that trusts both fingerprints for one cycle (add the old one to
 * [TRUSTED], ship, then remove it).
 */
object SigningCert {
    /** suite signing certificate, held offline by the suite's signing key holder */
    const val SUITE_CERT_SHA256 = "0dfe8516d9c7ca269a19be95c4e49ef87af576dbcbe815865f060127496c6043"

    /** Every fingerprint this launcher accepts. One entry outside a rotation cycle. */
    val TRUSTED: List<String> = listOf(SUITE_CERT_SHA256)

    /** SHA-256 of a DER-encoded certificate, lowercase hex. */
    fun fingerprint(certDer: ByteArray): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(certDer)
        val out = StringBuilder(digest.size * 2)
        for (b in digest) {
            val v = b.toInt() and 0xFF
            out.append("0123456789abcdef"[v ushr 4])
            out.append("0123456789abcdef"[v and 0x0F])
        }
        return out.toString()
    }

    /** True when [fingerprintHex] is one the suite trusts (case- and separator-insensitive). */
    fun isTrusted(fingerprintHex: String?): Boolean {
        val normalised = normalise(fingerprintHex ?: return false)
        return TRUSTED.any { normalise(it) == normalised }
    }

    /** Accepts "AA:BB:…" and "aabb…" alike. */
    fun normalise(hex: String): String =
        hex.filter { !it.isWhitespace() && it != ':' }.lowercase()
}
