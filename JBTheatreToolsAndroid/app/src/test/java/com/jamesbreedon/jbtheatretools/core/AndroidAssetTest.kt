package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Test
import java.io.File

/** The `<AssetPrefix>-v<ver>-android-arm64.apk` naming, derived from the catalog's macOS asset. */
class AndroidAssetTest {

    private val catalog: Catalog by lazy {
        Catalog.parse(File(System.getProperty("jbtt.catalog")!!).readText())
    }

    @Test fun derivesThePrefixFromTheMacAssetName() {
        assertEquals("HeloControl", AndroidAsset.prefixFromMacAsset("HeloControl-macOS.zip"))
        assertEquals("DMXTools", AndroidAsset.prefixFromMacAsset("DMXTools-macOS.zip"))
        // The one app whose macOS asset uses dots and a universal2 marker.
        assertEquals(
            "Cisco.Switch.Tools",
            AndroidAsset.prefixFromMacAsset("Cisco.Switch.Tools.macOS.universal2.zip"),
        )
        // Per-arch macOS builds (the Full variants).
        assertEquals("PDFTools-Full", AndroidAsset.prefixFromMacAsset("PDFTools-Full-macOS-arm64.zip"))
        assertEquals("convert", AndroidAsset.prefixFromMacAsset("convert-macOS.zip"))
    }

    @Test fun buildsTheApkNameForEveryCatalogApp() {
        catalog.apps.forEach { app ->
            val name = AndroidAsset.apkName(app, "v1.2.3")
            assertNotNull("no Android asset name derivable for ${app.id}", name)
            assertEquals(true, name!!.endsWith("-v1.2.3-android-arm64.apk"))
        }
        val helo = catalog.apps.first { it.id == "helocontrol" }
        assertEquals("HeloControl-v2.1.1-android-arm64.apk", AndroidAsset.apkName(helo, "v2.1.1"))
        // A tag with or without the leading v gives the same asset name.
        assertEquals("HeloControl-v2.1.1-android-arm64.apk", AndroidAsset.apkName(helo, "2.1.1"))
    }

    @Test fun dateTaggedAppsWork() {
        val convert = catalog.apps.first { it.id == "convert" }
        assertEquals(
            "convert-vbuild-20260912-android-arm64.apk",
            AndroidAsset.apkName(convert, "build-20260912"),
        )
    }

    @Test fun anExplicitCatalogKeyWins() {
        val app = CatalogApp(
            id = "x", name = "X", owner = "o", repo = "r",
            assets = mapOf("macos" to "Something-macOS.zip", "android-arm64" to "Custom.apk"),
        )
        assertEquals("Custom.apk", AndroidAsset.apkName(app, "v9.9.9"))
    }

    @Test fun sidecarName() {
        assertEquals(
            "HeloControl-v2.1.1-android-arm64.apk.sha256",
            AndroidAsset.sha256SidecarName("HeloControl-v2.1.1-android-arm64.apk"),
        )
    }
}
