package com.jamesbreedon.jbtheatretools.core

import com.jamesbreedon.jbtheatretools.net.ReleaseAsset
import com.jamesbreedon.jbtheatretools.net.ReleaseInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** The newest release that carries an Android build wins (same cases as the macOS / Windows launcher pick). */
class LauncherPickTest {
    private fun r(tag: String, pre: Boolean, vararg names: String) =
        ReleaseInfo(tag, names.mapIndexed { i, n -> ReleaseAsset(i.toLong(), n, 1) }, prerelease = pre)
    private val hasApk: (ReleaseInfo) -> Boolean = { rel -> rel.assets.any { it.name.endsWith(".apk") } }

    @Test fun aDesktopOnlyDevBuildIsSkipped() {
        val macOnly = r("v1.32.1-dev.1", true, "JBTheatreTools-macOS.zip")
        val release = r("v1.32.0", false, "JBTheatreTools-v1.32.0-android-arm64.apk")
        assertEquals("v1.32.0", ReleasePick.latestWithBuild(listOf(macOnly, release), devChannel = true, hasBuild = hasApk)?.tagName)
    }

    @Test fun newestBuildCarryingReleaseWins() {
        val a = r("v1.32.1-dev.2", true, "JBTheatreTools-v1.32.1-dev.2-android-arm64.apk")
        val b = r("v1.32.1-dev.1", true, "JBTheatreTools-macOS.zip")
        assertEquals("v1.32.1-dev.2", ReleasePick.latestWithBuild(listOf(b, a), devChannel = true, hasBuild = hasApk)?.tagName)
        assertNull(ReleasePick.latestWithBuild(listOf(b, a), devChannel = false, hasBuild = hasApk))   // dev builds only with the switch on
    }

    @Test fun noAndroidBuildAnywhereFallsBackToTheLatest() {
        val only = r("v1.0.0", false, "App-macOS.zip")
        assertEquals("v1.0.0", ReleasePick.latestWithBuild(listOf(only), devChannel = false, hasBuild = hasApk)?.tagName)
    }
}
