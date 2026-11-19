package com.jamesbreedon.jbtheatretools.core

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.Executors

/**
 * The launcher's log: `filesDir/logs/JBTheatreTools.log`, with a Share action in About (there is no file manager to
 * reveal it in). The suite's verbose logging rule, in one place:
 *
 *     2026-11-19 14:03:22.417  DEBUG  [github] GET host/repos/…/releases → 200, 4,210 bytes, 318 ms
 *
 * INFO / WARN / ERROR are always written. DEBUG is written only while "Verbose logging (for troubleshooting)" is on
 * (About, key `verbose`). Nothing credential-shaped reaches the file: call sites don't pass secrets, and [redact] masks
 * known shapes, and any credential registered with [registerSecret], as a second line of defence. Writes are queued
 * onto one background thread, so logging never slows a screen; an unwritable folder means no log, never a crash.
 */
class AppLog internal constructor(private val file: File, private val verboseSource: () -> Boolean = { false }) {

    private val lock = Any()
    private val secrets = mutableListOf<String>()
    private val writer = Executors.newSingleThreadExecutor { r -> Thread(r, "jbtt-log").apply { isDaemon = true } }

    @Volatile private var verboseCache = false
    @Volatile private var verboseCheckedAt = 0L

    /** Verbose logging on. The saved setting is re-read at most every two seconds. */
    val verbose: Boolean
        get() {
            val now = System.currentTimeMillis()
            if (now - verboseCheckedAt > 2_000) {
                verboseCache = runCatching { verboseSource() }.getOrDefault(verboseCache)
                verboseCheckedAt = now
            }
            return verboseCache
        }

    /** About: the switch just changed. Takes effect at once and is recorded in the log. */
    fun noteVerboseChanged(on: Boolean) {
        val was = verboseCache
        verboseCache = on
        verboseCheckedAt = System.currentTimeMillis()
        if (was != on) info("settings", "verbose logging ${if (was) "on" else "off"} → ${if (on) "on" else "off"}")
    }

    fun debug(area: String, message: () -> String) { if (verbose) write("DEBUG", area, message()) }
    fun debug(area: String, message: String) { if (verbose) write("DEBUG", area, message) }
    fun info(area: String, message: String) = write("INFO ", area, message)
    fun warn(area: String, message: String) = write("WARN ", area, message)

    /** One plain line saying what failed and why, then the technical detail below it. */
    fun error(area: String, message: String, t: Throwable? = null) {
        val text = if (t == null) message else
            message + "\n    ${t.javaClass.simpleName}: ${t.message}" +
                t.stackTrace.take(12).joinToString("") { "\n        at $it" }
        write("ERROR", area, text)
    }

    /** Existing free-text lines ("install x FAILED: …"): INFO (a warning when it says it failed), area from the line. */
    fun log(message: String) {
        val lower = message.lowercase(Locale.ROOT)
        val level = if ("failed" in lower || "couldn't" in lower || "can't" in lower || "refused" in lower) "WARN " else "INFO "
        write(level, areaOf(message), message)
    }

    private fun write(level: String, area: String, message: String) {
        val whenMs = System.currentTimeMillis()
        val body = redact(message)   // masked and cut on the caller's thread: no secret ever sits in the queue
        runCatching {
            writer.execute {
                runCatching {
                    val stamp = stampFormat().format(Date(whenMs))
                    val lines = body.replace("\r\n", "\n").split('\n')
                    val out = StringBuilder()
                    lines.forEachIndexed { i, raw ->
                        val line = cut(raw)
                        if (i == 0) out.append(stamp).append("  ").append(level).append("  [").append(area).append("] ").append(line)
                        else out.append(line)
                        out.append('\n')
                    }
                    synchronized(lock) {
                        file.parentFile?.mkdirs()
                        rotateIfNeeded(out.length)
                        file.appendText(out.toString())
                    }
                }
            }
        }
    }

    /** Rolls at 1 MB (5 MB with verbose on) and keeps 2 older files (3 with verbose on); `.1` is the newest. */
    private fun rotateIfNeeded(adding: Int) {
        val verboseNow = verboseCache
        val limit = if (verboseNow) VERBOSE_LIMIT else NORMAL_LIMIT
        val keep = if (verboseNow) VERBOSE_KEEP else NORMAL_KEEP
        if (!file.exists() || file.length() + adding <= limit) return
        File(file.parentFile, "${file.name}.$keep").delete()
        for (n in keep - 1 downTo 1) {
            val f = File(file.parentFile, "${file.name}.$n")
            if (f.exists()) f.renameTo(File(file.parentFile, "${file.name}.${n + 1}"))
        }
        file.renameTo(File(file.parentFile, "${file.name}.1"))
        var n = keep + 1
        while (File(file.parentFile, "${file.name}.$n").delete()) n++
    }

    /** Waits until every queued line is on disk (tests, and before the log is shared). */
    fun flush() {
        runCatching { writer.submit {}.get(5, java.util.concurrent.TimeUnit.SECONDS) }
    }

    /** The last lines of the log (at most [maxBytes] of it), masked. Waits for pending writes. */
    fun read(maxBytes: Int = 200_000): String {
        flush()
        return runCatching {
            if (!file.isFile) return@runCatching ""
            val text = file.readText()
            redact(if (text.length <= maxBytes) text else text.takeLast(maxBytes))
        }.getOrDefault("")
    }

