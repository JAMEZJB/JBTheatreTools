using System.IO.Compression;
using Xunit;

namespace JBTheatreTools.Tests;

public sealed class InstallTransactionTests : IDisposable
{
    private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("JBTT_TEST_ROOT") ?? Path.GetTempPath(), "install-test-" + Guid.NewGuid().ToString("N"));
    private string AppDir => Path.Combine(root, "apps", "demo");
    private string Manifest => Path.Combine(root, "installed.json");
    private string OldExe => Path.Combine(AppDir, "old.exe");

    public InstallTransactionTests()
    {
        Directory.CreateDirectory(AppDir);
        File.WriteAllText(OldExe, "old payload");
        File.WriteAllText(Manifest, "old metadata");
    }

    private string Exe()
    {
        string path = Path.Combine(root, "download.exe");
        File.WriteAllText(path, "new payload");
        return path;
    }

    private string Zip(params string[] entries)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string name in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("new payload");
        }
        return path;
    }

    private void AssertOldIntact()
    {
        Assert.Equal("old payload", File.ReadAllText(OldExe));
        Assert.Equal("old metadata", File.ReadAllText(Manifest));
        Assert.Empty(Directory.GetDirectories(AppDir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuccessfulCommitPublishesNewGenerationBeforeChangingManifest(bool zip)
    {
        string download = zip ? Zip("Demo Full/Demo Full.exe", "Demo Full/_internal/data.bin") : Exe();
        using var oldOpen = new FileStream(OldExe, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = InstallTransaction.Install(AppDir, "demo@full", download,
            zip ? "Demo.zip" : "Demo.exe", "Demo Full", payload =>
            {
                Assert.Equal("old metadata", File.ReadAllText(Manifest));
                Assert.True(File.Exists(OldExe));
                Assert.Equal("new payload", File.ReadAllText(payload.Executable));
                InstallTransaction.WriteManifest(Manifest, payload.Executable);
            });
        Assert.Equal(result.Executable, File.ReadAllText(Manifest));
        Assert.True(File.Exists(download));
        Assert.Single(Directory.GetDirectories(AppDir));
        Assert.DoesNotContain(".install-", result.Directory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommitFailurePreservesPreviousPayloadAndMetadata(bool zip)
    {
        string download = zip ? Zip("Demo.exe") : Exe();
        Assert.Throws<IOException>(() => InstallTransaction.Install(AppDir, "demo", download,
            zip ? "Demo.zip" : "Demo.exe", "Demo", _ => throw new IOException("synthetic commit failure")));
        AssertOldIntact();
        Assert.True(File.Exists(download));
    }

    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("..\\escape.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("C:\\absolute.exe")]
    [InlineData("folder/../../escape.exe")]
    [InlineData("folder/data:stream.exe")]
    [InlineData("folder/NUL.exe")]
    [InlineData("folder/CON.txt")]
    [InlineData("folder/COM1.exe")]
    [InlineData("folder/trailing. /x.exe")]
    public void UnsafeZipPathsCannotTouchPreviousInstall(string entry)
    {
        string download = Zip("Demo.exe", entry);
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Install(AppDir, "demo", download,
            "Demo.zip", "Demo", _ => throw new Exception("must not commit")));
        AssertOldIntact();
        Assert.False(File.Exists(Path.Combine(root, "escape.exe")));
    }

    [Fact]
    public void ZipWithoutExecutablePreservesPreviousInstall()
    {
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Install(AppDir, "demo", Zip("readme.txt"),
            "Demo.zip", "Demo", _ => throw new Exception("must not commit")));
        AssertOldIntact();
    }

    [Fact]
    public void CorruptZipPreservesPreviousInstall()
    {
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Install(AppDir, "demo", Exe(),
            "Demo.zip", "Demo", _ => throw new Exception("must not commit")));
        AssertOldIntact();
    }

    [Fact]
    public void DuplicateCaseInsensitivePathsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Install(AppDir, "demo",
            Zip("Demo.exe", "DEMO.EXE"), "Demo.zip", "Demo", _ => throw new Exception("must not commit")));
        AssertOldIntact();
    }

    [Fact]
    public void ZipSymlinkIsRejected()
    {
        string download = Zip("Demo.exe");
        using (var zip = ZipFile.Open(download, ZipArchiveMode.Update))
            zip.Entries[0].ExternalAttributes = unchecked((int)0xA1FF0000);
        Assert.Throws<InvalidDataException>(() => InstallTransaction.Install(AppDir, "demo", download,
            "Demo.zip", "Demo", _ => throw new Exception("must not commit")));
        AssertOldIntact();
    }

    [Fact]
    public void CancelBeforeInstallDoesNotTouchPreviousFiles()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => InstallTransaction.Install(AppDir, "demo", Exe(),
            "Demo.exe", "Demo", _ => throw new Exception("must not commit"), cancel.Token));
        AssertOldIntact();
    }

    [Fact]
    public void CancellationAfterCommitDoesNotDeletePublishedFiles()
    {
        using var cancel = new CancellationTokenSource();
        var result = InstallTransaction.Install(AppDir, "demo", Exe(), "Demo.exe", "Demo", payload =>
        {
            InstallTransaction.WriteManifest(Manifest, payload.Executable);
            cancel.Cancel();
        }, cancel.Token);
        Assert.True(File.Exists(result.Executable));
        Assert.Equal(result.Executable, File.ReadAllText(Manifest));
    }

    [Fact]
    public void VariantAndExeZipTransitionsUseSeparateGenerations()
    {
        var standard = InstallTransaction.Install(AppDir, "demo", Exe(), "Demo.exe", "Demo", _ => { });
        var full = InstallTransaction.Install(AppDir, "demo@full", Zip("Demo Full.exe"), "Demo.zip", "Demo Full", _ => { });
        var changed = InstallTransaction.Install(AppDir, "demo@full", Exe(), "Demo.exe", "Demo", _ => { });
        Assert.NotEqual(full.Directory, changed.Directory);
        Assert.True(File.Exists(standard.Executable));
        Assert.True(File.Exists(full.Executable));
        Assert.True(File.Exists(changed.Executable));
    }

    [Fact]
    public void FailedManifestReplacementPreservesDestinationAndCleansTemporary()
    {
        string destination = Path.Combine(root, "occupied"); Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "marker"), "unchanged");
        Assert.ThrowsAny<IOException>(() => InstallTransaction.WriteManifest(destination, "new metadata"));
        Assert.Equal("unchanged", File.ReadAllText(Path.Combine(destination, "marker")));
        Assert.Empty(Directory.GetFiles(root, ".manifest-*"));
        Assert.Equal("old metadata", File.ReadAllText(Manifest));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
