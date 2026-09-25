package com.jamesbreedon.jbtheatretools.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneOffset
import java.time.format.DateTimeFormatter

/**
 * A machine's setup — which apps are installed at which versions, which are held, and optionally the desktop
 * list layout — saved to a file and replayed on another device. The same format as the desktop launchers
 * (schema 1): `{"kind":"jbtheatretools-setup","schemaVersion":1,"createdAt","createdBy","apps":[{"id","variant"?,
 * "version","held"?}],"layout":{...}}`. Parsing is strict about shape and size.
 */
data class SetupProfile(
    val createdAt: String = "",
    val createdBy: String = "",
    val apps: List<Entry> = emptyList(),
    val layout: Layout? = null,
) {
    data class Entry(val id: String, val variant: String?, val version: String?, val held: Boolean)

    data class Layout(
        val pinned: List<String>, val hidden: List<String>, val order: List<String>,
        val categoryOrder: List<String>, val collapsed: List<String>,
    )

    class FormatError(message: String) : Exception(message)

    companion object {
        const val KIND = "jbtheatretools-setup"
        const val SCHEMA_VERSION = 1
        const val MAX_BYTES = 1_000_000
        const val MAX_ENTRIES = 500
        private val pretty = Json { prettyPrint = true }

        fun parse(json: String): SetupProfile {
            if (json.toByteArray(Charsets.UTF_8).size > MAX_BYTES) throw FormatError("This file is too large to be a setup file.")
            val root = runCatching { Json.parseToJsonElement(json) }.getOrNull() as? JsonObject
                ?: throw FormatError("This file isn't a JB Theatre Tools setup file.")
            if (str(root, "kind") != KIND) throw FormatError("This file isn't a JB Theatre Tools setup file.")
            val schema = (root["schemaVersion"] as? JsonPrimitive)?.takeIf { !it.isString }?.intOrNull
            if (schema == null || schema < 1) throw FormatError("This setup file has no valid schemaVersion.")
            if (schema > SCHEMA_VERSION) throw FormatError("This setup file was made by a newer JB Theatre Tools — update the launcher first.")
            val apps = root["apps"] as? JsonArray ?: throw FormatError("This setup file lists no apps.")
            val entries = ArrayList<Entry>()
            for (a in apps) {
                if (entries.size >= MAX_ENTRIES) break
                val o = a as? JsonObject ?: continue
                val id = str(o, "id")?.trim()
                if (id.isNullOrEmpty()) continue
                val variant = str(o, "variant")?.trim()
                val version = str(o, "version")?.trim()
                val held = (o["held"] as? JsonPrimitive)?.takeIf { !it.isString }?.booleanOrNull == true
                entries.add(Entry(id, variant?.ifEmpty { null }, version?.ifEmpty { null }, held))
            }
            val l = root["layout"] as? JsonObject
            val layout = l?.let {
                Layout(ids(it, "pinned"), ids(it, "hidden"), ids(it, "order"), ids(it, "categoryOrder"), ids(it, "collapsed"))
            }
            return SetupProfile(str(root, "createdAt") ?: "", str(root, "createdBy") ?: "", entries, layout)
        }

        private fun str(o: JsonObject, name: String): String? =
            (o[name] as? JsonPrimitive)?.takeIf { it.isString }?.content

        private fun ids(o: JsonObject, name: String): List<String> {
            val arr = o[name] as? JsonArray ?: return emptyList()
            return arr.mapNotNull { (it as? JsonPrimitive)?.takeIf { p -> p.isString }?.content }
                .filter { it.isNotBlank() }.take(MAX_ENTRIES)
        }

        private val utc: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyy-MM-dd'T'HH:mm:ss'Z'").withZone(ZoneOffset.UTC)

        fun timestamp(now: Instant): String = utc.format(now)

        /** A suggested file name: "JB Theatre Tools setup 2026-09-25.json". */
        fun suggestedFileName(today: LocalDate): String = "JB Theatre Tools setup $today.json"
    }

    fun serialize(): String = pretty.encodeToString(JsonElement.serializer(), buildJsonObject {
        put("kind", KIND)
        put("schemaVersion", SCHEMA_VERSION)
        put("createdAt", createdAt)
        put("createdBy", createdBy)
        put("apps", buildJsonArray {
            for (e in apps) add(buildJsonObject {
                put("id", e.id)
                e.variant?.let { put("variant", it) }
                e.version?.let { put("version", it) }
                if (e.held) put("held", true)
            })
        })
        layout?.let { l ->
            put("layout", buildJsonObject {
                putJsonArray("pinned") { l.pinned.forEach { add(JsonPrimitive(it)) } }
                putJsonArray("hidden") { l.hidden.forEach { add(JsonPrimitive(it)) } }
                putJsonArray("order") { l.order.forEach { add(JsonPrimitive(it)) } }
                putJsonArray("categoryOrder") { l.categoryOrder.forEach { add(JsonPrimitive(it)) } }
                putJsonArray("collapsed") { l.collapsed.forEach { add(JsonPrimitive(it)) } }
            })
        }
    })
}

