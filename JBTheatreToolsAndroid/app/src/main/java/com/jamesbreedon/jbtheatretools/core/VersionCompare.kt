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

    /** True if `a` is a strictly newer version than `b`. */
    fun isNewer(a: String, b: String): Boolean = compare(a, b) > 0

    /**
     * Semver-aware ordering. The numeric core compares component-wise (missing parts = 0); a pre-release
     * (`1.2.0-dev.3`, `1.2.0-rc1`) sorts BEFORE its release (`1.2.0`), and pre-release identifiers compare
     * semver-style (numbers numerically, numbers before words, a longer list after its prefix) — so
     * `0.1.0-dev.1 < 0.1.0-dev.2 < 0.1.0`. A tag that doesn't start with a digit (Convert's
     * `build-20260912`) keeps the lenient digit-run parse and never counts as a pre-release.
     */
    fun compare(a: String, b: String): Int {
        val (ca, pa) = split(a)
        val (cb, pb) = split(b)
        val x = parts(ca)
        val y = parts(cb)
        for (i in 0 until maxOf(x.size, y.size)) {
            val u = x.getOrElse(i) { 0L }
            val v = y.getOrElse(i) { 0L }
            if (u != v) return u.compareTo(v)
        }
        if (pa == null && pb == null) return 0
        if (pa == null) return 1
        if (pb == null) return -1
        for (i in 0 until minOf(pa.size, pb.size)) {
            val c = compareIdentifier(pa[i], pb[i])
            if (c != 0) return c
        }
        return pa.size.compareTo(pb.size)
    }

    /** True for development pre-releases (`vX.Y.Z-dev.N`). */
    fun isDev(tag: String): Boolean = norm(tag).lowercase().contains("-dev.")

    /** The version without its pre-release suffix — `v0.1.0-dev.2` -> `0.1.0` (an APK's own versionName). */
    fun core(tag: String): String = split(tag).first

    private fun split(s: String): Pair<String, List<String>?> {
        val n = norm(s)
        val dash = n.indexOf('-')
        if (n.isEmpty() || !n[0].isDigit() || dash < 0) return n to null
        return n.substring(0, dash) to n.substring(dash + 1).split('.')
    }

    private fun compareIdentifier(a: String, b: String): Int {
        val na = a.toLongOrNull()
        val nb = b.toLongOrNull()
        return when {
            na != null && nb != null -> na.compareTo(nb)
            na != null -> -1
            nb != null -> 1
            else -> a.compareTo(b)
        }
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

