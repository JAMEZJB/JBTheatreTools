package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Parses the REAL repo-root `catalog.json` — the same file the macOS and Windows launchers bundle and
 * the Gradle build copies into this APK's assets. Its path is handed in by the build (`jbtt.catalog`),
 * so there is still exactly one copy of the file.
 */
class CatalogTest {

    private val catalog: Catalog by lazy {
        val path = System.getProperty("jbtt.catalog") ?: error("jbtt.catalog system property not set")
        Catalog.parse(File(path).readText())
    }

    @Test fun parsesEveryApp() {
        assertEquals(1, catalog.schemaVersion)
        assertEquals(22, catalog.apps.size)
        catalog.apps.forEach { app ->
            assertTrue("${app.id} has no name", app.name.isNotBlank())
            assertTrue("${app.id} has no owner", app.owner.isNotBlank())
            assertTrue("${app.id} has no repo", app.repo.isNotBlank())
        }
        assertEquals(catalog.apps.size, catalog.apps.map { it.id }.distinct().size)
    }

    @Test fun carriesTheDownloadServerAndSelfEntry() {
        assertNotNull(catalog.downloadServer)
        assertTrue(catalog.downloadServer!!.startsWith("https://"))
        val self = catalog.selfInfo
        assertNotNull(self)
        assertEquals("JBTheatreTools", self!!.repo)
        assertEquals("jbtheatretools", self.asApp().id)
    }

    @Test fun variantsResolveLikeTheDesktopLaunchers() {
        val ndi = catalog.apps.first { it.id == "nditools" }
        assertTrue(ndi.hasVariants)
        assertEquals(listOf("standard", "full"), ndi.variants.map { it.id })
        // Unknown / null variant falls back to the first (default) variant.
        assertEquals(ndi.variants[0].assets, ndi.assets(null))
        assertEquals(ndi.variants[0].assets, ndi.assets("nonsense"))
        assertEquals(ndi.variants[1].assets, ndi.assets("full"))
        // Install slots: the default keeps the plain id, others get `<id>@<variant>`.
        assertEquals("nditools", ndi.installKey("standard"))
        assertEquals("nditools@full", ndi.installKey("full"))

        val helo = catalog.apps.first { it.id == "helocontrol" }
        assertFalse(helo.hasVariants)
        assertEquals(helo.assets, helo.assets("anything"))
        assertEquals("helocontrol", helo.installKey(null))
    }

    @Test fun categoriesCoverEveryApp() {
        val ordered = catalog.orderedCategories()
        assertTrue(ordered.isNotEmpty())
        val grouped = catalog.apps.count { it.category in ordered }
        assertEquals(catalog.apps.size, grouped)
    }

    @Test fun whatsNewLinesAreOptionalAndParsed() {
        val helo = catalog.apps.first { it.id == "helocontrol" }
        assertNotNull(helo.whatsNew)
        assertNotNull(helo.whatsNewVersion)
    }

    @Test fun everyCatalogAppHasAnApplicationId() {
        // PackageIds is what the PackageManager lookup uses; a missing slug means the launcher would
        // silently never see that app as installed.
        catalog.apps.forEach { app ->
            assertNotNull("no applicationId mapped for ${app.id}", PackageIds.packageId(app.id))
        }
        assertEquals(catalog.apps.size, PackageIds.catalogIds.size)
    }

    @Test fun ignoresUnknownKeys() {
        val text = """{"schemaVersion":1,"somethingNew":true,
            "apps":[{"id":"x","name":"X","owner":"o","repo":"r","assets":{},"futureField":42}]}"""
        val parsed = Catalog.parse(text)
        assertEquals(1, parsed.apps.size)
    }
}
