using Xunit;

namespace JBTheatreTools.Tests;

/// <summary>"Is this app open?" before an update or removal: a process started through the connector entry point
/// (bin\&lt;id&gt;, a folder link to the installed version's folder; Stagehand and Claude start apps that way) names that
/// path as its image, and must still count as the installed app running.</summary>
public sealed class RunningAppTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "running-" + Guid.NewGuid().ToString("N"));
    private readonly string current, older, exe;

    public RunningAppTests()
    {
        current = Path.Combine(root, "apps", "sd", "sd-cur");
        older = Path.Combine(root, "apps", "sd", "sd-old");
        Directory.CreateDirectory(current);
        Directory.CreateDirectory(older);
        exe = Path.Combine(current, "SD.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(Path.Combine(older, "SD.exe"), "x");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private string Link(string name, string target)
    {
        var link = Path.Combine(root, "bin", name);
        Directory.CreateSymbolicLink(link, target);
        return link;
    }

    [Fact]
    public void TheInstalledExeItselfCounts() => Assert.True(InstallManager.RunsInstalledExe(exe, exe));

    [Fact]
    public void StartedThroughTheEntryPointCounts()
    {
        var link = Link("sd", current);
        Assert.True(InstallManager.RunsInstalledExe(Path.Combine(link, "SD.exe"), exe));
    }

    [Fact]
    public void AnotherVersionOrProgramDoesNot()
    {
        var link = Link("sd", older);
        Assert.False(InstallManager.RunsInstalledExe(Path.Combine(link, "SD.exe"), exe));          // links elsewhere
        Assert.False(InstallManager.RunsInstalledExe(Path.Combine(current, "Other.exe"), exe));     // another program
        Assert.False(InstallManager.RunsInstalledExe(Path.Combine(older, "SD.exe"), exe));          // a plain folder
    }
}
