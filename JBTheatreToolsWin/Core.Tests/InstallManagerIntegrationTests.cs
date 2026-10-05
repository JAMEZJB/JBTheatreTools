using System.IO.Compression;
using System.Text.Json;
using Xunit;

namespace JBTheatreTools.Tests;

public sealed class InstallManagerIntegrationTests : IDisposable
{
    private readonly string root = Path.Combine(Environment.GetEnvironmentVariable("JBTT_TEST_ROOT") ?? Path.GetTempPath(),
        "manager-test-" + Guid.NewGuid().ToString("N"));
    private readonly InstallManager manager;
    private readonly CatalogApp app = new()
    {
        Id = "demo", Name = "Demo",
        Variants = new() { new() { Id = "standard", Label = "Standard" }, new() { Id = "full", Label = "Full" } }
    };

    public InstallManagerIntegrationTests()
    {
        manager = new InstallManager(root);
        Shortcuts.Succeed = true; Shortcuts.Start.Clear(); Shortcuts.Desktop.Clear();
    }

    private string Exe(string text = "payload")
    {
        string file = Path.Combine(manager.CacheDir, Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(file, text); return file;
    }

    private string Zip(bool withExe = true)
    {
        string file = Path.Combine(manager.CacheDir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(file, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(withExe ? "Demo Full/Demo Full.exe" : "readme.txt").Open());
        writer.Write("zip payload"); return file;
    }

    [Fact]
    public void ZipExeTransitionsUpdateMetadataAndPreserveSibling()
    {
        string standard = manager.Install(app, "v1", Exe(), "Demo.exe", false);
        string full = manager.Install(app, "v1", Zip(), "Demo.zip", false, "full");
        string replaced = manager.Install(app, "v2", Exe(), "Demo.exe", false, "full");
        Assert.True(File.Exists(standard));
        Assert.False(File.Exists(full));
        Assert.Equal(replaced, manager.InstalledPath("demo@full"));
        Assert.Equal("v2", manager.InstalledVersion("demo@full"));
        Assert.Equal("v1", manager.InstalledVersion("demo"));
        string zipped = manager.Install(app, "v3", Zip(), "Demo.zip", false, "full");
        Assert.False(File.Exists(replaced));
        Assert.Equal(zipped, manager.Manifest()["demo@full"].Path);
        manager.Uninstall("demo@full");
        Assert.False(File.Exists(zipped));
        Assert.True(File.Exists(standard));
    }

    [Fact]
    public void RecordsThePreviousVersionForRollBack()
    {
        manager.Install(app, "v1", Exe(), "Demo.exe", false);
        Assert.Null(manager.Record("demo")!.PreviousVersion);           // a first install replaced nothing
        manager.Install(app, "v2", Exe(), "Demo.exe", false);
        Assert.Equal("v1", manager.Record("demo")!.PreviousVersion);
        manager.Install(app, "2", Exe(), "Demo.exe", false);             // a reinstall of the same version…
        Assert.Equal("v1", manager.Record("demo")!.PreviousVersion);     // …keeps the older one
        manager.Install(app, "v1", Exe(), "Demo.exe", false);            // roll back
        Assert.Equal("2", manager.Record("demo")!.PreviousVersion);      // → rolling forward is one click too
        Assert.Null(manager.Record("demo@full"));
    }

    [Fact]
    public void MeasuresAndClearsTheDownloadCache()
    {
        string installed = manager.Install(app, "v1", Exe("12345"), "Demo.exe", false);
        Assert.Equal(5, manager.SizeOnDisk("demo"));
        Assert.Equal(0, manager.SizeOnDisk("demo@full"));
        Assert.True(manager.InstalledSize() >= 5);
        File.WriteAllText(Path.Combine(manager.CacheDir, "leftover.part"), "0123456789");
        Directory.CreateDirectory(Path.Combine(manager.CacheDir, "extract-x"));
        File.WriteAllText(Path.Combine(manager.CacheDir, "extract-x", "f"), "abc");
        Assert.True(manager.CacheSize() >= 13);
        manager.ClearCache();
        Assert.Equal(0, manager.CacheSize());
        Assert.True(Directory.Exists(manager.CacheDir));
        Assert.True(File.Exists(installed));                              // installs are never in the cache
        Assert.True(manager.FreeSpace() > 0);
        Assert.Equal(0, InstallManager.DirectorySize(Path.Combine(root, "does-not-exist")));
    }

    [Fact]
    public void FailedExtractionPreservesMetadataPayloadAndShortcuts()
    {
        string old = manager.Install(app, "v1", Exe(), "Demo.exe", true);
        string metadata = File.ReadAllText(manager.ManifestPath);
        Assert.Throws<InvalidDataException>(() => manager.Install(app, "v2", Zip(false), "Demo.zip", true));
        Assert.Equal(metadata, File.ReadAllText(manager.ManifestPath));
        Assert.True(File.Exists(old));
        Assert.Equal(old, Shortcuts.Start["Demo"]);
        Assert.Equal(old, Shortcuts.Desktop["Demo"]);
        Assert.Equal("v1", manager.InstalledVersion("demo"));
    }

    [Fact]
    public void CorruptManifestAbortsWithoutReplacingInstalledFiles()
    {
        string old = manager.Install(app, "v1", Exe(), "Demo.exe", false);
        File.WriteAllText(manager.ManifestPath, "not json");
        Assert.Throws<JsonException>(() => manager.Install(app, "v2", Exe(), "Demo.exe", false));
        Assert.Equal("not json", File.ReadAllText(manager.ManifestPath));
        Assert.True(File.Exists(old));
        Assert.Single(Directory.GetDirectories(Path.Combine(manager.AppsDir, app.Id)));
    }

    [Fact]
    public void FailedShortcutUpdateKeepsOldWorkingTarget()
    {
        string old = manager.Install(app, "v1", Exe(), "Demo.exe", true);
        Shortcuts.Succeed = false;
        string next = manager.Install(app, "v2", Exe(), "Demo.exe", true);
        Assert.True(File.Exists(old));
        Assert.Equal(old, Shortcuts.Start["Demo"]);
        Assert.Equal(old, Shortcuts.Desktop["Demo"]);
        Assert.Equal(next, manager.InstalledPath("demo"));
        Assert.Equal("v2", manager.InstalledVersion("demo"));
    }

    [Fact]
    public void ShortcutTogglesDoNotRecordFailedCreations()
    {
        manager.Install(app, "v1", Exe(), "Demo.exe", false);
        Shortcuts.Succeed = false;
        manager.SyncShortcuts("demo", "Demo", true);
        manager.SetDesktopShortcut("demo", "Demo", true);
        manager.SetStartMenuShortcut("demo", "Demo", true);
        Assert.False(manager.HasDesktopShortcut("demo"));
        Assert.False(manager.HasStartMenuShortcut("demo"));
        Assert.True(manager.NeedsShortcutSync("demo", true));
    }

    [Fact]
    public void LegacySingleFileInstallUpgradesWithoutTouchingSibling()
    {
        string directory = Path.Combine(manager.AppsDir, app.Id); Directory.CreateDirectory(directory);
        string legacy = Path.Combine(directory, "Demo.exe"); File.WriteAllText(legacy, "old");
        string sibling = Path.Combine(directory, "Sibling.exe"); File.WriteAllText(sibling, "sibling");
        File.WriteAllText(manager.ManifestPath, JsonSerializer.Serialize(new Dictionary<string, InstalledRecord>
        {
            ["demo"] = new() { Version = "v1", Path = legacy },
            ["demo@full"] = new() { Version = "v1", Path = sibling, Variant = "full" }
        }));
        string next = manager.Install(app, "v2", Zip(), "Demo.zip", false);
        Assert.False(File.Exists(legacy));
        Assert.True(File.Exists(sibling));
        Assert.True(File.Exists(next));
    }

    [Fact]
    public void EmptyExecutableAbortsBeforeMetadataChanges()
    {
        string old = manager.Install(app, "v1", Exe(), "Demo.exe", false);
        string metadata = File.ReadAllText(manager.ManifestPath);
        Assert.Throws<InvalidDataException>(() => manager.Install(app, "v2", Exe(""), "Demo.exe", false));
        Assert.Equal(metadata, File.ReadAllText(manager.ManifestPath));
        Assert.True(File.Exists(old));
    }

    [Fact]
    public void RemovesOldVersionsKeptAsideOnTheSlotsNextInstallOrUninstall()
    {
        manager.Install(app, "v1", Exe(), "Demo.exe", false);
        manager.Install(app, "v1", Exe(), "Demo.exe", false, "full");
        string directory = Path.Combine(manager.AppsDir, app.Id);
        string Aside(string slot, TimeSpan age)
        {
            string d = Path.Combine(directory, slot + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "Demo.exe"), "old");
            Directory.SetCreationTimeUtc(d, DateTime.UtcNow - age);
            return d;
        }
        string stale = Aside("demo", TimeSpan.FromHours(2));
        string recent = Aside("demo", TimeSpan.FromMinutes(1));        // maybe another process's install in flight
        string sibling = Aside("demo@full", TimeSpan.FromHours(2));   // another edition's: only its own slot cleans it
        string current = manager.InstalledPath("demo")!;
        string next = manager.Install(app, "v2", Exe(), "Demo.exe", false);
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(sibling));
        Assert.False(File.Exists(current));                           // the replaced version: removed as before
        Assert.True(File.Exists(next));
        manager.Uninstall("demo@full");
        Assert.False(Directory.Exists(sibling));
        Assert.True(File.Exists(next));
    }

