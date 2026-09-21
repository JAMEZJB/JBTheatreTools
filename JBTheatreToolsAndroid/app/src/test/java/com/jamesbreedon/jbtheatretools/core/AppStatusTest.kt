package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * "Is there an update?" — the comparison between what `PackageManager` reports as installed and the
 * latest release tag, including Convert's rolling `build-YYYYMMDD` versions.
 */
class AppStatusTest {

    private fun app(id: String = "helocontrol") =
        CatalogApp(id = id, name = "App", owner = "o", repo = "r", assets = mapOf("macos" to "App-macOS.zip"))

    private fun status(installed: String?, latest: String?, asset: String? = "App-v1-android-arm64.apk") =
        AppStatus(app(), installedVersion = installed, latestVersion = latest, apkAssetName = asset)

    @Test fun notInstalledIsNotAnUpdate() {
        val s = status(null, "2.1.1")
        assertFalse(s.isInstalled)
        assertFalse(s.hasUpdate)
        assertTrue(s.canInstall)
    }

    @Test fun sameVersionIsNotAnUpdate() {
        assertFalse(status("2.1.1", "2.1.1").hasUpdate)
        // The installed versionName never carries the tag's leading "v"; the comparison tolerates both.
        assertFalse(status("2.1.1", "v2.1.1").hasUpdate)
    }

    @Test fun newerReleaseIsAnUpdate() {
        assertTrue(status("2.1.0", "2.1.1").hasUpdate)
        assertTrue(status("1.26.0", "v1.27.0").hasUpdate)
    }

    @Test fun olderReleaseIsNotAnUpdate() {
        // A downgrade (e.g. a yanked release) must never present itself as an update.
        assertFalse(status("2.2.0", "2.1.1").hasUpdate)
    }

    @Test fun dateTaggedAppsCompareCorrectly() {
        assertTrue(status("build-20260901", "build-20260912").hasUpdate)
        assertFalse(status("build-20260912", "build-20260912").hasUpdate)
        assertFalse(status("build-20260912", "build-20260901").hasUpdate)
    }

    @Test fun anAppWithNoAndroidAssetCannotBeInstalled() {
        val s = status(null, "2.1.1", asset = null)
        assertFalse(s.canInstall)
        assertFalse(s.hasUpdate)
    }

    @Test fun installedWithNoFeedYetIsNotAnUpdate() {
        val s = status("2.1.1", null, asset = null)
        assertTrue(s.isInstalled)
        assertFalse(s.hasUpdate)
    }
}
