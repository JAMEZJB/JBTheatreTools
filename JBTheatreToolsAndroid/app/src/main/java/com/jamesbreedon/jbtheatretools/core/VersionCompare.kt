package com.jamesbreedon.jbtheatretools.core

/**
 * Lenient, overflow-safe version-string comparison — the Kotlin twin of the Windows launcher's
 * `VersionCompare` and the macOS launcher's comparator.
 *
 * Tolerates a leading "v" and differing component counts, and parses each numeric segment into a
 * [Long] with saturation, so a long numeric tag (a date tag such as Convert's `build-20260912`)
 * can't wrap and compare as OLDER than the installed build — which would silently hide an update.
 */
object VersionCompare {
    fun norm(s: String): String {
        val t = s.trim()
        return if (t.startsWith("v") || t.startsWith("V")) t.substring(1) else t
    }

    fun equal(a: String, b: String): Boolean = norm(a) == norm(b)

    /** True if `a` is a strictly newer version than `b` (component-wise numeric compare). */
    fun isNewer(a: String, b: String): Boolean {
        val pa = parts(a)
        val pb = parts(b)
        for (i in 0 until maxOf(pa.size, pb.size)) {
            val x = pa.getOrElse(i) { 0L }
            val y = pb.getOrElse(i) { 0L }
            if (x != y) return x > y
        }
        return false
    }

    private fun parts(s: String): List<Long> = norm(s).split('.').map { segment ->
        // First contiguous digit run in the segment: skip leading non-digits, take the digits, stop at
        // the next non-digit. Handles date-style tags ("build-20260912" -> 20260912) while staying
        // identical for ordinary semver segments ("2", "0-rc1" -> 0).
        var n = 0L
        var started = false
        for (c in segment) {
            if (c.isDigit()) {
                started = true
                n = if (n > (Long.MAX_VALUE - 9) / 10) Long.MAX_VALUE else n * 10 + (c - '0')
            } else if (started) break
        }
        n
    }
}
