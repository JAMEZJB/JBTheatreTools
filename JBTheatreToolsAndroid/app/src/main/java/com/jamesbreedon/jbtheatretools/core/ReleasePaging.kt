package com.jamesbreedon.jbtheatretools.core

/**
 * How far the releases list is read (the same rule as the Mac and Windows launchers). GitHub lists releases newest
 * first, a page at a time. One page is nearly always enough; but an app with a long run of development builds since
 * its last full release would have that release fall off the first page, and a launcher that isn't on development
 * builds would then see no release at all. So further pages are read only while every page so far was full AND none
 * of them held a full release (published, not a pre-release, not a development build), up to [MAX_PAGES].
 */
object ReleasePaging {
    const val MAX_PAGES = 10

    /**
     * The non-draft releases from [fetchPage] (pages count from 1; drafts included in what it returns), newest first,
     * each tag once. An error on the first page is thrown; an error on a later page ends the reading with what the
     * earlier pages gave, which is never less than the single page read before.
     */
    fun <T> collect(
        fetchPage: (Int) -> List<T>, tag: (T) -> String, isDraft: (T) -> Boolean, isPrerelease: (T) -> Boolean,
        pageSize: Int, maxPages: Int = MAX_PAGES,
    ): List<T> {
        fun isFull(r: T) = !isDraft(r) && !isPrerelease(r) && !VersionCompare.isDev(tag(r))
        var batch = fetchPage(1)
        val all = batch.toMutableList()
        var page = 2
        while (batch.size >= pageSize && page <= maxPages && all.none(::isFull)) {
            batch = try { fetchPage(page) } catch (e: Exception) { break }
            all += batch
            page++
        }
        // A release published between two page reads moves the rest down one, so a tag can come back twice.
        val seen = HashSet<String>()
        return all.filter { !isDraft(it) && seen.add(tag(it)) }
    }
}
