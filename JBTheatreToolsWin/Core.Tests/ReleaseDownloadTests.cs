using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>--download's fetch-and-verify step. Every release here is synthetic, signed in-process by a throwaway Ed25519
/// key in minisign's "ED" format (the suite key never appears), and the fetch writes bytes from memory: no network.</summary>
public sealed class ReleaseDownloadTests : IDisposable
{
    private readonly string dir = Path.Combine(Environment.GetEnvironmentVariable("JBTT_TEST_ROOT") ?? Path.GetTempPath(),
        "download-test-" + Guid.NewGuid().ToString("N"));
    private readonly Ed25519PrivateKeyParameters key;
    private readonly byte[] keyId = { 1, 2, 3, 4, 5, 6, 7, 8 };
    private const string Asset = "Demo-Windows-x64.exe";
    private readonly byte[] payload = Encoding.UTF8.GetBytes("synthetic windows build");

    public ReleaseDownloadTests()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        key = (Ed25519PrivateKeyParameters)gen.GenerateKeyPair().Private;
    }

    public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

    private string PublicKey => Convert.ToBase64String("Ed"u8.ToArray().Concat(keyId).Concat(key.GeneratePublicKey().GetEncoded()).ToArray());

    private byte[] Sign(byte[] message)
    {
        var s = new Ed25519Signer();
        s.Init(true, key);
        s.BlockUpdate(message, 0, message.Length);
        return s.GenerateSignature();
    }

    private string Minisig(byte[] manifest, string comment)
    {
        var sig = Sign(Minisign.Blake2b512(manifest));
        var global = Sign(sig.Concat(Encoding.UTF8.GetBytes(comment)).ToArray());
        return $"untrusted comment: test\n{Convert.ToBase64String("ED"u8.ToArray().Concat(keyId).Concat(sig).ToArray())}\n" +
               $"trusted comment: {comment}\n{Convert.ToBase64String(global)}\n";
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private Dictionary<string, byte[]> Release(Dictionary<string, byte[]>? listed = null, string comment = "Demo v1.2.0", byte[]? served = null)
    {
        listed ??= new() { [Asset] = payload, ["Demo-macOS.zip"] = "mac"u8.ToArray() };
        var manifest = Encoding.UTF8.GetBytes(string.Concat(listed.OrderBy(kv => kv.Key).Select(kv => $"{Hex(kv.Value)}  {kv.Key}\n")));
        return new() { [Asset] = served ?? payload, ["SHA256SUMS"] = manifest, ["SHA256SUMS.minisig"] = Encoding.UTF8.GetBytes(Minisig(manifest, comment)) };
    }

    private Task<ReleaseDownload.Result> Run(Dictionary<string, byte[]> files, long? size = null, string? publicKey = "") =>
        ReleaseDownload.RunAsync(dir, Asset, size ?? payload.Length, "Demo", "v1.2.0", (name, dest) =>
        {
            if (!files.TryGetValue(name, out var data)) throw new HttpRequestException($"no {name}");
            File.WriteAllBytes(dest, data);
            return Task.CompletedTask;
        }, publicKey == "" ? PublicKey : publicKey);

    private string[] FolderNames() =>
        Directory.Exists(dir) ? Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()! : Array.Empty<string>();

    private async Task AssertRejected(Dictionary<string, byte[]> files, long? size = null, string? publicKey = "")
    {
        await Assert.ThrowsAsync<InstallVerificationException>(() => Run(files, size, publicKey));
        Assert.Empty(FolderNames());   // no partial file, no staging folder
    }

    [Fact]
    public async Task DownloadsAndVerifiesTheThreeFiles()
    {
        var r = await Run(Release());
        Assert.Equal(new[] { "Demo-Windows-x64.exe", "SHA256SUMS", "SHA256SUMS.minisig" }, FolderNames());
        Assert.Equal(payload, File.ReadAllBytes(r.Path));
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "SHA256SUMS"), r.Sums);
        Assert.Equal(Path.Combine(Path.GetFullPath(dir), "SHA256SUMS.minisig"), r.Sig);
        Assert.Equal(payload.Length, r.Size);
        Assert.Equal(Hex(payload), r.Sha256);
        // What lands in the folder passes --install-file's check as it stands.
        InstallManager.VerifyLocalFile(r.Path, Asset, r.Sums, "Demo", "v1.2.0", PublicKey);
    }

    [Fact]
    public Task SignatureForAnotherReleaseIsRefused() => AssertRejected(Release(comment: "Demo v1.1.0"));

    [Fact]
    public Task TamperedBuildIsRefused() => AssertRejected(Release(served: "tampered build!!!!!!!!!"u8.ToArray()));   // same size

    [Fact]
    public Task UnlistedBuildIsRefused() => AssertRejected(Release(listed: new() { ["Demo-macOS.zip"] = "mac"u8.ToArray() }));

    [Fact]
    public Task SuiteKeyRefusesATestSignature() => AssertRejected(Release(), publicKey: null);

    [Fact]
    public Task SizeOtherThanTheReleaseListsIsRefused() => AssertRejected(Release(), size: 9999);

    [Fact]
    public async Task FailedFetchLeavesNothingAndKeepsWhatWasThere()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SHA256SUMS"), "older, unrelated");
        var files = Release();
        files.Remove("SHA256SUMS.minisig");   // the third download fails
        await Assert.ThrowsAsync<HttpRequestException>(() => Run(files));
        Assert.Equal(new[] { "SHA256SUMS" }, FolderNames());
        Assert.Equal("older, unrelated", File.ReadAllText(Path.Combine(dir, "SHA256SUMS")));
    }

    [Fact]
    public async Task ReplacesAnEarlierCopy()
    {
        Directory.CreateDirectory(dir);
        foreach (var n in new[] { Asset, "SHA256SUMS", "SHA256SUMS.minisig" }) File.WriteAllText(Path.Combine(dir, n), "old");
        var r = await Run(Release());
        Assert.Equal(payload, File.ReadAllBytes(r.Path));
        Assert.Equal(new[] { "Demo-Windows-x64.exe", "SHA256SUMS", "SHA256SUMS.minisig" }, FolderNames());
    }

    [Theory]
    [InlineData("")]
    [InlineData("../x.exe")]
    [InlineData("a/b.exe")]
    [InlineData("..\\x.exe")]
    [InlineData(".hidden")]
    [InlineData("SHA256SUMS")]
    [InlineData("SHA256SUMS.minisig")]
    [InlineData("two words.exe")]
    public void AssetNameMustBeAPlainFileName(string bad) => Assert.False(ReleaseDownload.IsPlainAssetName(bad));

    [Theory]
    [InlineData("ShowDashboard-Windows-x64.exe")]
    [InlineData("Stagehand-macOS.zip")]
    [InlineData("PDFTools-Full-Windows-arm64.exe")]
    public void PlainAssetNamesPass(string good) => Assert.True(ReleaseDownload.IsPlainAssetName(good));
}
