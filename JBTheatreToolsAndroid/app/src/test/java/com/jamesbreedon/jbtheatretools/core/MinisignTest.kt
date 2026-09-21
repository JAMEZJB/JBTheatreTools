package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.util.Base64

/**
 * Signature verification against
 *   (a) the REAL signed manifest from the public JBTheatreTools v1.15.0 release — the same fixture the
 *       macOS tests use, signed offline with the suite key, so this proves
 *       interoperability with the actual signing side rather than self-consistency; and
 *   (b) a THROWAWAY keypair generated for this test suite (its public key is in the fixtures; the
 *       secret key was never committed and is not the suite key).
 */
class MinisignTest {

    private val release = "JBTheatreTools v1.15.0"

    private fun fixture(name: String): ByteArray =
        javaClass.classLoader!!.getResourceAsStream("fixtures/$name")!!.use { it.readBytes() }

    private val manifest get() = fixture("SHA256SUMS")
    private val minisig get() = String(fixture("SHA256SUMS.minisig"), Charsets.UTF_8)

    @Test fun realReleaseManifestVerifies() {
        assertEquals(287, manifest.size)
        Minisign.verify(manifest, minisig, release)   // throws on failure
    }

    @Test fun parseExtractsFields() {
        val sig = Minisign.parse(minisig)
        assertTrue("releases are signed in minisign's default ED mode", sig.prehashed)
        assertEquals(release, sig.trustedComment)
        assertEquals(64, sig.signature.size)
        assertEquals(64, sig.globalSignature.size)
        // key id is a little-endian u64 -> printed big-endian by minisign
        val printed = sig.keyId.reversed().joinToString("") { "%02X".format(it) }
        assertEquals("FC6699A439B42178", printed)
    }

    @Test fun tamperedManifestFails() {
        try {
            Minisign.verify(manifest + "x".toByteArray(), minisig, release)
            fail("expected BadSignature")
        } catch (e: Minisign.VerifyException.BadSignature) {
            // expected
        }
    }

    @Test fun manifestIsBoundToItsOwnRelease() {
        try {
            Minisign.verify(manifest, minisig, "JBTheatreTools v1.14.0")
            fail("expected CommentMismatch")
        } catch (e: Minisign.VerifyException.CommentMismatch) {
            // expected — a validly-signed manifest from another release can't be replayed
        }
    }

    @Test fun tamperedTrustedCommentFails() {
        val forged = minisig.replace("v1.15.0", "v1.15.1")
        try {
            Minisign.verify(manifest, forged, "JBTheatreTools v1.15.1")
            fail("expected BadGlobalSignature")
        } catch (e: Minisign.VerifyException.BadGlobalSignature) {
            // expected — the comment is itself signed, and that is checked first
        }
    }

    @Test fun anotherKeyIsRejected() {
        val throwawayPub = String(fixture("throwaway.pub"), Charsets.UTF_8).lines()[1]
        try {
            Minisign.verify(manifest, minisig, release, publicKeyBase64 = throwawayPub)
            fail("expected UnknownKey")
        } catch (e: Minisign.VerifyException.UnknownKey) {
            // expected — the key id doesn't match
        }
    }

    @Test fun throwawayKeypairRoundTrip() {
        val pub = String(fixture("throwaway.pub"), Charsets.UTF_8).lines()[1]
        val sums = fixture("throwaway-SHA256SUMS")
        val sig = String(fixture("throwaway-SHA256SUMS.minisig"), Charsets.UTF_8)
        Minisign.verify(sums, sig, "DemoRepo v1.0.0", publicKeyBase64 = pub)

        // …and the same manifest under the wrong release name is refused.
        try {
            Minisign.verify(sums, sig, "DemoRepo v1.0.1", publicKeyBase64 = pub)
            fail("expected CommentMismatch")
        } catch (e: Minisign.VerifyException.CommentMismatch) {
            // expected
        }
        // …and a single flipped byte in the manifest is refused.
        val flipped = sums.copyOf().also { it[0] = (it[0] + 1).toByte() }
        try {
            Minisign.verify(flipped, sig, "DemoRepo v1.0.0", publicKeyBase64 = pub)
            fail("expected BadSignature")
        } catch (e: Minisign.VerifyException.BadSignature) {
            // expected
        }
    }

    @Test fun malformedInputsAreRefusedNotCrashed() {
        val cases = listOf(
            "",
            "untrusted comment: x\nnot-base64\ntrusted comment: y\nalso-not-base64",
            "untrusted comment: x\n" + Base64.getEncoder().encodeToString(ByteArray(10)) +
                "\ntrusted comment: y\n" + Base64.getEncoder().encodeToString(ByteArray(64)),
        )
        for (case in cases) {
            try {
                Minisign.verify(manifest, case, release)
                fail("expected Malformed for: $case")
            } catch (e: Minisign.VerifyException.Malformed) {
                // expected
            }
        }
    }
}
