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

    public void Dispose() => Directory.Delete(root, recursive: true);
}
