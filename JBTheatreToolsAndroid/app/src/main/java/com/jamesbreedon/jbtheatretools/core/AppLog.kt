package com.jamesbreedon.jbtheatretools.core

import android.content.Context
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * The launcher's log — `filesDir/logs/JBTheatreTools.log` (§5 rule 5/6), with a Share action in
 * About (there is no Finder to reveal to).
 *
 * It names the asset, the release tag and the verification result. It NEVER contains the passphrase
 * or the token: [redact] strips anything that looks like a credential before a line is written, so a
 * shared log can't leak one even if a caller is careless.
 */
class AppLog private constructor(private val file: File) {

    private val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.UK)
    private val lock = Any()

    fun log(message: String) {
        val line = "${stamp.format(Date())}  ${redact(message)}\n"
        synchronized(lock) {
            runCatching {
                file.parentFile?.mkdirs()
                if (file.length() > MAX_BYTES) rotate()
                file.appendText(line)
            }
        }
    }

    fun read(maxBytes: Int = 200_000): String = runCatching {
        if (!file.isFile) return@runCatching ""
        val text = file.readText()
        if (text.length <= maxBytes) text else text.takeLast(maxBytes)
    }.getOrDefault("")

    val path: File get() = file

    private fun rotate() {
        val old = File(file.parentFile, file.name + ".1")
        runCatching { if (old.exists()) old.delete(); file.renameTo(old) }
    }

    companion object {
        private const val MAX_BYTES = 512L * 1024

        @Volatile
        private var instance: AppLog? = null

        fun get(context: Context): AppLog = instance ?: synchronized(this) {
            instance ?: AppLog(File(File(context.filesDir, "logs"), "JBTheatreTools.log")).also {
                instance = it
            }
        }

        private val credentialish = Regex(
            "(?i)(bearer\\s+\\S+|basic\\s+\\S+|gh[pousr]_[A-Za-z0-9]{10,}|github_pat_[A-Za-z0-9_]{10,})"
        )

        /** Belt and braces: nothing credential-shaped ever reaches the file. */
        fun redact(message: String): String = credentialish.replace(message, "<redacted>")
    }
}
