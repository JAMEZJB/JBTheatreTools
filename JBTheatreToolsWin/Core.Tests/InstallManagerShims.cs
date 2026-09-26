// The integration tests compile the real installer/catalog with inert OS/network boundaries.
namespace JBTheatreTools;

internal static class Log { public static void Write(string message) { } }
internal static class Shortcuts
{
    public static bool Succeed = true;
    public static readonly Dictionary<string, string> Start = new();
    public static readonly Dictionary<string, string> Desktop = new();
    public static string SafeName(string name) => name;
    public static bool CreateStartMenu(string name, string target)
    {
        if (!Succeed) return false;
        Start[name] = target; return true;
    }
    public static bool CreateDesktop(string name, string target)
    {
        if (!Succeed) return false;
        Desktop[name] = target; return true;
    }
    public static void RemoveStartMenu(string name) => Start.Remove(name);
    public static void RemoveDesktop(string name) => Desktop.Remove(name);
}
public sealed class ReleaseAsset
{
    public long Size { get; set; }
    public long Id { get; set; }
    public string Name { get; set; } = "";
}
public sealed class ReleaseInfo
{
    public string TagName { get; set; } = "";
    public List<ReleaseAsset> Assets { get; set; } = new();
}
public sealed class GitHubClient
{
    public Task DownloadAssetAsync(string owner, string repo, long id, string path, IProgress<double>? progress)
        => throw new InvalidOperationException("Network access is forbidden in install integration tests.");
}
