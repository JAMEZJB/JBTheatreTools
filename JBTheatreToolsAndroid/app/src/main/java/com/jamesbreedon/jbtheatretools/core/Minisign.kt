package com.jamesbreedon.jbtheatretools.core

import net.i2p.crypto.eddsa.EdDSAEngine
import net.i2p.crypto.eddsa.EdDSAPublicKey
import net.i2p.crypto.eddsa.spec.EdDSANamedCurveTable
import net.i2p.crypto.eddsa.spec.EdDSAPublicKeySpec
import java.security.MessageDigest
import java.util.Base64

/**
 * Verifies minisign (Ed25519) signatures on release manifests — the Kotlin twin of the macOS
 * launcher's `Minisign.swift` and the Windows launcher's `Minisign.cs`.
 *
 * Why: a `SHA256SUMS` fetched from the same release, over the same channel, as the payload only proves
 * the download wasn't corrupted in transit — it can't tell a hostile release (or a hostile relay) from
 * a real one. Every release's `SHA256SUMS` is signed OFFLINE with the suite's minisign key,
 * and the launcher embeds the public key: a manifest is trusted only if that signature verifies. The
 * key never leaves the machine that signs; this code only ever verifies.
 *
 * minisign file format (`SHA256SUMS.minisig`):
 *
 *     untrusted comment: <ignored>
 *     base64( alg(2) ‖ key id(8) ‖ signature(64) )   alg "ED" = Ed25519 over BLAKE2b-512(file); "Ed" = over the file
 *     trusted comment: <text>
 *     base64( global signature(64) )                 Ed25519 over ( signature ‖ trusted comment )
 *
 * The trusted comment is `<repo> <tag>`; verifying it against the release being installed
 * binds a manifest to ONE release, so a validly-signed manifest from another release can't be replayed.
 *
 * `java.security.Signature.getInstance("Ed25519")` only exists from API 33, and minSdk is 29, so the
 * curve arithmetic comes from the small public-domain `net.i2p.crypto:eddsa` library on every level.
 */
object Minisign {
    /** The suite's minisign public key (key id FC6699A439B42178). Rotating it means a launcher release. */
    const val SUITE_PUBLIC_KEY = "RWR4IbQ5pJlm/OLhkB2EnuKqtxxz9/TKpCRycMpLJlZh5fxqMQDyZUoC"

    sealed class VerifyException(message: String) : Exception(message) {
        class Malformed(why: String) :
            VerifyException("The release's signature file is malformed ($why).")
        class UnknownKey :
            VerifyException("The release's manifest was signed with a different key than the suite key.")
        class BadSignature :
            VerifyException("The release's manifest signature does not verify.")
        class BadGlobalSignature :
            VerifyException("The release's signature comment does not verify.")
        class CommentMismatch(expected: String, got: String) :
            VerifyException("The signed manifest is for “$got”, not “$expected”.")
    }

    class PublicKey(base64: String) {
        val keyId: ByteArray      // 8 bytes
        val raw: ByteArray        // 32 bytes

        init {
            val d = try {
                Base64.getDecoder().decode(base64.trim())
            } catch (e: IllegalArgumentException) {
                throw VerifyException.Malformed("public key")
            }
            if (d.size != 42 || d[0] != 'E'.code.toByte() || d[1] != 'd'.code.toByte()) {
                throw VerifyException.Malformed("public key")
            }
            keyId = d.copyOfRange(2, 10)
            raw = d.copyOfRange(10, 42)
        }
    }

    data class Signature(
        /** "ED": the signature is over BLAKE2b-512 of the file (minisign's default); "Ed": the raw file. */
        val prehashed: Boolean,
        val keyId: ByteArray,
        val signature: ByteArray,          // 64 bytes
        val trustedComment: String,
        val globalSignature: ByteArray,    // 64 bytes, over (signature ‖ trustedComment)
    )

    /** Parses a `.minisig` file. Throws [VerifyException.Malformed] on anything unexpected. */
    fun parse(text: String): Signature {
        val lines = text.split('\n', '\r').map { it.trim() }.filter { it.isNotEmpty() }
        if (lines.size < 4) throw VerifyException.Malformed("expected 4 lines, got ${lines.size}")
        val blob = try {
            Base64.getDecoder().decode(lines[1])
        } catch (e: IllegalArgumentException) {
            throw VerifyException.Malformed("signature line")
        }
        if (blob.size != 74) throw VerifyException.Malformed("signature line")
        val alg = String(blob, 0, 2, Charsets.US_ASCII)
        if (alg != "ED" && alg != "Ed") throw VerifyException.Malformed("algorithm “$alg”")
        val prefix = "trusted comment:"
        if (!lines[2].lowercase().startsWith(prefix)) throw VerifyException.Malformed("trusted comment line")
        val comment = lines[2].substring(prefix.length).trim()
        val global = try {
            Base64.getDecoder().decode(lines[3])
        } catch (e: IllegalArgumentException) {
            throw VerifyException.Malformed("global signature")
        }
        if (global.size != 64) throw VerifyException.Malformed("global signature")
        return Signature(
            prehashed = alg == "ED",
            keyId = blob.copyOfRange(2, 10),
            signature = blob.copyOfRange(10, 74),
            trustedComment = comment,
            globalSignature = global,
        )
    }

    /**
     * Verifies [minisig] over [manifest] with the suite key, failing closed on any problem. When
     * [expectedTrustedComment] is given (releases are signed with `<repo> <tag>`), the signed comment must
     * match it exactly, binding the manifest to that one release.
     */
    @Throws(VerifyException::class)
    fun verify(
        manifest: ByteArray,
        minisig: String,
        expectedTrustedComment: String?,
        publicKeyBase64: String = SUITE_PUBLIC_KEY,
    ) {
        val key = PublicKey(publicKeyBase64)
        val sig = parse(minisig)
        if (!sig.keyId.contentEquals(key.keyId)) throw VerifyException.UnknownKey()

        // 1. The trusted comment is itself signed (over signature ‖ comment). Check it before using the
        //    comment for anything, so a tampered comment can't steer the release-binding check.
        val globalMessage = sig.signature + sig.trustedComment.toByteArray(Charsets.UTF_8)
        if (!ed25519Verify(key.raw, globalMessage, sig.globalSignature)) {
            throw VerifyException.BadGlobalSignature()
        }
        // 2. The manifest signature — over BLAKE2b-512(manifest) for "ED", the raw bytes for legacy "Ed".
        val message = if (sig.prehashed) Blake2b.hash512(manifest) else manifest
        if (!ed25519Verify(key.raw, message, sig.signature)) throw VerifyException.BadSignature()
        // 3. Bind the manifest to the release being installed.
        if (expectedTrustedComment != null && sig.trustedComment != expectedTrustedComment) {
            throw VerifyException.CommentMismatch(expectedTrustedComment, sig.trustedComment)
        }
    }

    private fun ed25519Verify(publicKeyRaw: ByteArray, message: ByteArray, signature: ByteArray): Boolean =
        try {
            val spec = EdDSANamedCurveTable.getByName(EdDSANamedCurveTable.ED_25519)
            val key = EdDSAPublicKey(EdDSAPublicKeySpec(publicKeyRaw, spec))
            val engine = EdDSAEngine(MessageDigest.getInstance(spec.hashAlgorithm))
            engine.initVerify(key)
            engine.update(message)
            engine.verify(signature)
        } catch (e: Exception) {
            false
        }
}
