using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace JBTheatreTools;

/// <summary>
/// Verifies minisign (Ed25519) signatures on release manifests.
///
/// Why: a <c>SHA256SUMS</c> fetched from the same release, over the same channel, as the payload only proves
/// the download wasn't corrupted in transit — it can't tell a hostile release (or a hostile relay) from a
/// real one. The controller signs every release's <c>SHA256SUMS</c> OFFLINE with the suite's minisign key,
/// and the launcher embeds the public key: a manifest is trusted only if that signature verifies. The key
/// never leaves the controller; this code only ever verifies. (Audit F1.)
///
/// minisign file format (<c>SHA256SUMS.minisig</c>):
///   untrusted comment: (ignored)
///   base64( alg(2) ‖ key id(8) ‖ signature(64) )   alg "ED" = Ed25519 over BLAKE2b-512(file); "Ed" = over the file
///   trusted comment: (text)
///   base64( global signature(64) )                  Ed25519 over ( signature ‖ trusted comment )
///
/// The controller's trusted comment is "&lt;repo&gt; &lt;tag&gt;"; verifying it against the release being installed
/// binds a manifest to ONE release, so a validly-signed manifest from another release can't be replayed.
/// </summary>
public static class Minisign
{
    /// <summary>The suite's minisign public key (key id FC6699A439B42178). Rotating it means a launcher release.</summary>
    public const string SuitePublicKey = "RWR4IbQ5pJlm/OLhkB2EnuKqtxxz9/TKpCRycMpLJlZh5fxqMQDyZUoC";

    public enum Failure { Malformed, UnknownKey, BadSignature, BadGlobalSignature, CommentMismatch }

    public sealed class VerifyException : Exception
    {
        public Failure Kind { get; }
        public VerifyException(Failure kind, string message) : base(message) { Kind = kind; }
    }

    /// <param name="Prehashed">"ED": the signature is over BLAKE2b-512 of the file (minisign's default); "Ed": over the raw file.</param>
    public sealed record Signature(bool Prehashed, byte[] KeyId, byte[] Sig, string TrustedComment, byte[] GlobalSig);

    private sealed record PublicKey(byte[] KeyId, byte[] Raw);

    private static PublicKey ParsePublicKey(string base64)
    {
        byte[] d;
        try { d = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new VerifyException(Failure.Malformed, "The suite public key is malformed."); }
        if (d.Length != 42 || d[0] != (byte)'E' || d[1] != (byte)'d')
            throw new VerifyException(Failure.Malformed, "The suite public key is malformed.");
        return new PublicKey(d[2..10], d[10..42]);
    }

    /// <summary>Parses a <c>.minisig</c> file. Throws <see cref="VerifyException"/> (Malformed) on anything unexpected.</summary>
    public static Signature Parse(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length < 4) throw Bad($"expected 4 lines, got {lines.Length}");
        byte[] blob;
        try { blob = Convert.FromBase64String(lines[1]); } catch (FormatException) { throw Bad("signature line"); }
        if (blob.Length != 74) throw Bad("signature line");
        var alg = Encoding.ASCII.GetString(blob, 0, 2);
        if (alg != "ED" && alg != "Ed") throw Bad($"algorithm \"{alg}\"");
        const string prefix = "trusted comment:";
        if (!lines[2].StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw Bad("trusted comment line");
        var comment = lines[2][prefix.Length..].Trim();
        byte[] global;
        try { global = Convert.FromBase64String(lines[3]); } catch (FormatException) { throw Bad("global signature"); }
        if (global.Length != 64) throw Bad("global signature");
        return new Signature(alg == "ED", blob[2..10], blob[10..74], comment, global);

        static VerifyException Bad(string why) => new(Failure.Malformed, $"The release's signature file is malformed ({why}).");
    }

    /// <summary>
    /// Verifies <paramref name="minisig"/> over <paramref name="manifest"/> with the suite key, failing closed
    /// on any problem. When <paramref name="expectedTrustedComment"/> is given (the controller signs
    /// "&lt;repo&gt; &lt;tag&gt;"), the signed comment must match it exactly, binding the manifest to that one release.
    /// </summary>
    public static void Verify(byte[] manifest, string minisig, string? expectedTrustedComment, string? publicKeyBase64 = null)
    {
        var key = ParsePublicKey(publicKeyBase64 ?? SuitePublicKey);
        var sig = Parse(minisig);
        if (!key.KeyId.SequenceEqual(sig.KeyId))
            throw new VerifyException(Failure.UnknownKey, "The release's manifest was signed with a different key than the suite key.");

        // 1. The trusted comment is itself signed (over signature ‖ comment). Check it before using the
        //    comment for anything, so a tampered comment can't steer the release-binding check.
        var globalMessage = sig.Sig.Concat(Encoding.UTF8.GetBytes(sig.TrustedComment)).ToArray();
        if (!Ed25519Verify(key.Raw, globalMessage, sig.GlobalSig))
            throw new VerifyException(Failure.BadGlobalSignature, "The release's signature comment does not verify.");

        // 2. The manifest signature — over BLAKE2b-512(manifest) for "ED", the raw bytes for legacy "Ed".
        var message = sig.Prehashed ? Blake2b512(manifest) : manifest;
        if (!Ed25519Verify(key.Raw, message, sig.Sig))
            throw new VerifyException(Failure.BadSignature, "The release's manifest signature does not verify.");

        // 3. Bind the manifest to the release being installed.
        if (expectedTrustedComment != null && sig.TrustedComment != expectedTrustedComment)
            throw new VerifyException(Failure.CommentMismatch,
                $"The signed manifest is for “{sig.TrustedComment}”, not “{expectedTrustedComment}”.");
    }

    /// <summary>BLAKE2b-512 (unkeyed, 64-byte digest) — minisign's prehash.</summary>
    public static byte[] Blake2b512(byte[] message)
    {
        var d = new Blake2bDigest(512);
        d.BlockUpdate(message, 0, message.Length);
        var o = new byte[64];
        d.DoFinal(o, 0);
        return o;
    }

    private static bool Ed25519Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (signature.Length != 64) return false;
        var s = new Ed25519Signer();
        s.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        s.BlockUpdate(message, 0, message.Length);
        return s.VerifySignature(signature);
    }
}
