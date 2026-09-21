package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The pinned suite signing certificate — fail-closed comparison. */
class SigningCertTest {

    private val suite = "0dfe8516d9c7ca269a19be95c4e49ef87af576dbcbe815865f060127496c6043"

    @Test fun thePinIsTheSuiteCertificate() {
        assertEquals(suite, SigningCert.SUITE_CERT_SHA256)
        assertEquals(listOf(suite), SigningCert.TRUSTED)
    }

    @Test fun acceptsTheSuiteFingerprintInAnySpelling() {
        assertTrue(SigningCert.isTrusted(suite))
        assertTrue(SigningCert.isTrusted(suite.uppercase()))
        assertTrue(SigningCert.isTrusted(suite.chunked(2).joinToString(":")))
        assertTrue(SigningCert.isTrusted("  $suite  "))
    }

    @Test fun refusesEverythingElse() {
        assertFalse(SigningCert.isTrusted(null))
        assertFalse(SigningCert.isTrusted(""))
        // One character different is not the suite certificate.
        assertFalse(SigningCert.isTrusted(suite.dropLast(1) + "4"))
        assertFalse(SigningCert.isTrusted("0".repeat(64)))
        // A truncated prefix must not pass.
        assertFalse(SigningCert.isTrusted(suite.take(32)))
    }

    @Test fun fingerprintIsSha256OfTheDerBytes() {
        // sha256("hello\n") — the helper is a plain SHA-256 over whatever DER it is given.
        assertEquals(
            "5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03",
            SigningCert.fingerprint("hello\n".toByteArray()),
        )
    }
}
