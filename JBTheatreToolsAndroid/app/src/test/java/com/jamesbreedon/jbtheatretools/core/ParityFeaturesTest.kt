package com.jamesbreedon.jbtheatretools.core

import com.jamesbreedon.jbtheatretools.net.DownloadCancelledException
import com.jamesbreedon.jbtheatretools.net.GitHubException
import com.jamesbreedon.jbtheatretools.net.ReleaseAsset
import com.jamesbreedon.jbtheatretools.net.ReleaseInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.IOException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.time.Instant

/**
 * The desktop-parity pieces of the Android launcher: the list layout (pins, hidden, order, sections), the "N of M"
 * count, roll back's previous version, a batch's retry and top-up, and the setup file's layout section.
 */
class ParityFeaturesTest {

    private val categories = mapOf("dmx" to "Lighting", "psn" to "Lighting", "pdf" to "Office", "rename" to "Office", "convert" to null)
    private val ids = listOf("dmx", "psn", "pdf", "rename", "convert")
    private val catOf: (String) -> String = { AppLayout.categoryOf(categories[it]) }

    // ── ordering ──────────────────────────────────────────────────────────────

    @Test fun savedOrderFirstThenNewAppsInCatalogOrder() {
        assertEquals(ids, AppLayout.ordered(ids, emptyList()))
        assertEquals(listOf("pdf", "dmx", "psn", "rename", "convert"), AppLayout.ordered(ids, listOf("pdf", "dmx")))
        // Unknown ids (an app no longer in the catalog) and repeats are dropped.
        assertEquals(listOf("rename", "dmx", "psn", "pdf", "convert"), AppLayout.ordered(ids, listOf("gone", "rename", "rename")))
    }

    @Test fun pinnedFirstThenCategoriesInSavedThenCatalogOrder() {
        val g = AppLayout.groups(ids, catOf, pinned = listOf("rename"), savedCategoryOrder = emptyList(), catalogCategories = listOf("Lighting", "Office"))
        assertEquals(listOf("pinned", "Lighting", "Office", "Other"), g.map { it.key })
        assertEquals("Pinned", g[0].title)
        assertEquals(listOf("rename"), g[0].ids)
        assertEquals(listOf("pdf"), g[2].ids)          // the pinned app has left its category
        assertEquals(listOf("convert"), g[3].ids)      // no category → Other
        val saved = AppLayout.groups(ids, catOf, emptyList(), savedCategoryOrder = listOf("Office"), catalogCategories = listOf("Lighting", "Office"))
        assertEquals(listOf("Office", "Lighting", "Other"), saved.map { it.key })
        // A section with nothing to show (all filtered or hidden) isn't listed.
        assertEquals(listOf("Office"), AppLayout.groups(listOf("pdf"), catOf, emptyList(), emptyList(), listOf("Lighting", "Office")).map { it.key })
    }

    @Test fun categoryOrderAppendsStraysAlphabetically() {
        assertEquals(listOf("B", "A", "C", "Other", "Z"), AppLayout.categoryOrder(listOf("B"), listOf("A", "C"), listOf("Z", "Other", "A")))
    }

    @Test fun moveSwapsWithTheNearestVisibleNeighbourInTheSameSection() {
        val visible = ids.toSet() - "psn"
        val key: (String) -> String = { AppLayout.groupKey(it, emptyList(), catOf) }
        // pdf ↔ rename (same section)
        assertEquals(listOf("dmx", "psn", "rename", "pdf", "convert"), AppLayout.move(ids, "pdf", up = false, visible, key))
        // rename up past pdf
        assertEquals(listOf("dmx", "psn", "rename", "pdf", "convert"), AppLayout.move(ids, "rename", up = true, visible, key))
        // The edge of its section: pdf can't move up into Lighting, and dmx's only neighbour (psn) is hidden.
        assertNull(AppLayout.move(ids, "pdf", up = true, visible, key))
        assertNull(AppLayout.move(ids, "dmx", up = false, visible, key))
        assertNull(AppLayout.move(ids, "nope", up = true, visible, key))
        // Pinned apps reorder among themselves only.
        val pins = listOf("convert", "dmx")
        val pinKey: (String) -> String = { AppLayout.groupKey(it, pins, catOf) }
        assertEquals(listOf("convert", "psn", "pdf", "rename", "dmx"), AppLayout.move(ids, "convert", up = true, ids.toSet(), pinKey))
    }

