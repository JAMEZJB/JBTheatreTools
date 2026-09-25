package com.jamesbreedon.jbtheatretools.core

import java.time.Duration
import java.time.Instant

/**
 * The rules behind the launcher's unattended behaviour — scheduled checks and update notifications — kept pure
 * so they're unit-tested and identical on every launcher.
 */
object UpdatePolicy {
    /** The "While open, check every" choices (settings value to label). The first is the default. */
    val intervals: List<Pair<String, String>> = listOf(
        "4h" to "4 hours", "1h" to "Hour", "12h" to "12 hours", "24h" to "Day", "off" to "Off",
    )
    const val DEFAULT_INTERVAL = "4h"

    /** The check interval for a settings value; null = scheduled checks off. Unknown -> the default. */
    fun interval(raw: String?): Duration? = when (raw ?: DEFAULT_INTERVAL) {
        "off" -> null
        "1h" -> Duration.ofHours(1)
        "12h" -> Duration.ofHours(12)
        "24h" -> Duration.ofHours(24)
        else -> Duration.ofHours(4)
    }

    /**
     * True when a scheduled check should run now: scheduled checks are on and either nothing has been checked yet
     * or the interval has passed (a clock that jumped backwards counts as due).
     */
    fun isDue(lastCheck: Instant?, now: Instant, raw: String?): Boolean {
        val interval = interval(raw) ?: return false
        if (lastCheck == null) return true
        val elapsed = Duration.between(lastCheck, now)
        return elapsed.isNegative || elapsed >= interval
    }

    /** One app with an update on offer (not held). */
    data class Pending(val id: String, val name: String, val version: String) {
        val key: String get() = "$id ${VersionCompare.norm(version)}"
    }

    /**
     * Which pending updates are NEW (not notified before), and the notified set to store: the keys of everything
     * currently pending — so the set never grows beyond what's on offer.
     */
    fun notify(pending: List<Pending>, alreadyNotified: Collection<String>): Pair<List<Pending>, List<String>> {
        val seen = alreadyNotified.toSet()
        return pending.filter { it.key !in seen } to pending.map { it.key }.distinct()
    }

    /**
     * The keys to remember after a check: what's pending now, plus the earlier keys of apps whose check didn't
     * complete this time — so one check that couldn't reach the feed doesn't make the next one announce the
     * same update again.
     */
    fun remembered(notified: List<String>, alreadyNotified: Collection<String>, uncheckedIds: Set<String>): List<String> =
        (notified + alreadyNotified.filter { it.substringBefore(' ') in uncheckedIds }).distinct()

    fun notificationTitle(count: Int): String = if (count == 1) "Update available" else "Updates available"

    /** "DMX Tools v1.2.0, PSN Tools v0.4.1 and 2 more". */
    fun notificationBody(items: List<Pending>): String {
        val body = items.take(3).joinToString(", ") { "${it.name} ${VersionCompare.display(it.version)}" }
        return if (items.size > 3) "$body and ${items.size - 3} more" else body
    }

    /** "Updated DMX Tools to v1.2.0" / "Updated 3 apps". */
    fun autoUpdateSummary(updated: List<Pair<String, String>>): String =
        if (updated.size == 1) "Updated ${updated[0].first} to ${VersionCompare.display(updated[0].second)}"
        else "Updated ${updated.size} apps"
}

/** When to show the launcher's own "what's new" after it has been updated. */
object LauncherWhatsNew {
    /**
     * True on the first launch of a version newer than the last one seen. With nothing seen yet it's an update only
     * when the launcher was already installed before (versions before 1.30 didn't record what they were) — a fresh
     * install shows nothing.
     */
    fun shouldShow(lastSeen: String?, current: String, existingInstall: Boolean = false): Boolean =
        if (lastSeen.isNullOrBlank()) existingInstall else VersionCompare.isNewer(current, lastSeen)
}

/**
 * The pre-download disk-space check: an archive is budgeted at 3x its size (download + extracted copy), a
 * single file at 2x, plus a 50 MB margin.
 */
object DiskSpace {
    const val MARGIN = 50_000_000L

    fun isArchive(assetName: String): Boolean = assetName.endsWith(".zip", ignoreCase = true)

    fun required(assetSize: Long, assetName: String): Long {
        if (assetSize <= 0) return MARGIN
        val factor = if (isArchive(assetName)) 3L else 2L
        return if (assetSize > (Long.MAX_VALUE - MARGIN) / factor) Long.MAX_VALUE else assetSize * factor + MARGIN
    }

    /** The refusal message, or null when there's room. A negative [free] means "couldn't tell": never block. */
    fun shortfall(required: Long, free: Long): String? =
        if (free < 0 || free >= required) null
        else "Not enough disk space — needs about ${ByteSize.format(required)}, ${ByteSize.format(free)} free."
}
