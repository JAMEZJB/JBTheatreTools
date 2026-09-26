package com.jamesbreedon.jbtheatretools.core

import java.time.Duration
import java.time.Instant
import java.time.OffsetDateTime

/**
 * "3 days ago" for a release date — identical wording on every launcher. Counts whole elapsed days (24-hour
 * periods); a date in the future (clock skew) reads as "today".
 */
object RelativeAge {
    fun describe(date: Instant, now: Instant): String {
        val millis = Duration.between(date, now).toMillis()
        val days = if (millis <= 0) 0L else millis / 86_400_000L
        return when {
            days < 1 -> "today"
            days == 1L -> "yesterday"
            days < 7 -> "$days days ago"
            days < 30 -> (days / 7).let { if (it == 1L) "1 week ago" else "$it weeks ago" }
            days < 365 -> maxOf(1L, days / 30).let { if (it == 1L) "1 month ago" else "$it months ago" }
            else -> (days / 365).let { if (it == 1L) "1 year ago" else "$it years ago" }
        }
    }

    /** Parses GitHub's `published_at` (ISO 8601); null when absent or malformed. */
    fun parseIso(iso: String?): Instant? {
        if (iso.isNullOrBlank()) return null
        return runCatching { OffsetDateTime.parse(iso.trim()).toInstant() }.getOrNull()
    }
}
