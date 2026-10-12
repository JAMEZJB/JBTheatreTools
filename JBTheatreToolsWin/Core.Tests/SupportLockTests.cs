using Xunit;

// A hold belongs to the thread that took it (as in the launcher, where it never spans an await), so these tests block
// on the test thread rather than awaiting.
#pragma warning disable xUnit1031

namespace JBTheatreTools.Tests;

/// <summary>Several launcher processes share one support folder on a PC (the window, a command-line install, one
/// started remotely by Stagehand). Two managers on the same folder stand in for two processes: each has its own lock
/// file handle, as separate processes would.</summary>
public sealed class SupportLockTests : IDisposable
{
    private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("JBTT_TEST_ROOT") ?? Path.GetTempPath(),
        "lock-test-" + Guid.NewGuid().ToString("N"));

    public SupportLockTests() { Shortcuts.Succeed = true; }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private static CatalogApp App(string id) => new() { Id = id, Name = id };

    private static string Exe(InstallManager m, string text)
    {
        string file = Path.Combine(m.CacheDir, Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(file, text);
        return file;
    }

    [Fact]
    public void TwoProcessesInstallingAtOnceKeepEveryRecord()
    {
        // Without a shared lock each read-modify-write of installed.json could drop the other's new record.
        var a = new InstallManager(root);
        var b = new InstallManager(root);
        var ids = Enumerable.Range(0, 12).Select(i => $"app{i}").ToList();
        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, (id, _, i) =>
        {
            var m = i % 2 == 0 ? a : b;
            m.Install(App(id), "v1", Exe(m, id), $"{id}.exe", false);
        });
        var manifest = new InstallManager(root).Manifest(strict: true);
        Assert.All(ids, id => Assert.True(manifest.ContainsKey(id), $"{id} lost from installed.json"));
        Assert.All(ids, id => Assert.True(File.Exists(manifest[id].Path)));
    }

    [Fact]
    public void AnInstallWaitsForAnotherProcessHoldingTheLock()
    {
        var a = new InstallManager(root);
        var b = new InstallManager(root);
        var held = a.Lock.Hold();
        var install = Task.Run(() => b.Install(App("late"), "v1", Exe(b, "late"), "late.exe", false));
        Assert.False(install.Wait(400), "the install ran while another process held the lock");
        Assert.False(File.Exists(b.ManifestPath) && b.Manifest().ContainsKey("late"));
        held.Dispose();
        Assert.True(install.Wait(10_000));
        Assert.True(new InstallManager(root).Manifest().ContainsKey("late"));
    }

    [Fact]
    public void ABusyLockGivesUpWithBusyAfterItsWait()
    {
        var a = new SupportLock(root);
        var b = new SupportLock(root);
        using (a.Hold())
            Assert.Throws<SupportLock.BusyException>(() => b.Hold(TimeSpan.FromMilliseconds(200)));
        using (b.Hold(TimeSpan.FromMilliseconds(200))) { }   // free again once released
    }

    [Fact]
    public void TheLockIsReentrantOnItsThread()
    {
        var a = new SupportLock(root);
        using (a.Hold())
        using (a.Hold(TimeSpan.Zero)) { }   // the commit path syncs connectors under the install's hold
        var other = new SupportLock(root);
        using (other.Hold(TimeSpan.FromMilliseconds(200))) { }   // and the outer release really released it
    }

    [Fact]
    public void ATakenLockStaysWithItsThread()
    {
        var a = new SupportLock(root);
        using var held = a.Hold();
        // Another thread of the same process waits too (it would otherwise interleave a manifest write).
        var t = Task.Run(() => { using (a.Hold(TimeSpan.FromMilliseconds(200))) { } });
        Assert.False(t.Wait(400));
        held.Dispose();
        Assert.True(t.Wait(5_000));
    }
}

/// <summary>--install-file: a build copied onto a PC is checked with the release's own signed SHA256SUMS, offline.
/// Uses the REAL signed manifest of the public JBTheatreTools v1.15.0 release (Fixtures/).</summary>
public sealed class LocalFileVerifyTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "local-verify-" + Guid.NewGuid().ToString("N"));
    private const string Asset = "JBTheatreTools-Windows-x64.exe";

    public LocalFileVerifyTests()
    {
        Directory.CreateDirectory(dir);
        foreach (var f in new[] { "SHA256SUMS", "SHA256SUMS.minisig" })
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", f), Path.Combine(dir, f));
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private string Sums => Path.Combine(dir, "SHA256SUMS");

    private string File_(string name, string text = "not the real build")
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void AFileThatIsNotTheSignedBuildIsRefused()
    {
        var ex = Assert.Throws<InstallVerificationException>(() =>
            InstallManager.VerifyLocalFile(File_(Asset), Asset, Sums, "JBTheatreTools", "v1.15.0"));
        Assert.Contains("Checksum mismatch", ex.Message);
    }

    [Fact]
    public void SumsSignedForAnotherReleaseAreRefused()
    {
        // The signature names "JBTheatreTools v1.15.0": the same files can't vouch for another tag or app.
        Assert.Throws<InstallVerificationException>(() =>
            InstallManager.VerifyLocalFile(File_(Asset), Asset, Sums, "JBTheatreTools", "v1.16.0"));
        Assert.Throws<InstallVerificationException>(() =>
            InstallManager.VerifyLocalFile(File_(Asset), Asset, Sums, "ShowDashboard", "v1.15.0"));
    }

    [Fact]
    public void UnsignedOrAlteredSumsAreRefused()
    {
        File.Delete(Sums + ".minisig");
        var ex = Assert.Throws<InstallVerificationException>(() =>
            InstallManager.VerifyLocalFile(File_(Asset), Asset, Sums, "JBTheatreTools", "v1.15.0"));
        Assert.Contains("signature", ex.Message);

        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "SHA256SUMS.minisig"), Sums + ".minisig");
        File.AppendAllText(Sums, "0000  extra.exe\n");
        Assert.Throws<InstallVerificationException>(() =>
            InstallManager.VerifyLocalFile(File_(Asset), Asset, Sums, "JBTheatreTools", "v1.15.0"));
    }

    [Fact]
    public void MissingSumsOrAnUnlistedAssetAreRefused()
    {
        Assert.Contains("isn't listed", Assert.Throws<InstallVerificationException>(() =>
            InstallManager.VerifyLocalFile(File_("Other.exe"), "Other.exe", Sums, "JBTheatreTools", "v1.15.0")).Message);
        Assert.Contains("No SHA256SUMS", Assert.Throws<InstallVerificationException>(() =>
            InstallManager.VerifyLocalFile(File_(Asset), Asset, Path.Combine(dir, "missing"), "JBTheatreTools", "v1.15.0")).Message);
    }
}