    [Fact]
    public void UninstallPublicationFailurePreservesSlotSiblingShortcutsAndCache()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows replacement sharing refusal.
        string current = manager.Install(app, "v1", Exe(), "Demo.exe", true);
        string sibling = manager.Install(app, "v2", Zip(), "Demo.zip", true, "full");
        byte[] metadata = File.ReadAllBytes(manager.ManifestPath);
        Assert.Equal(current, manager.InstalledPath("demo")); // prime the read caches
        Assert.Equal(sibling, manager.InstalledPath("demo@full"));
        var start = new Dictionary<string, string>(Shortcuts.Start);
        var desktop = new Dictionary<string, string>(Shortcuts.Desktop);
        int events = 0;
        manager.ManifestChanged += () => events++;
        Exception? refusal = null;
        using (var held = new FileStream(manager.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            try { manager.Uninstall("demo"); } catch (Exception exception) { refusal = exception; }
        Assert.True(refusal is IOException or UnauthorizedAccessException);
        Assert.Equal(metadata, File.ReadAllBytes(manager.ManifestPath));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(sibling));
        Assert.Equal(current, manager.InstalledPath("demo"));
        Assert.Equal("v1", manager.InstalledVersion("demo"));
        Assert.Equal(start.OrderBy(p => p.Key), Shortcuts.Start.OrderBy(p => p.Key));
        Assert.Equal(desktop.OrderBy(p => p.Key), Shortcuts.Desktop.OrderBy(p => p.Key));
        Assert.Equal(0, events);
        Assert.Empty(Directory.GetFiles(root, ".manifest-*.tmp"));
    }