    @Test fun moveSectionKeepsPinnedFirst() {
        assertEquals(listOf("Office", "Lighting", "Other"), AppLayout.moveSection(listOf("pinned", "Lighting", "Office", "Other"), "Office", up = true))
        assertNull(AppLayout.moveSection(listOf("pinned", "Lighting", "Office"), "Lighting", up = true))
        assertNull(AppLayout.moveSection(listOf("Lighting", "Office"), "Office", up = false))
        assertNull(AppLayout.moveSection(listOf("pinned", "Lighting"), "pinned", up = false))
    }

    @Test fun pinTogglesAndAppendsToThePinnedSection() {
        val a = AppLayout.togglePin(LayoutPrefs(pinned = listOf("dmx")), "pdf")
        assertEquals(listOf("dmx", "pdf"), a.pinned)
        assertEquals(listOf("pdf"), AppLayout.togglePin(a, "dmx").pinned)
    }

    // ── "3 of 24 apps" ────────────────────────────────────────────────────────

    @Test fun countLabelUsesTheDesktopWording() {
        assertEquals("3 of 24 apps", AppLayout.countLabel(3, 24, filtering = true))
        assertEquals("0 of 1 app", AppLayout.countLabel(0, 1, filtering = true))
        assertEquals("24 apps", AppLayout.countLabel(24, 24, filtering = false))
        assertEquals("1 app", AppLayout.countLabel(1, 1, filtering = false))
    }

    // ── roll back ─────────────────────────────────────────────────────────────

    @Test fun previousVersionTracksTheVersionReplaced() {
        assertNull(PreviousVersion.after(null, "1.2.0", null))                   // first install
        assertEquals("1.1.0", PreviousVersion.after("1.1.0", "1.2.0", null))     // update
        assertEquals("1.1.0", PreviousVersion.after("1.2.0", "1.2.0", "1.1.0"))  // reinstall keeps the old previous
        assertEquals("1.2.0", PreviousVersion.after("1.2.0", "1.1.0", "1.1.0"))  // after a roll back, previous is the newer one
    }

    private val rename = CatalogApp(
        id = "rename", name = "Rename Tools", owner = "o", repo = "RenameTools",
        assets = mapOf("macos" to "RenameTools-macOS.zip"),
    )

    private fun release(tag: String, apk: Boolean = true, signed: Boolean = true, prerelease: Boolean = false) = ReleaseInfo(
        tagName = tag, prerelease = prerelease,
        assets = buildList {
            if (apk) add(ReleaseAsset(1, "RenameTools-$tag-android-arm64.apk", 10))
            if (signed) { add(ReleaseAsset(2, "SHA256SUMS")); add(ReleaseAsset(3, "SHA256SUMS.minisig")) }
        },
    )

    @Test fun rollBackOnlyGoesBackToAReleaseTheStrictInstallCanPass() {
        val releases = listOf(release("v0.3.3"), release("v0.3.2"), release("v0.3.1", signed = false), release("v0.3.0", apk = false))
        assertEquals("v0.3.2", PreviousVersion.rollbackTag(rename, "0.3.3", "0.3.2", releases, devChannel = false))
        assertNull(PreviousVersion.rollbackTag(rename, "0.3.2", "0.3.3", releases, false))  // never forward
        assertNull(PreviousVersion.rollbackTag(rename, null, "0.3.2", releases, false))     // not installed
        assertNull(PreviousVersion.rollbackTag(rename, "0.3.3", null, releases, false))     // nothing recorded
        assertNull(PreviousVersion.rollbackTag(rename, "0.3.3", "0.3.1", releases, false))  // unsigned checksums
        assertNull(PreviousVersion.rollbackTag(rename, "0.3.3", "0.3.0", releases, false))  // no Android build
        assertNull(PreviousVersion.rollbackTag(rename, "0.3.3", "0.3.2", emptyList(), false)) // releases not loaded
        val dev = listOf(release("v0.3.3-dev.1", prerelease = true))
        assertNull(PreviousVersion.rollbackTag(rename, "0.3.3", "0.3.3-dev.1", dev, devChannel = false))
        assertEquals("v0.3.3-dev.1", PreviousVersion.rollbackTag(rename, "0.3.3", "0.3.3-dev.1", dev, devChannel = true))
    }

