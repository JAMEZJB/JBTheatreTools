package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * The catalog-id -> applicationId map, and the invariant that makes it load-bearing: every id it
 * names is also listed in `AndroidManifest.xml`'s `<queries>` block. If they ever drift, the launcher
 * silently reports an installed app as not installed.
 */
class PackageIdsTest {

    @Test fun mapsEveryCatalogAppToComJamesbreedonSlug() {
        assertEquals("com.jamesbreedon.helo", PackageIds.packageId("helocontrol"))
        assertEquals("com.jamesbreedon.cisco", PackageIds.packageId("ciscoswitchtools"))
        assertEquals("com.jamesbreedon.labels", PackageIds.packageId("ciscobrotherlabels"))
        assertEquals("com.jamesbreedon.inventory", PackageIds.packageId("machineinventory"))
        assertEquals("com.jamesbreedon.radar", PackageIds.packageId("reporadar"))
        assertEquals("com.jamesbreedon.jbtheatretools", PackageIds.packageId("jbtheatretools"))
        assertNull(PackageIds.packageId("not-an-app"))
    }

    @Test fun twentyTwoApps() {
        assertEquals(22, PackageIds.all().size)
        assertEquals(22, PackageIds.all().distinct().size)
        PackageIds.all().forEach { assertTrue(it.startsWith("com.jamesbreedon.")) }
    }

    @Test fun manifestQueriesListEveryManagedPackage() {
        val manifest = File(File(System.getProperty("jbtt.catalog")!!).parentFile,
            "JBTheatreToolsAndroid/app/src/main/AndroidManifest.xml").readText()
        PackageIds.all().forEach { pkg ->
            assertTrue("AndroidManifest <queries> is missing $pkg", manifest.contains("\"$pkg\""))
        }
    }
}
