package com.jamesbreedon.jbtheatretools.core

/** The status half of the find bar: which apps to show. */
enum class StatusFilter(val label: String) { ALL("All"), INSTALLED("Installed"), UPDATES("Updates"), NOT_INSTALLED("Not installed") }

/**
 * Find & filter for the app list — the same rules on every launcher. The query is split on whitespace and EVERY
 * word must appear (case-insensitively) somewhere in the app's name, blurb, category or id.
 */
object AppFilter {
    fun tokens(query: String?): List<String> =
        query.orEmpty().split(Regex("\\s+")).filter { it.isNotEmpty() }.map { it.lowercase() }

    fun matchesQuery(query: String?, vararg fields: String?): Boolean {
        val tokens = tokens(query)
        if (tokens.isEmpty()) return true
        val hay = fields.filterNotNull().filter { it.isNotEmpty() }.map { it.lowercase() }
        return tokens.all { t -> hay.any { it.contains(t) } }
    }

    fun matchesStatus(filter: StatusFilter, installed: Boolean, updateAvailable: Boolean, installable: Boolean): Boolean =
        when (filter) {
            StatusFilter.INSTALLED -> installed
            StatusFilter.UPDATES -> updateAvailable
            StatusFilter.NOT_INSTALLED -> !installed && installable
            StatusFilter.ALL -> true
        }

    fun isActive(query: String?, filter: StatusFilter): Boolean = tokens(query).isNotEmpty() || filter != StatusFilter.ALL
}
