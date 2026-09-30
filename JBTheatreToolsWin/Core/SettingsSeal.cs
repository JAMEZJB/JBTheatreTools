using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace JBTheatreTools;

/// <summary>Passphrase sealing for the passwords part of a settings backup (<c>.jbtt-settings</c>) — the same construction
/// the suite's apps use, so each side reads what the other writes:
/// <code>
///   key(64)  = PBKDF2-HMAC-SHA256(NFC passphrase, salt, 600 000 rounds)
///   enc, mac = key[0..32], key[32..64]
///   stream   = HMAC-SHA256(enc, nonce ‖ counter as 8-byte big-endian), counter 0, 1, 2 … (32 bytes per block)
///   ct       = plaintext XOR stream
///   tag      = HMAC-SHA256(mac, "jbtt-seal-v1" ‖ len(aad) as 8-byte big-endian ‖ aad ‖ nonce ‖ ct)
/// </code>
/// The tag is checked (constant time) before anything is decrypted. The apps may use scrypt for the key step instead; the
/// launcher only writes PBKDF2 and reports a scrypt box as "made by a newer app".</summary>
public static class SettingsSeal
{
    public const string Algorithm = "hmac-sha256-ctr+hmac-sha256";
    public const string Pbkdf2Name = "pbkdf2-sha256";
    public const int Pbkdf2Rounds = 600_000;
    public const int MinRounds = 100_000, MaxRounds = 5_000_000;   // what a file may ask for (a hostile one can't hang us)
    private static readonly byte[] TagPrefix = Encoding.ASCII.GetBytes("jbtt-seal-v1");

    public enum Failure { WrongPassphrase, Damaged, Unsupported }

    public sealed class SealException : Exception
    {
        public Failure Kind { get; }
        public SealException(Failure kind) : base(kind switch
        {
            Failure.WrongPassphrase => "That passphrase doesn't open this backup.",
            Failure.Damaged => "The backup's password section is damaged.",
            _ => "The backup's password section was made by a newer app.",
        }) { Kind = kind; }
    }

    /// <summary>Unicode NFC, then UTF-8 — "é" typed either way opens the same file.</summary>
    public static byte[] Normalized(string passphrase) =>
        Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC));

    public static byte[] DeriveKey(string passphrase, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Normalized(passphrase), salt, rounds, HashAlgorithmName.SHA256, 64);

    public static byte[] Stream(byte[] key, byte[] nonce, int count)
    {
        var output = new byte[count];
        var block = new byte[nonce.Length + 8];
        nonce.CopyTo(block, 0);
        int written = 0;
        for (ulong counter = 0; written < count; counter++)
        {
            BinaryPrimitives.WriteUInt64BigEndian(block.AsSpan(nonce.Length), counter);
            var chunk = HMACSHA256.HashData(key, block);
            int n = Math.Min(chunk.Length, count - written);
            Array.Copy(chunk, 0, output, written, n);
            written += n;
        }
        return output;
    }

    public static byte[] Tag(byte[] macKey, byte[] aad, byte[] nonce, byte[] ct)
    {
        var msg = new byte[TagPrefix.Length + 8 + aad.Length + nonce.Length + ct.Length];
        int o = 0;
        TagPrefix.CopyTo(msg, o); o += TagPrefix.Length;
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(o), (ulong)aad.Length); o += 8;
        aad.CopyTo(msg, o); o += aad.Length;
        nonce.CopyTo(msg, o); o += nonce.Length;
        ct.CopyTo(msg, o);
        return HMACSHA256.HashData(macKey, msg);
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var r = new byte[a.Length];
        for (int i = 0; i < a.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
        return r;
    }

    /// <summary>The sealed box as the file stores it (without <c>protected</c>/<c>count</c>, which the caller adds).
    /// <paramref name="salt"/>, <paramref name="nonce"/> and <paramref name="rounds"/> are for the known-answer tests.</summary>
    public static JsonObject Seal(byte[] plaintext, string passphrase, byte[] aad,
                                  byte[]? salt = null, byte[]? nonce = null, int rounds = Pbkdf2Rounds)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("Enter a passphrase.", nameof(passphrase));
        salt ??= RandomNumberGenerator.GetBytes(16);
        nonce ??= RandomNumberGenerator.GetBytes(16);
        var key = DeriveKey(passphrase, salt, rounds);
        var ct = Xor(plaintext, Stream(key[..32], nonce, plaintext.Length));
        return new JsonObject
        {
            ["alg"] = Algorithm,
            ["kdf"] = new JsonObject { ["name"] = Pbkdf2Name, ["iterations"] = rounds, ["salt"] = Convert.ToBase64String(salt) },
            ["nonce"] = Convert.ToBase64String(nonce),
            ["ct"] = Convert.ToBase64String(ct),
            ["tag"] = Convert.ToBase64String(Tag(key[32..], aad, nonce, ct)),
        };
    }

    public static byte[] Unseal(JsonObject box, string passphrase, byte[] aad)
    {
        if (Str(box["alg"]) != Algorithm || box["kdf"] is not JsonObject kdf) throw new SealException(Failure.Unsupported);
        if (Str(kdf["name"]) != Pbkdf2Name) throw new SealException(Failure.Unsupported);
        int rounds;
        try { rounds = kdf["iterations"]?.GetValue<int>() ?? 0; }
        catch (Exception) { throw new SealException(Failure.Unsupported); }
        if (rounds < MinRounds || rounds > MaxRounds) throw new SealException(Failure.Unsupported);
        byte[] salt = B64(kdf["salt"]), nonce = B64(box["nonce"]), ct = B64(box["ct"]), given = B64(box["tag"]);
        var key = DeriveKey(passphrase ?? "", salt, rounds);
        if (!CryptographicOperations.FixedTimeEquals(Tag(key[32..], aad, nonce, ct), given))
            throw new SealException(Failure.WrongPassphrase);
        return Xor(ct, Stream(key[..32], nonce, ct.Length));
    }

    private static string? Str(JsonNode? n)
    {
        try { return n?.GetValue<string>(); } catch (Exception) { return null; }
    }

    private static byte[] B64(JsonNode? n)
    {
        var s = Str(n) ?? throw new SealException(Failure.Damaged);
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw new SealException(Failure.Damaged); }
    }
}
