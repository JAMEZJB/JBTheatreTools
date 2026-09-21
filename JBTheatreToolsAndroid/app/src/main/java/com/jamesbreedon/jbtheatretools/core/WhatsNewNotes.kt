package com.jamesbreedon.jbtheatretools.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * The suite's "New in vX.Y.Z:" lines, served by the download relay so they can be edited on the server
 * without shipping a launcher release (the desktop launchers' v1.26.0 overlay). The bundled catalog's
 * lines are the FALLBACK: the launcher fetches this document alongside every update check
 * (server/passphrase mode only), overlays it on the catalog, and keeps the last good copy on disk so
 * an offline start still shows the latest lines it saw.
 *
 * Document (`<relay origin>/notes/whats-new.json`, same passphrase auth as the release endpoints):
 *
 *     { "schemaVersion": 1,
 *       "apps": { "<catalog app id>": { "whatsNew": "one line", "whatsNewVersion": "v1.2.3" }, … } }
 *
 * Rules: unknown ids are ignored; a missing key means "keep the bundled line"; an EMPTY `whatsNew`
 * hides the row's line; text is trimmed, whitespace-collapsed, control characters stripped and
 * length-capped. The launcher never interprets the text beyond displaying it.
 */
data class WhatsNewNotes(val notes: Map<String, Note> = emptyMap()) {
    data class Note(val whatsNew: String, val whatsNewVersion: String?)

    val isEmpty: Boolean get() = notes.isEmpty()

    operator fun get(id: String): Note? = notes[id]

    /** The line to show for an app: the relay's if it has one (empty = hide), else the catalog's. */
    fun resolved(app: CatalogApp): Pair<String?, String?> {
        val n = notes[app.id] ?: return app.whatsNew to app.whatsNewVersion
        return (if (n.whatsNew.isEmpty()) null else n.whatsNew) to n.whatsNewVersion
    }

    companion object {
        const val SCHEMA_VERSION = 1
        const val MAX_LINE_LENGTH = 160
        const val MAX_VERSION_LENGTH = 24

        class ParseException(message: String) : Exception(message)

        /**
         * Parses the relay document. Throws on anything that isn't the documented shape (so a stray
         * HTML error page or a redirect body can never clobber the bundled lines).
         */
        fun parse(bytes: ByteArray): WhatsNewNotes {
            val root = try {
                Json.parseToJsonElement(String(bytes, Charsets.UTF_8)) as? JsonObject
                    ?: throw ParseException("not an object")
            } catch (e: ParseException) {
                throw e
            } catch (e: Exception) {
                throw ParseException("not JSON")
            }
            val schema = root["schemaVersion"]?.jsonPrimitive?.content?.toIntOrNull() ?: SCHEMA_VERSION
            if (schema > SCHEMA_VERSION) throw ParseException("newer schema $schema")
            val apps = (root["apps"] as? JsonObject) ?: throw ParseException("no apps object")
            val out = LinkedHashMap<String, Note>()
            for ((id, raw) in apps) {
                val obj = raw as? JsonObject ?: continue
                val line = obj["whatsNew"]?.jsonPrimitive?.content ?: continue
                val version = obj["whatsNewVersion"]?.jsonPrimitive?.content
                    ?.let { clean(it, MAX_VERSION_LENGTH) }
                out[id] = Note(clean(line, MAX_LINE_LENGTH) ?: "", version)
            }
            return WhatsNewNotes(out)
        }

        /** Trim, strip control characters, collapse whitespace runs, cap the length. */
        fun clean(s: String, max: Int): String? {
            val stripped = s.filter { it.code >= 0x20 && it.code != 0x7F }
            val collapsed = stripped.split(Regex("\\s+")).filter { it.isNotEmpty() }.joinToString(" ")
            if (collapsed.isEmpty()) return null
            return if (collapsed.length > max) collapsed.take(max).trimEnd() + "…" else collapsed
        }
    }
}
