using System.Text;
using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>Signature verification against the REAL signed manifest from the public JBTheatreTools v1.15.0
/// release (Fixtures/), signed offline by the controller with the suite key — so these prove interoperability
/// with the actual signing side, not just self-consistency.</summary>
public class MinisignTests
{
    private const string Release = "JBTheatreTools v1.15.0";
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static byte[] Manifest => File.ReadAllBytes(Fixture("SHA256SUMS"));
    private static string Minisig => File.ReadAllText(Fixture("SHA256SUMS.minisig"));

    [Fact]
    public void RealReleaseManifestVerifies()
    {
        Assert.Equal(287, Manifest.Length);
        Minisign.Verify(Manifest, Minisig, Release);   // no throw = verified
    }

    [Fact]
    public void ParseExtractsFields()
    {
        var sig = Minisign.Parse(Minisig);
        Assert.True(sig.Prehashed, "controller signs in minisign's default \"ED\" mode (Ed25519 over BLAKE2b-512)");
        Assert.Equal(Release, sig.TrustedComment);
        Assert.Equal(64, sig.Sig.Length);
        Assert.Equal(64, sig.GlobalSig.Length);
        Assert.Equal("FC6699A439B42178", Convert.ToHexString(sig.KeyId.Reverse().ToArray()));   // little-endian key id
    }

    [Fact]
    public void TamperedManifestFails()
    {
        var tampered = Manifest.Append((byte)'x').ToArray();
        var ex = Assert.Throws<Minisign.VerifyException>(() => Minisign.Verify(tampered, Minisig, Release));
        Assert.Equal(Minisign.Failure.BadSignature, ex.Kind);
    }

    [Fact]
    public void ManifestIsBoundToItsOwnRelease()
    {
        // A validly-signed manifest from v1.15.0 must not be accepted for another release (replay).
        var ex = Assert.Throws<Minisign.VerifyException>(() => Minisign.Verify(Manifest, Minisig, "JBTheatreTools v1.14.0"));
        Assert.Equal(Minisign.Failure.CommentMismatch, ex.Kind);
    }

    [Fact]
    public void TamperedTrustedCommentFails()
    {
        // Editing the comment to claim another release breaks the GLOBAL signature (checked first).
        var forged = Minisig.Replace("v1.15.0", "v1.15.1");
        var ex = Assert.Throws<Minisign.VerifyException>(() => Minisign.Verify(Manifest, forged, "JBTheatreTools v1.15.1"));
        Assert.Equal(Minisign.Failure.BadGlobalSignature, ex.Kind);
    }

    [Fact]
    public void DifferentKeyIdIsRejected()
    {
        var raw = Convert.FromBase64String(Minisign.SuitePublicKey);
        Array.Clear(raw, 2, 8);   // same key bytes, different key id
        var ex = Assert.Throws<Minisign.VerifyException>(() => Minisign.Verify(Manifest, Minisig, Release, Convert.ToBase64String(raw)));
        Assert.Equal(Minisign.Failure.UnknownKey, ex.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a\nb\nc")]
    [InlineData("untrusted comment: x\nbm90YmFzZTY0\ntrusted comment: y\nzz")]
    public void MalformedInputsFailClosed(string bad)
    {
        var ex = Assert.Throws<Minisign.VerifyException>(() => Minisign.Verify(Manifest, bad, Release));
        Assert.Equal(Minisign.Failure.Malformed, ex.Kind);
    }

    [Fact]
    public void Blake2b512KnownAnswers()
    {
        Assert.Equal("786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce",
            Convert.ToHexString(Minisign.Blake2b512(Array.Empty<byte>())).ToLowerInvariant());
        Assert.Equal("ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            Convert.ToHexString(Minisign.Blake2b512(Encoding.ASCII.GetBytes("abc"))).ToLowerInvariant());
    }
}
