package com.jamesbreedon.jbtheatretools.core

import java.time.Instant
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/**
 * The "Share diagnostics" report: everything useful for a support question, nothing secret — the same layout as
 * the desktop launchers. Credentials are never passed in, and every log line is redacted as a second line of
 * defence.
 */
object Diagnostics {
    const val LOG_LINES = 40

    data class AppLine(val name: String, val installed: String?, val latest: String?, val status: String, val held: Boolean)

    data class Info(
        val launcherVersion: String, val os: String, val arch: String, val authMode: String, val relayHost: String?,
        val devChannel: Boolean, val showLock: Boolean, val installLocation: String, val apps: List<AppLine>,
        val logTail: List<String>, val now: Instant,
    )

    private val tokens = Regex("gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}")
    private val schemeValues = Regex("""\b(Bearer|Basic|token)\s+[A-Za-z0-9+/=._\-]{16,}""", RegexOption.IGNORE_CASE)
    private val stamp: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss 'UTC'").withZone(ZoneOffset.UTC)

    fun redact(line: String): String = schemeValues.replace(tokens.replace(line, "[redacted]"), "$1 [redacted]")

    fun build(i: Info): String {
        val sb = StringBuilder()
        sb.append("JB Theatre Tools diagnostics — ").append(stamp.format(i.now)).append('\n')
        sb.append("Launcher: v").append(VersionCompare.norm(i.launcherVersion)).append('\n')
        sb.append("System: ").append(i.os).append(" (").append(i.arch).append(")\n")
        sb.append("Downloads via: ").append(i.authMode)
        if (!i.relayHost.isNullOrEmpty()) sb.append(" (").append(i.relayHost).append(')')
        sb.append('\n')
        sb.append("Install location: ").append(i.installLocation).append('\n')
        sb.append("Show lock: ").append(if (i.showLock) "on" else "off")
            .append(" · Development builds: ").append(if (i.devChannel) "on" else "off").append('\n')
        sb.append("\nApps (").append(i.apps.size).append("):\n")
        for (a in i.apps) {
            sb.append("  ").append(a.name).append(" — installed ").append(a.installed ?: "—")
                .append(", latest ").append(a.latest ?: "—").append(", ").append(a.status)
            if (a.held) sb.append(", held")
            sb.append('\n')
        }
        val tail = i.logTail.takeLast(LOG_LINES)
        sb.append("\nRecent log (").append(tail.size).append(" lines):\n")
        for (l in tail) sb.append("  ").append(redact(l)).append('\n')
        return sb.toString()
    }
}
