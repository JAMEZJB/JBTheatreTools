package com.jamesbreedon.jbtheatretools.core

import com.jamesbreedon.jbtheatretools.net.ReleaseInfo
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Semver pre-release ordering and the Dev-channel release pick. */
class DevChannelTest {
    private fun rel(tag: String, pre: Boolean = false) = ReleaseInfo(tagName = tag, prerelease = pre)

    @Test fun preReleasesSortBeforeTheirRelease() {
        assertTrue(VersionCompare.isNewer("0.1.0-dev.2", "0.1.0-dev.1"))
        assertTrue(VersionCompare.isNewer("v0.1.0", "v0.1.0-dev.2"))       // a release supersedes its dev builds
        assertTrue(VersionCompare.isNewer("0.1.0-dev.1", "0.0.9"))
        assertTrue(VersionCompare.isNewer("0.1.0-dev.10", "0.1.0-dev.9"))  // numeric, not lexical
        assertFalse(VersionCompare.isNewer("1.2", "1.2.0"))
        assertTrue(VersionCompare.isNewer("build-20260921", "build-20260915"))  // Convert's date tags unchanged
        assertEquals("0.1.0", VersionCompare.core("v0.1.0-dev.3"))
        assertTrue(VersionCompare.isDev("v0.1.0-dev.1")); assertFalse(VersionCompare.isDev("v0.1.0"))
    }

    @Test fun devChannelOffNeverPicksADevBuild() {
        val list = listOf(rel("v0.2.0-dev.1", pre = true), rel("v0.1.0"))
        assertEquals("v0.1.0", ReleasePick.latest(list, devChannel = false)!!.tagName)
        // an app whose only releases are dev builds (NETGEAR / MikroTik today) shows nothing, not a dev build
        assertNull(ReleasePick.latest(listOf(rel("v0.1.0-dev.1", pre = true)), devChannel = false))
        // a non-dev pre-release is still the fallback when there is no stable release
        assertEquals("v0.1.0-rc1", ReleasePick.latest(listOf(rel("v0.1.0-rc1", pre = true)), false)!!.tagName)
    }

    @Test fun devChannelOnPicksTheHighestIncludingDevBuilds() {
        val list = listOf(rel("v0.1.0"), rel("v0.2.0-dev.1", pre = true), rel("v0.2.0-dev.2", pre = true))
        assertEquals("v0.2.0-dev.2", ReleasePick.latest(list, devChannel = true)!!.tagName)
        val released = list + rel("v0.2.0")
        assertEquals("v0.2.0", ReleasePick.latest(released, devChannel = true)!!.tagName)
    }

    @Test fun devApkNameCarriesTheFullTag() {
        val app = CatalogApp(id = "x", name = "X", owner = "o", repo = "NetgearSwitchTools",
            assets = mapOf("macos" to "NetgearSwitchTools-macOS.zip"))
        assertEquals("NetgearSwitchTools-v0.1.0-dev.1-android-arm64.apk", AndroidAsset.apkName(app, "v0.1.0-dev.1"))
    }
}