    @Test fun installVersionListsAndroidBuildsNewestFirst() {
        val list = PreviousVersion.installable(rename, listOf(release("v0.3.1", signed = false), release("v0.3.3"), release("v0.3.0", apk = false), release("v0.3.2")))
        assertEquals(listOf("v0.3.3", "v0.3.2", "v0.3.1"), list.map { it.tagName })   // an unsigned one may still be picked
        assertTrue(PreviousVersion.needsRemoval("0.3.3", "v0.3.2"))
        assertFalse(PreviousVersion.needsRemoval("0.3.2", "v0.3.3"))
        assertFalse(PreviousVersion.needsRemoval("0.3.3", "v0.3.3"))
        assertFalse(PreviousVersion.needsRemoval(null, "v0.3.2"))
    }

    // ── batches: retry once, top up ───────────────────────────────────────────

    @Test fun onlyNetworkAndIoErrorsAreTransient() {
        assertTrue(TransientError.isTransient(IOException("reset")))
        assertTrue(TransientError.isTransient(SocketTimeoutException()))
        assertTrue(TransientError.isTransient(UnknownHostException()))
        assertTrue(TransientError.isTransient(GitHubException.http(502)))
        assertFalse(TransientError.isTransient(GitHubException.unauthorized()))
        assertFalse(TransientError.isTransient(GitHubException.noRelease()))
        assertFalse(TransientError.isTransient(DownloadCancelledException()))
        assertFalse(TransientError.isTransient(ShowLockedException()))
        assertFalse(TransientError.isTransient(IllegalStateException("checksum mismatch")))   // verification
        assertFalse(TransientError.isTransient(null))
    }

    @Test fun topUpAddsEachAppAtMostOncePerBatch() {
        val q = BatchQueue(listOf("a", "b")) { it }
        assertEquals("a", q.next())
        assertEquals("b", q.next { listOf("a", "c") })   // not run out yet: no top-up
        // Ran out: c is new, a and b were had.
        assertEquals("c", q.next { listOf("a", "b", "c", "c") })
        assertNull(q.next { listOf("a", "b", "c") })      // nothing new: done (a failure isn't re-added)
        assertEquals("d", q.next { listOf("d") })
        assertNull(q.next())
        assertEquals(setOf("a", "b", "c", "d"), q.keys)
    }

    @Test fun slotsAreRecheckedJustBeforeTheirTurn() {
        val update = BatchSlot(rename, installedAtList = "0.3.2", target = "0.3.3")
        assertNull(BatchRules.skipReason(update, "0.3.2", heldNow = false))
        assertEquals("is held", BatchRules.skipReason(update, "0.3.2", heldNow = true))
        assertEquals("was removed meanwhile", BatchRules.skipReason(update, null, false))
        assertEquals("is already at v0.3.3", BatchRules.skipReason(update, "0.3.3", false))
        val fresh = BatchSlot(rename, target = "0.3.3")
        assertNull(BatchRules.skipReason(fresh, null, heldNow = true))
        val import = BatchSlot(rename, tag = "v0.3.2", onlyIfMissing = true)
        assertEquals("is already installed", BatchRules.skipReason(import, "0.3.3", false))
        assertNull(BatchRules.skipReason(import, null, false))
    }