    /** The last [count] lines (at most 400), optionally only those holding [filter]; masked. */
    fun tail(count: Int, filter: String? = null): List<String> {
        flush()
        val lines = runCatching { file.readText().takeLast(400_000).split('\n').filter { it.isNotEmpty() } }.getOrDefault(emptyList())
        val kept = if (filter.isNullOrBlank()) lines else lines.filter { it.contains(filter.trim(), ignoreCase = true) }
        return kept.takeLast(count.coerceIn(0, 400)).map { redact(it) }
    }

    val path: File get() = file

    /** Always written, verbose or not: what is running and the settings that explain its behaviour (never a secret). */
    fun writeHeader(version: String, settings: List<Pair<String, String>>) {
        info("start", "==== JB Theatre Tools $version (Android) ====")
        info("start", "system: Android ${android.os.Build.VERSION.RELEASE} (API ${android.os.Build.VERSION.SDK_INT}), ${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}, ${android.os.Build.SUPPORTED_ABIS.firstOrNull() ?: "unknown processor"}")
        info("start", "running as: the built app (${file.parentFile?.absolutePath ?: "?"})")
        settings.forEach { (k, v) -> info("start", "setting $k: $v") }
        info("start", "verbose logging: " + if (verbose) "ON (every step is recorded)" else "off (switch it on in About to trace a problem)")
    }

    /** Tells the log a credential the app now holds, so any exact copy of it is masked wherever it turns up. */
    fun registerSecret(value: String?) {
        if (value == null || value.length < 6) return
        synchronized(secrets) {
            if (value !in secrets) secrets += value
            val b64 = java.util.Base64.getEncoder().encodeToString(value.toByteArray(Charsets.UTF_8))
            if (b64 !in secrets) secrets += b64
        }
    }

    fun redact(message: String): String {
        var s = message
        val known = synchronized(secrets) { secrets.toList() }
        for (secret in known) s = s.replace(secret, "<redacted>")
        return redactShapes(s)
    }

    companion object {
        const val MAX_LINE = 2000
        const val NORMAL_LIMIT = 1_000_000L
        const val NORMAL_KEEP = 2
        const val VERBOSE_LIMIT = 5_000_000L
        const val VERBOSE_KEEP = 3

        @Volatile
        private var instance: AppLog? = null

        /** The log in use (set by [get]), for code that has no Context to hand, such as the release-feed client. */
        val current: AppLog? get() = instance

        fun get(context: Context): AppLog = instance ?: synchronized(this) {
            instance ?: AppLog(File(File(context.filesDir, "logs"), "JBTheatreTools.log")) {
                Settings(context.applicationContext).verbose
            }.also { instance = it }
        }

        private val stamp = ThreadLocal<SimpleDateFormat>()
        private fun stampFormat(): SimpleDateFormat =
            stamp.get() ?: SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.UK).also { stamp.set(it) }

        fun cut(line: String): String = if (line.length > MAX_LINE) line.take(MAX_LINE - 1) + "…" else line

        /** "web trigger: x" → "web-trigger"; else the first word. */
        fun areaOf(message: String): String {
            if (message.startsWith("[")) message.indexOf(']').takeIf { it > 0 }?.let { return message.substring(1, it) }
            val colon = message.indexOf(':')
            if (colon in 1..24) {
                val head = message.substring(0, colon)
                if ('/' !in head && '\\' !in head && head.split(' ').filter { it.isNotEmpty() }.size <= 2) {
                    return head.lowercase(Locale.ROOT).replace(' ', '-')
                }
            }
            val first = message.trimStart().split(' ').firstOrNull().orEmpty().lowercase(Locale.ROOT)
            return first.filter { it.isLetter() || it == '-' }.ifEmpty { "app" }
        }

        private val shapes = listOf(
            Regex("gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}") to "<redacted>",
            Regex("\\b(Bearer|Basic|token)\\s+[A-Za-z0-9+/=._\\-]{8,}", RegexOption.IGNORE_CASE) to "$1 <redacted>",
            Regex("\\b(authorization)\\s*[:=]\\s*[^\\s,;]+(\\s+[^\\s,;]+)?", RegexOption.IGNORE_CASE) to "$1: <redacted>",
            Regex("([?&\"']?\\b(?:access_?token|token|passphrase|password|passwd|pass|secret|api_?key|key)\\b[\"']?\\s*[=:]\\s*)(?:\"[^\"]*\"|'[^']*'|[^\\s&,;\"']+)", RegexOption.IGNORE_CASE) to "$1<redacted>",
            Regex("(X-Amz-Signature|X-Amz-Credential|X-Amz-Security-Token|sig|signature)=[^&\\s]+", RegexOption.IGNORE_CASE) to "$1=<redacted>",
        )

        /** Belt and braces: nothing credential-shaped ever reaches the file. */
        fun redactShapes(message: String): String =
            shapes.fold(message) { acc, (re, template) -> re.replace(acc, template) }

        /** Kept for existing callers. */
        fun redact(message: String): String = redactShapes(message)

        /** "host:port/path" of a URL: the query (which can carry a signature) dropped. */
        fun hostPath(url: java.net.URL?): String =
            if (url == null) "(no address)" else url.host + (if (url.port != -1) ":${url.port}" else "") + url.path
    }
}
