package com.jamesbreedon.jbtheatretools.core

/**
 * How this device arranges the app list: pinned apps (in the order they were pinned), apps hidden from the list,
 * the saved app order, the saved section order and the collapsed sections. The same model as the desktop
 * launchers' list layout, and the same `layout` section of a setup file.
 */
data class LayoutPrefs(
    val pinned: List<String> = emptyList(),
    val hidden: Set<String> = emptySet(),
    val order: List<String> = emptyList(),
    val categoryOrder: List<String> = emptyList(),
    val collapsed: Set<String> = emptySet(),
)

/**
 * The list layout's rules — pure, so every case is unit-tested. Pinned apps float to the top in their own "Pinned"
 * section; every other app sits under its catalog category ("Other" when it has none), sections in the saved order,
 * then the catalog's, then any stray category A–Z. Reordering moves an app past its nearest neighbour in the SAME
 * section that's on screen; it's paused while a search or status filter narrows the list.
 */
object AppLayout {
    /** The Pinned section's key (the desktop launchers' Windows spelling; the macOS one is read too). */
    const val PINNED_KEY = "pinned"
    private const val MAC_PINNED_KEY = "\u0001pinned"
    const val UNCATEGORISED = "Other"

    data class Group(val key: String, val title: String, val ids: List<String>)

    fun categoryOf(category: String?): String = category?.trim()?.takeIf { it.isNotEmpty() } ?: UNCATEGORISED

    /** Every catalog id in the saved order: saved ids first (in saved order), then apps added since, in catalog order. */
    fun ordered(catalogIds: List<String>, savedOrder: List<String>): List<String> {
        val known = catalogIds.toSet()
        val out = LinkedHashSet<String>()
        for (id in savedOrder) if (id in known) out.add(id)
        out.addAll(catalogIds)
        return out.toList()
    }

    /** The effective section order: the saved order, then the catalog's, then any category left over A–Z. */
    fun categoryOrder(saved: List<String>, catalogCategories: List<String>, used: Collection<String>): List<String> {
        val out = LinkedHashSet<String>()
        out.addAll(saved)
        out.addAll(catalogCategories)
        out.addAll(used.toSortedSet())
        return out.toList()
    }

    /**
     * The sections in render order: Pinned first (when any pinned app is shown), then each non-empty category.
     * [shownIds] are the ids on screen, already in list order (filtered, hidden apps left out).
     */
    fun groups(
        shownIds: List<String>,
        categoryOf: (String) -> String,
        pinned: Collection<String>,
        savedCategoryOrder: List<String>,
        catalogCategories: List<String>,
    ): List<Group> {
        val pinnedSet = pinned.toSet()
        val out = ArrayList<Group>()
        val pins = shownIds.filter { it in pinnedSet }
        if (pins.isNotEmpty()) out.add(Group(PINNED_KEY, "Pinned", pins))
        val main = shownIds.filter { it !in pinnedSet }
        for (cat in categoryOrder(savedCategoryOrder, catalogCategories, main.map(categoryOf))) {
            val ids = main.filter { categoryOf(it) == cat }
            if (ids.isNotEmpty()) out.add(Group(cat, cat, ids))
        }
        return out
    }

    fun groupKey(id: String, pinned: Collection<String>, categoryOf: (String) -> String): String =
        if (id in pinned) PINNED_KEY else categoryOf(id)

    /**
     * [order] with [id] swapped past its nearest neighbour [up]/down that is [visible] and in the same section; null
     * when there's none (the edge of its section) or the id isn't listed.
     */
    fun move(order: List<String>, id: String, up: Boolean, visible: Set<String>, groupKeyOf: (String) -> String): List<String>? {
        val i = order.indexOf(id)
        if (i < 0) return null
        val key = groupKeyOf(id)
        val range = if (up) (i - 1 downTo 0) else (i + 1 until order.size)
        val j = range.firstOrNull { order[it] in visible && groupKeyOf(order[it]) == key } ?: return null
        return order.toMutableList().apply { this[i] = order[j]; this[j] = id }
    }

    /** Moves the category section [key] one place [up]/down in [displayed] (the sections on screen, Pinned excluded). */
    fun moveSection(displayed: List<String>, key: String, up: Boolean): List<String>? {
        if (key == PINNED_KEY) return null
        val order = displayed.filter { it != PINNED_KEY }
        val i = order.indexOf(key)
        val j = if (up) i - 1 else i + 1
        if (i < 0 || j < 0 || j >= order.size) return null
        return order.toMutableList().apply { this[i] = order[j]; this[j] = key }
    }

    /** "3 of 24 apps" while a search or filter narrows the list, else "24 apps" — the desktop wording. */
    fun countLabel(shown: Int, total: Int, filtering: Boolean): String {
        val noun = if (total == 1) "app" else "apps"
        return if (filtering) "$shown of $total $noun" else "$total $noun"
    }

    /** Pins / unpins [id]. The Pinned section lists its apps in the saved app order, like every other section. */
    fun togglePin(prefs: LayoutPrefs, id: String): LayoutPrefs =
        if (id in prefs.pinned) prefs.copy(pinned = prefs.pinned - id) else prefs.copy(pinned = prefs.pinned + id)

    /** A setup file's layout, kept to the apps this launcher knows (sections are kept as named). */
    fun fromProfile(layout: SetupProfile.Layout, known: Set<String>): LayoutPrefs = LayoutPrefs(
        pinned = layout.pinned.filter { it in known }.distinct(),
        hidden = layout.hidden.filter { it in known }.toSet(),
        order = layout.order.filter { it in known }.distinct(),
        categoryOrder = layout.categoryOrder.distinct(),
        collapsed = layout.collapsed.map { if (it == MAC_PINNED_KEY) PINNED_KEY else it }.toSet(),
    )

    /** The layout a setup file saves: every app in list order and every section in section order. */
    fun toProfile(prefs: LayoutPrefs, catalogIds: List<String>, catalogCategories: List<String>, usedCategories: Collection<String>): SetupProfile.Layout =
        SetupProfile.Layout(
            pinned = prefs.pinned.filter { it in catalogIds },
            hidden = catalogIds.filter { it in prefs.hidden },
            order = ordered(catalogIds, prefs.order),
            categoryOrder = categoryOrder(prefs.categoryOrder, catalogCategories, usedCategories),
            collapsed = prefs.collapsed.sorted(),
        )
}
