package com.jamesbreedon.jbtheatretools.net

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/**
 * A local relay override may only ever point at an https jamesbreedon.com host — anything that could
 * write this app's preferences must not be able to redirect the API calls (and the passphrase with
 * them) somewhere else.
 */
class RelayPolicyTest {

    @Test fun acceptsTheHouseHosts() {
        assertEquals(
            "https://example.jamesbreedon.com/ghapi",
            RelayPolicy.validatedOverride("https://example.jamesbreedon.com/ghapi"),
        )
        assertEquals("https://jamesbreedon.com", RelayPolicy.validatedOverride("https://jamesbreedon.com"))
    }

    @Test fun refusesEverythingElse() {
        assertNull(RelayPolicy.validatedOverride(null))
        assertNull(RelayPolicy.validatedOverride(""))
        assertNull(RelayPolicy.validatedOverride("   "))
        assertNull(RelayPolicy.validatedOverride("http://jamesbreedon.com"))          // not https
        assertNull(RelayPolicy.validatedOverride("https://evil.example.com"))         // wrong host
        assertNull(RelayPolicy.validatedOverride("https://jamesbreedon.com.evil.io")) // suffix trick
        assertNull(RelayPolicy.validatedOverride("not a url"))
    }

    @Test fun notesUrlSitsBesideTheApiRoot() {
        assertEquals(
            "https://host.jamesbreedon.com/notes/whats-new.json",
            notesUrl("https://host.jamesbreedon.com/ghapi"),
        )
        assertEquals(
            "http://10.0.2.2:8787/notes/whats-new.json",
            notesUrl("http://10.0.2.2:8787/ghapi"),
        )
        assertNull(notesUrl("nonsense"))
    }
}
