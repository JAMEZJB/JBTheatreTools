package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Test

/** The release list reads more pages only while needed: the same rule and cases as the Windows launcher's tests. */
class ReleasePagingTest {
    private data class R(val tag: String, val pre: Boolean = false, val draft: Boolean = false)

    private fun collect(pages: List<List<R>>, size: Int = 3, calls: MutableList<Int> = mutableListOf(), failFrom: Int = 99) =
        ReleasePaging.collect({ p -> calls += p; if (p >= failFrom) error("synthetic") else pages.getOrElse(p - 1) { emptyList() } },
            { it.tag }, { it.draft }, { it.pre }, size)

    @Test fun onePageWhenItHoldsAFullRelease() {
        val calls = mutableListOf<Int>()
        val out = collect(listOf(listOf(R("v1.1.0-dev.2", true), R("v1.0.0"), R("v0.9.0")), listOf(R("v0.8.0"))), calls = calls)
        assertEquals(listOf(1), calls)
        assertEquals(listOf("v1.1.0-dev.2", "v1.0.0", "v0.9.0"), out.map { it.tag })
    }

    @Test fun readsOnWhileFullPagesHoldOnlyDevBuilds() {
        val calls = mutableListOf<Int>()
        val dev = listOf(R("v2.0.0-dev.3", true), R("v2.0.0-dev.2", true), R("v2.0.0-dev.1", true))
        val out = collect(listOf(dev, listOf(R("v1.9.0"), R("v1.8.0"))), calls = calls)
        assertEquals(listOf(1, 2), calls)
        assertEquals("v1.9.0", out[3].tag)   // the full release that fell off page 1 is found
    }

    @Test fun aShortPageEndsItAndDraftsAndRepeatsAreDropped() {
        val calls = mutableListOf<Int>()
        val out = collect(listOf(listOf(R("v1.0.0-dev.1", true), R("draft", draft = true))), calls = calls)
        assertEquals(listOf(1), calls)
        assertEquals(listOf("v1.0.0-dev.1"), out.map { it.tag })
        val dup = collect(listOf(listOf(R("a-dev.1", true), R("b-dev.1", true), R("c-dev.1", true)), listOf(R("c-dev.1", true), R("v1.0.0"))))
        assertEquals(listOf("a-dev.1", "b-dev.1", "c-dev.1", "v1.0.0"), dup.map { it.tag })
    }

    @Test fun aLaterPageErrorKeepsWhatWasReadAndTheCapHolds() {
        val dev = List(3) { R("v1.0.0-dev.$it", true) }
        assertEquals(3, collect(listOf(dev, dev), failFrom = 2).size)
        val calls = mutableListOf<Int>()
        collect(List(20) { p -> List(3) { R("v0.$p.$it-dev.1", true) } }, calls = calls)
        assertEquals(ReleasePaging.MAX_PAGES, calls.size)
    }
}
