package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * SHA256SUMS parsing — the shapes `sha256sum`/`shasum` actually produce, including Windows line
 * endings. The same cases the macOS `ChecksumParsingTests` and the Windows `Sha256Sums` tests assert.
 */
class Sha256SumsTest {

    @Test fun standardShasumFormat() {
        val sums = "abc123  App-macOS.zip\ndef456  App-v1.0.0-android-arm64.apk\n"
        assertEquals("abc123", Sha256Sums.expected("App-macOS.zip", sums))
        assertEquals("def456", Sha256Sums.expected("App-v1.0.0-android-arm64.apk", sums))
        assertNull(Sha256Sums.expected("Missing.zip", sums))
    }

    @Test fun windowsLineEndingsBinaryMarkerAndTabs() {
        val sums = "abc123 *App-macOS.zip\r\ndef456\tApp-Windows-x64.exe\r\n"
        assertEquals("abc123", Sha256Sums.expected("App-macOS.zip", sums))
        assertEquals("def456", Sha256Sums.expected("App-Windows-x64.exe", sums))
    }

    @Test fun nameMustMatchExactly() {
        val sums = "abc123  App-macOS.zip\n"
        assertNull(Sha256Sums.expected("App-macOS", sums))
        assertNull(Sha256Sums.expected("app-macos.zip", sums))
        assertNull(Sha256Sums.expected("App-macOS.zip", ""))
    }

    @Test fun hashesTheRealFixtureFile() {
        val fixture = File.createTempFile("sums", ".txt")
        fixture.writeText("hello\n")
        // sha256("hello\n")
        assertEquals(
            "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03",
            Sha256Sums.sha256Hex(fixture),
        )
        assertTrue(Sha256Sums.hexEquals(Sha256Sums.sha256Hex(fixture).uppercase(), Sha256Sums.sha256Hex(fixture)))
        fixture.delete()
    }

    @Test fun theRealSignedManifestListsItsAssets() {
        val text = javaClass.classLoader!!.getResourceAsStream("fixtures/SHA256SUMS")!!
            .use { String(it.readBytes(), Charsets.UTF_8) }
        assertEquals(
            "6e13dfbc9bd264076bdad6b7f18399869d5f5cffce2f0b400a0ad7dcd3096c2b",
            Sha256Sums.expected("JBTheatreTools-macOS.zip", text),
        )
    }
}
