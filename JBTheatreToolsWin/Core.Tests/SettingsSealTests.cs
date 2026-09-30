using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>The passwords part of a settings backup must be byte-for-byte what the suite's apps write and read, so these
/// check the launcher's PBKDF2 seal against the shared known-answer vectors (Fixtures/settings-seal-vectors.json).</summary>
public class SettingsSealTests
{
    private static JsonObject Vector(string kdfName)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "settings-seal-vectors.json")))!;
        return root["vectors"]!.AsArray().Select(v => v!.AsObject())
            .First(v => v["kdf"]!["name"]!.GetValue<string>() == kdfName);
    }

    private static JsonObject BoxFrom(JsonObject v)
    {
        var kdf = v["kdf"]!.DeepClone().AsObject();
        kdf["salt"] = Convert.ToBase64String(Convert.FromHexString(v["salt_hex"]!.GetValue<string>()));
        return new JsonObject
        {
            ["alg"] = SettingsSeal.Algorithm, ["kdf"] = kdf,
            ["nonce"] = Convert.ToBase64String(Convert.FromHexString(v["nonce_hex"]!.GetValue<string>())),
            ["ct"] = v["ct_b64"]!.GetValue<string>(), ["tag"] = v["tag_b64"]!.GetValue<string>(),
        };
    }

    private static byte[] Utf8(JsonNode? n) => Encoding.UTF8.GetBytes(n!.GetValue<string>());

    [Fact]
    public void ReproducesThePbkdf2VectorExactly()
    {
        var v = Vector("pbkdf2-sha256");
        int rounds = v["kdf"]!["iterations"]!.GetValue<int>();
        Assert.Equal(SettingsSeal.Pbkdf2Rounds, rounds);
        var box = SettingsSeal.Seal(Utf8(v["plaintext"]), v["passphrase"]!.GetValue<string>(), Utf8(v["aad"]),
            Convert.FromHexString(v["salt_hex"]!.GetValue<string>()), Convert.FromHexString(v["nonce_hex"]!.GetValue<string>()),
            rounds);
        Assert.Equal(v["ct_b64"]!.GetValue<string>(), box["ct"]!.GetValue<string>());
        Assert.Equal(v["tag_b64"]!.GetValue<string>(), box["tag"]!.GetValue<string>());
        Assert.Equal("hmac-sha256-ctr+hmac-sha256", box["alg"]!.GetValue<string>());
        Assert.Equal("pbkdf2-sha256", box["kdf"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void OpensTheVectorAndRefusesAWrongPassphrase()
    {
        var v = Vector("pbkdf2-sha256");
        var box = BoxFrom(v);
        Assert.Equal(Utf8(v["plaintext"]), SettingsSeal.Unseal(box, v["passphrase"]!.GetValue<string>(), Utf8(v["aad"])));
        var e = Assert.Throws<SettingsSeal.SealException>(() => SettingsSeal.Unseal(box, "correct horse battery!", Utf8(v["aad"])));
        Assert.Equal(SettingsSeal.Failure.WrongPassphrase, e.Kind);
        // A backup for another app (different aad) doesn't open either.
        Assert.Throws<SettingsSeal.SealException>(() =>
            SettingsSeal.Unseal(box, v["passphrase"]!.GetValue<string>(), Encoding.UTF8.GetBytes("jbtt-settings|1|psntools")));
    }

    [Fact]
    public void TamperedCiphertextIsRefused()
    {
        var aad = Encoding.UTF8.GetBytes("a");
        var box = SettingsSeal.Seal(Encoding.UTF8.GetBytes("secret"), "pw", aad, rounds: 100_000);
        var ct = Convert.FromBase64String(box["ct"]!.GetValue<string>());
        ct[0] ^= 1;
        box["ct"] = Convert.ToBase64String(ct);
        var e = Assert.Throws<SettingsSeal.SealException>(() => SettingsSeal.Unseal(box, "pw", aad));
        Assert.Equal(SettingsSeal.Failure.WrongPassphrase, e.Kind);
    }

    [Fact]
    public void ScryptBoxesAreReportedAsNewer()
    {
        var v = Vector("scrypt");
        var e = Assert.Throws<SettingsSeal.SealException>(() =>
            SettingsSeal.Unseal(BoxFrom(v), v["passphrase"]!.GetValue<string>(), Utf8(v["aad"])));
        Assert.Equal(SettingsSeal.Failure.Unsupported, e.Kind);
    }

    [Fact]
    public void HostileRoundCountsAreRefused()
    {
        var box = new JsonObject
        {
            ["alg"] = SettingsSeal.Algorithm,
            ["kdf"] = new JsonObject { ["name"] = "pbkdf2-sha256", ["iterations"] = 50_000_000, ["salt"] = "AAAA" },
            ["nonce"] = "AAAA", ["ct"] = "", ["tag"] = "",
        };
        var e = Assert.Throws<SettingsSeal.SealException>(() => SettingsSeal.Unseal(box, "x", Array.Empty<byte>()));
        Assert.Equal(SettingsSeal.Failure.Unsupported, e.Kind);
    }

    [Fact]
    public void PassphraseIsNfcNormalised()
    {
        string composed = "café", decomposed = "café";
        Assert.Equal(SettingsSeal.Normalized(composed), SettingsSeal.Normalized(decomposed));
        var box = SettingsSeal.Seal(Encoding.UTF8.GetBytes("x"), composed, Array.Empty<byte>(), rounds: 100_000);
        Assert.Equal(Encoding.UTF8.GetBytes("x"), SettingsSeal.Unseal(box, decomposed, Array.Empty<byte>()));
    }

    [Fact]
    public void EmptyPassphraseIsRefused() =>
        Assert.Throws<ArgumentException>(() => SettingsSeal.Seal(new byte[] { 1 }, "", Array.Empty<byte>()));
}