/** What importing a setup file would do on this device — computed before anything installs (the preview). */
object SetupPlanner {
    data class CatalogEntry(val id: String, val name: String, val variants: List<Pair<String, String>>)

    /** One slot to install. [variantId] null = the default edition; [tag] null = the latest release. */
    data class Install(val appId: String, val variantId: String?, val tag: String?, val label: String)

    data class Plan(
        val toInstall: List<Install>, val alreadyInstalled: List<String>,
        val skipped: List<String>, val holdIds: List<String>,
    )

    fun build(
        profile: SetupProfile,
        catalog: List<CatalogEntry>,
        installedKeys: Set<String>,
        supportsVariants: Boolean = true,
    ): Plan {
        val byId = catalog.associateBy { it.id }
        val toInstall = ArrayList<Install>()
        val already = ArrayList<String>()
        val skipped = ArrayList<String>()
        val hold = ArrayList<String>()
        val seenKeys = HashSet<String>()
        for (e in profile.apps) {
            val app = byId[e.id]
            if (app == null) { skipped.add("${e.id} — not in this launcher's catalog"); continue }
            var variant = e.variant
            var variantLabel: String? = null
            if (variant != null) {
                val idx = app.variants.indexOfFirst { it.first == variant }
                if (idx < 0) { skipped.add("${app.name} ($variant) — no such edition"); continue }
                if (idx == 0) variant = null
                else if (!supportsVariants) { skipped.add("${app.name} (${app.variants[idx].second}) — not available here"); continue }
                else variantLabel = app.variants[idx].second
            }
            val key = if (variant == null) app.id else "${app.id}@$variant"
            if (!seenKeys.add(key)) continue
            val label = if (variantLabel == null) app.name else "${app.name} ($variantLabel)"
            if (e.held && app.id !in hold) hold.add(app.id)
            if (key in installedKeys) { already.add(label); continue }
            toInstall.add(Install(app.id, variant, if (e.held) e.version else null, label))
        }
        return Plan(toInstall, already, skipped, hold)
    }

    /** The preview text shown before an import runs. */
    fun summary(plan: Plan): String {
        val sb = StringBuilder()
        if (plan.toInstall.isEmpty()) sb.append("Nothing to install — this machine already has every app in the file.")
        else {
            sb.append("Install ${plan.toInstall.size} app${if (plan.toInstall.size == 1) "" else "s"}:")
            for (i in plan.toInstall)
                sb.append("\n  • ").append(i.label).append(if (i.tag != null) " ${VersionCompare.display(i.tag)} (held)" else "")
        }
        if (plan.alreadyInstalled.isNotEmpty())
            sb.append("\n\nAlready installed (left as they are): ${plan.alreadyInstalled.joinToString(", ")}")
        if (plan.holdIds.isNotEmpty())
            sb.append("\n\nHeld at their versions: ${plan.holdIds.size} app${if (plan.holdIds.size == 1) "" else "s"}")
        if (plan.skipped.isNotEmpty())
            sb.append("\n\nSkipped:\n  • ").append(plan.skipped.joinToString("\n  • "))
        return sb.toString()
    }
}
