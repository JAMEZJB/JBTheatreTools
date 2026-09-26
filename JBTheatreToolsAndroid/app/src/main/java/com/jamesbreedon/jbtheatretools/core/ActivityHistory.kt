package com.jamesbreedon.jbtheatretools.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import java.time.Instant
import java.time.LocalDateTime
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter
import java.util.Locale

/** One line of the install history: what happened to which app, and when. */
data class ActivityEvent(
    val at: Instant,
    val app: String,
    val name: String,
    /** install · update · downgrade · reinstall · uninstall · failed · cancelled */
    val action: String,
    val from: String? = null,
    val to: String? = null,
    val note: String? = null,
)

/**
 * The launcher's activity history (history.json in the app's files dir): a JSON array, oldest first, capped at
 * [CAP] entries — the same format and wording as the desktop launchers. Pure; the file IO lives in the repository.
 * A damaged file reads as empty history rather than failing a launch.
 */
object ActivityHistory {
    const val CAP = 300
    const val MAX_NOTE_LENGTH = 200

    private val stamp: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss'Z'").withZone(ZoneOffset.UTC)
    private val pretty = Json { prettyPrint = true }

    fun actionFor(from: String?, to: String): String = when {
        from.isNullOrEmpty() -> "install"
        VersionCompare.isNewer(to, from) -> "update"
        VersionCompare.isNewer(from, to) -> "downgrade"
        else -> "reinstall"
    }

    fun parse(json: String?): List<ActivityEvent> {
        if (json.isNullOrBlank()) return emptyList()
        val root = runCatching { Json.parseToJsonElement(json) }.getOrNull() as? JsonArray ?: return emptyList()
        return root.mapNotNull { e ->
            val o = e as? JsonObject ?: return@mapNotNull null
            val at = RelativeAge.parseIso(str(o, "at"))
            val app = str(o, "app")
            val action = str(o, "action")
            if (at == null || app.isNullOrEmpty() || action.isNullOrEmpty()) return@mapNotNull null
            ActivityEvent(at, app, str(o, "name") ?: app, action, str(o, "from"), str(o, "to"), str(o, "note"))
        }
    }

    /**
     * A history file that exists and has content but isn't a JSON list: damaged. The repository keeps it aside as
     * history.json.bad before writing a new one, rather than silently overwriting the old history.
     */
    fun isDamaged(json: String?): Boolean =
        !json.isNullOrBlank() && runCatching { Json.parseToJsonElement(json) }.getOrNull() !is JsonArray

    private fun str(o: JsonObject, name: String): String? =
        (o[name] as? JsonPrimitive)?.takeIf { it.isString }?.content

    fun serialize(events: List<ActivityEvent>): String = pretty.encodeToString(JsonElement.serializer(), buildJsonArray {
        for (e in events) add(buildJsonObject {
            put("at", stamp.format(e.at))
            put("app", e.app)
            put("name", e.name)
            put("action", e.action)
            e.from?.let { put("from", it) }
            e.to?.let { put("to", it) }
            e.note?.let { put("note", it) }
        })
    })

    /** Appends, trimming the note, and drops the oldest entries past the cap. */
    fun append(existing: List<ActivityEvent>, e: ActivityEvent): List<ActivityEvent> {
        var note = e.note?.trim()
        if (note != null && note.length > MAX_NOTE_LENGTH) note = note.substring(0, MAX_NOTE_LENGTH).trimEnd() + "…"
        val list = existing + e.copy(note = if (note.isNullOrEmpty()) null else note)
        return if (list.size > CAP) list.drop(list.size - CAP) else list
    }

    /** "Updated DMX Tools v1.1.0 → v1.2.0". */
    fun describe(e: ActivityEvent): String {
        fun v(t: String?) = if (t == null) "" else " " + VersionCompare.display(t)
        return when (e.action) {
            "install" -> "Installed ${e.name}${v(e.to)}"
            "update" -> "Updated ${e.name}${v(e.from)} →${v(e.to)}"
            "downgrade" -> "Rolled back ${e.name}${v(e.from)} →${v(e.to)}"
            "reinstall" -> "Reinstalled ${e.name}${v(e.to)}"
            "uninstall" -> "Removed ${e.name}${v(e.from)}"
            "failed" -> "Couldn't install ${e.name}${v(e.to)}" + (e.note?.let { ": $it" } ?: "")
            // The system's install dialog was cancelled: nothing went wrong.
            "cancelled" -> "Cancelled installing ${e.name}${v(e.to)}"
            else -> "${e.action} ${e.name}${v(e.to)}"
        }
    }

    private val time: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm", Locale.US)
    private val full: DateTimeFormatter = DateTimeFormatter.ofPattern("d MMM yyyy HH:mm", Locale.US)

    /** "Today 14:02", "Yesterday 09:10" or "12 Sep 2026 14:02" — both times already in local time. */
    fun `when`(atLocal: LocalDateTime, nowLocal: LocalDateTime): String {
        val t = time.format(atLocal)
        return when (atLocal.toLocalDate()) {
            nowLocal.toLocalDate() -> "Today $t"
            nowLocal.toLocalDate().minusDays(1) -> "Yesterday $t"
            else -> full.format(atLocal)
        }
    }
}