    [Fact]
    public void UninstallCorruptManifestRefusesBeforeAnyCleanup()
    {
        string current = manager.Install(app, "v1", Exe(), "Demo.exe", true);
        string sibling = manager.Install(app, "v1", Exe(), "Demo.exe", false, "full");
        Assert.Equal(current, manager.InstalledPath("demo"));
        File.WriteAllText(manager.ManifestPath, "not json");
        int events = 0;
        manager.ManifestChanged += () => events++;
        Assert.Throws<JsonException>(() => manager.Uninstall("demo"));
        Assert.Equal("not json", File.ReadAllText(manager.ManifestPath));
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(sibling));
        Assert.Equal(current, Shortcuts.Start["Demo"]);
        Assert.Equal(current, Shortcuts.Desktop["Demo"]);
        Assert.Equal(current, manager.InstalledPath("demo"));
        Assert.Equal(0, events);
    }

    [Fact]
    public void UninstallUnreadableManifestRefusesBeforeAnyCleanup()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows read sharing refusal.
        string current = manager.Install(app, "v1", Exe(), "Demo.exe", true);
        byte[] metadata = File.ReadAllBytes(manager.ManifestPath);
        Assert.Equal(current, manager.InstalledPath("demo"));
        int events = 0;
        manager.ManifestChanged += () => events++;
        using (var held = new FileStream(manager.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => manager.Uninstall("demo"));
        Assert.Equal(metadata, File.ReadAllBytes(manager.ManifestPath));
        Assert.True(File.Exists(current));
        Assert.Equal(current, Shortcuts.Start["Demo"]);
        Assert.Equal(current, Shortcuts.Desktop["Demo"]);
        Assert.Equal(current, manager.InstalledPath("demo"));
        Assert.Equal(0, events);
    }

    [Fact]
    public void UninstallPublishesRemovalBeforeCleaningPayloadAndShortcuts()
    {
        string current = manager.Install(app, "v1", Zip(), "Demo.zip", true);
        string sibling = manager.Install(app, "v2", Exe(), "Demo.exe", false, "full");
        Assert.Equal(current, manager.InstalledPath("demo"));
        int events = 0;
        bool publicationObservedBeforeCleanup = false;
        manager.ManifestChanged += () =>
        {
            events++;
            Assert.False(manager.Manifest(strict: true).ContainsKey("demo"));
            Assert.Null(manager.InstalledPath("demo"));
            Assert.True(File.Exists(current));
            Assert.Equal(current, Shortcuts.Start["Demo"]);
            Assert.Equal(current, Shortcuts.Desktop["Demo"]);
            publicationObservedBeforeCleanup = true;
        };
        manager.Uninstall("demo");
        // WriteManifest deliberately contains observer exceptions, so also require the
        // observation below rather than relying only on assertions inside its callback.
        Assert.Equal(1, events);
        Assert.True(publicationObservedBeforeCleanup);
        Assert.False(File.Exists(current));
        Assert.True(File.Exists(sibling));
        Assert.Equal(sibling, manager.InstalledPath("demo@full"));
        Assert.False(Shortcuts.Start.ContainsKey("Demo"));
        Assert.False(Shortcuts.Desktop.ContainsKey("Demo"));
    }

    [Fact]
    public void UninstallLegacySingleFilePreservesSibling()
    {
        string directory = Path.Combine(manager.AppsDir, app.Id);
        Directory.CreateDirectory(directory);
        string current = Path.Combine(directory, "Demo.exe");
        string sibling = Path.Combine(directory, "Sibling.exe");
        File.WriteAllText(current, "current"); File.WriteAllText(sibling, "sibling");
        File.WriteAllText(manager.ManifestPath, JsonSerializer.Serialize(new Dictionary<string, InstalledRecord>
        {
            ["demo"] = new() { Version = "v1", Path = current },
            ["demo@full"] = new() { Version = "v2", Path = sibling, Variant = "full" }
        }));
        manager.Uninstall("demo");
        Assert.False(File.Exists(current));
        Assert.True(File.Exists(sibling));
        Assert.Equal("sibling", File.ReadAllText(sibling));
        Assert.Equal(sibling, manager.Manifest(strict: true)["demo@full"].Path);
    }

    [Fact]
    public void UninstallAbsentSlotDoesNotPublishOrRemoveSibling()
    {
        string sibling = manager.Install(app, "v1", Exe(), "Demo.exe", true, "full");
        byte[] metadata = File.ReadAllBytes(manager.ManifestPath);
        int events = 0;
        manager.ManifestChanged += () => events++;
        manager.Uninstall("demo");
        Assert.Equal(metadata, File.ReadAllBytes(manager.ManifestPath));
        Assert.True(File.Exists(sibling));
        Assert.Equal(0, events);
        Assert.Equal(sibling, manager.InstalledPath("demo@full"));
    }

    [Fact]
    public void UninstallRetainsLockedPayloadAfterCommittedRemoval()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows payload deletion sharing refusal.
        string current = manager.Install(app, "v1", Exe(), "Demo.exe", true);
        using (var held = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            manager.Uninstall("demo");
            Assert.False(manager.Manifest(strict: true).ContainsKey("demo"));
            Assert.Null(manager.InstalledPath("demo"));
            Assert.True(File.Exists(current));
            Assert.False(Shortcuts.Start.ContainsKey("Demo"));
            Assert.False(Shortcuts.Desktop.ContainsKey("Demo"));
        }
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