    @Test fun installEveryAppTakesMissingAppsAndPendingUpdates() {
        fun st(id: String, installed: String?, latest: String?, apk: Boolean = true, held: Boolean = false, hidden: Boolean = false) =
            AppStatus(rename.copy(id = id), installedVersion = installed, latestVersion = latest,
                apkAssetName = if (apk) "x.apk" else null, held = held, hidden = hidden)
        val statuses = listOf(
            st("missing", null, "1.0.0"), st("current", "1.0.0", "1.0.0"), st("update", "1.0.0", "1.1.0"),
            st("held", "1.0.0", "1.1.0", held = true), st("noapk", null, "1.0.0", apk = false), st("norelease", null, null, apk = false, hidden = true),
        )
        assertEquals(listOf("missing", "update"), BatchRules.installAllWork(statuses).map { it.app.id })
        assertEquals(listOf("update"), BatchRules.updateWork(statuses).map { it.app.id })
    }

    // ── rows ──────────────────────────────────────────────────────────────────

    @Test fun latestLineMatchesTheDesktopRow() {
        val now = Instant.parse("2026-10-02T12:00:00Z")
        assertEquals("Latest: v1.2.0 (3 days ago) · 22 MB", RowText.latestLine("1.2.0", "2026-09-29T09:00:00Z", 22_000_000, now))
        assertEquals("Latest: v1.2.0", RowText.latestLine("v1.2.0", null, 0, now))
        assertEquals("Update all (3) · 45 MB", RowText.batchLabel("Update all", 3, 45_000_000))
        assertEquals("Install every app (12)", RowText.batchLabel("Install every app", 12, 0))
    }

    // ── setup file layout ─────────────────────────────────────────────────────

    @Test fun layoutRoundTripsThroughTheSharedSetupFormat() {
        val prefs = LayoutPrefs(
            pinned = listOf("pdf", "gone"), hidden = setOf("convert"), order = listOf("rename"),
            categoryOrder = listOf("Office"), collapsed = setOf("pinned", "Lighting"),
        )
        val out = AppLayout.toProfile(prefs, ids, listOf("Lighting", "Office"), ids.map(catOf))
        assertEquals(listOf("pdf"), out.pinned)
        assertEquals(listOf("convert"), out.hidden)
        assertEquals(listOf("rename", "dmx", "psn", "pdf", "convert"), out.order)   // every app, in list order
        assertEquals(listOf("Office", "Lighting", "Other"), out.categoryOrder)       // every section
        val text = SetupProfile(createdAt = "2026-10-02T12:00:00Z", createdBy = "test", layout = out).serialize()
        val back = SetupProfile.parse(text).layout!!
        assertEquals(out, back)
        val prefsBack = AppLayout.fromProfile(back, ids.toSet())
        assertEquals(listOf("pdf"), prefsBack.pinned)
        assertEquals(setOf("convert"), prefsBack.hidden)
        assertEquals(setOf("pinned", "Lighting"), prefsBack.collapsed)
    }

    @Test fun importKeepsOnlyKnownAppsAndReadsTheMacPinnedKey() {
        val json = """{"kind":"jbtheatretools-setup","schemaVersion":1,"apps":[],
            "layout":{"pinned":["dmx","unknown","dmx"],"hidden":["x","pdf"],"order":["psn","nope"],
            "categoryOrder":["Office","Office","Lighting"],"collapsed":["\u0001pinned","Office"]}}"""
        val l = AppLayout.fromProfile(SetupProfile.parse(json).layout!!, ids.toSet())
        assertEquals(listOf("dmx"), l.pinned)
        assertEquals(setOf("pdf"), l.hidden)
        assertEquals(listOf("psn"), l.order)
        assertEquals(listOf("Office", "Lighting"), l.categoryOrder)
        assertEquals(setOf("pinned", "Office"), l.collapsed)
        // A file with no layout section imports none.
        assertNull(SetupProfile.parse("""{"kind":"jbtheatretools-setup","schemaVersion":1,"apps":[]}""").layout)
    }
}
