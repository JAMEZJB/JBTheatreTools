package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** "Install updates automatically": which Android versions, which apps, and what it says afterwards. */
class AutoUpdatePolicyTest {

    private fun status(id: String, installed: String?, latest: String?, held: Boolean = false, asset: String? = "$id.apk") =
        AppStatus(
            CatalogApp(id = id, name = id.uppercase(), owner = "o", repo = id),
            installedVersion = installed, latestVersion = latest, apkAssetName = asset, held = held,
        )

    @Test fun onlyAndroid12AndLater() {
        assertFalse(AutoUpdatePolicy.available(29))
        assertFalse(AutoUpdatePolicy.available(30))
        assertTrue(AutoUpdatePolicy.available(31))
        assertTrue(AutoUpdatePolicy.available(35))
    }

    @Test fun onlyInstalledAppsWithAnUpdateThatArentHeldOrBusy() {
        val all = listOf(
            status("a", "1.0.0", "1.1.0"),               // updates
            status("b", "1.1.0", "1.1.0"),               // current
            status("c", null, "1.0.0"),                  // not installed: never installed automatically
            status("d", "1.0.0", "1.1.0", held = true),  // held (by its row)
            status("e", "1.0.0", "1.1.0"),               // held (by settings)
            status("f", "1.0.0", "1.1.0"),               // already installing
            status("g", "1.0.0", "1.1.0", asset = null), // no Android build
        )
        assertEquals(listOf("a"), AutoUpdatePolicy.candidates(all, held = setOf("e"), busy = setOf("f")).map { it.app.id })
    }

    @Test fun whatItSaysAfterwards() {
        assertNull(AutoUpdatePolicy.notification(emptyList(), emptyList()))
        assertEquals("Apps updated" to "Updated DMX Tools to v1.2.0",
            AutoUpdatePolicy.notification(listOf("DMX Tools" to "1.2.0"), emptyList()))
        assertEquals("Updates waiting" to "PSN Tools needs you to tap Update in JB Theatre Tools.",
            AutoUpdatePolicy.notification(emptyList(), listOf("PSN Tools")))
        assertEquals("Apps updated" to "Updated 2 apps. 3 apps need you to tap Update in JB Theatre Tools.",
            AutoUpdatePolicy.notification(listOf("A" to "1", "B" to "2"), listOf("C", "D", "E")))
    }
}
