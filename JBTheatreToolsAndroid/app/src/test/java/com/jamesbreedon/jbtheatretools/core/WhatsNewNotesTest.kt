package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

/** The relay's editable "New in" overlay (v1.26.0) — parsed defensively, displayed verbatim. */
class WhatsNewNotesTest {

    private fun parse(json: String) = WhatsNewNotes.parse(json.toByteArray())

    private val app = CatalogApp(
        id = "helocontrol", name = "HELO Control", owner = "o", repo = "r",
        whatsNew = "bundled line", whatsNewVersion = "v2.1.0",
    )

    @Test fun relayLineOverridesTheBundledOne() {
        val notes = parse(
            """{"schemaVersion":1,"apps":{"helocontrol":{"whatsNew":"relay line","whatsNewVersion":"v2.1.1"}}}"""
        )
        assertEquals("relay line" to "v2.1.1", notes.resolved(app))
    }

    @Test fun aMissingKeyKeepsTheBundledLine() {
        val notes = parse("""{"schemaVersion":1,"apps":{"dmxtools":{"whatsNew":"x"}}}""")
        assertEquals("bundled line" to "v2.1.0", notes.resolved(app))
    }

    @Test fun anEmptyLineHidesIt() {
        val notes = parse("""{"schemaVersion":1,"apps":{"helocontrol":{"whatsNew":""}}}""")
        assertNull(notes.resolved(app).first)
    }

    @Test fun textIsCleanedAndCapped() {
        val long = "x".repeat(400)
        val notes = parse("""{"schemaVersion":1,"apps":{"helocontrol":{"whatsNew":"  a\tb  c   d  $long"}}}""")
        val line = notes.resolved(app).first!!
        // Control characters are stripped (so "a\tb" closes up), then whitespace runs collapse to one
        // space and the line is capped — exactly as the macOS `WhatsNewNotes.clean` does it.
        assertTrue(line, line.startsWith("ab c d "))
        assertTrue(line.length <= WhatsNewNotes.MAX_LINE_LENGTH + 1)   // +1 for the ellipsis
    }

    @Test fun aStrayHtmlPageCannotClobberTheBundledLines() {
        for (bad in listOf("<html>nope</html>", "", "{}", """{"apps":[]}""")) {
            try {
                parse(bad)
                fail("expected a parse failure for: $bad")
            } catch (e: WhatsNewNotes.Companion.ParseException) {
                // expected — the caller keeps the bundled lines
            }
        }
    }

    @Test fun aNewerSchemaIsRefused() {
        try {
            parse("""{"schemaVersion":99,"apps":{}}""")
            fail("expected a parse failure")
        } catch (e: WhatsNewNotes.Companion.ParseException) {
            // expected
        }
    }
}
