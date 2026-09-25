using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JBTheatreTools;

/// <summary>The "Copy Diagnostics" report: everything useful for a support question, nothing secret. Credentials
/// are never passed in, and every log line is run through <see cref="Redact"/> as a second line of defence.</summary>
public static class Diagnostics
{
    public const int LogLines = 40;

    public sealed record AppLine(string Name, string? Installed, string? Latest, string Status, bool Held);

    public sealed record Info(
        string LauncherVersion, string Os, string Arch, string AuthMode, string? RelayHost, bool DevChannel,
        bool ShowLock, string InstallLocation, IReadOnlyList<AppLine> Apps, IReadOnlyList<string> LogTail,
        DateTimeOffset Now);

    // GitHub token shapes, and the value after an Authorization scheme word (the word itself is kept).
    private static readonly Regex Tokens = new(@"gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}");
    private static readonly Regex SchemeValues = new(@"\b(Bearer|Basic|token)\s+[A-Za-z0-9+/=._\-]{16,}", RegexOptions.IgnoreCase);

    public static string Redact(string line) => SchemeValues.Replace(Tokens.Replace(line, "[redacted]"), "$1 [redacted]");

    public static string Build(Info i)
    {
        var sb = new StringBuilder();
        sb.Append("JB Theatre Tools diagnostics — ")
          .Append(i.Now.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Launcher: v").Append(VersionCompare.Norm(i.LauncherVersion)).Append('\n');
        sb.Append("System: ").Append(i.Os).Append(" (").Append(i.Arch).Append(")\n");
        sb.Append("Downloads via: ").Append(i.AuthMode);
        if (!string.IsNullOrEmpty(i.RelayHost)) sb.Append(" (").Append(i.RelayHost).Append(')');
        sb.Append('\n');
        sb.Append("Install location: ").Append(i.InstallLocation).Append('\n');
        sb.Append("Show lock: ").Append(i.ShowLock ? "on" : "off")
          .Append(" · Development builds: ").Append(i.DevChannel ? "on" : "off").Append('\n');
        sb.Append("\nApps (").Append(i.Apps.Count).Append("):\n");
        foreach (var a in i.Apps)
        {
            sb.Append("  ").Append(a.Name).Append(" — installed ").Append(a.Installed ?? "—")
              .Append(", latest ").Append(a.Latest ?? "—").Append(", ").Append(a.Status);
            if (a.Held) sb.Append(", held");
            sb.Append('\n');
        }
        var tail = i.LogTail.Skip(Math.Max(0, i.LogTail.Count - LogLines)).ToList();
        sb.Append("\nRecent log (").Append(tail.Count).Append(" lines):\n");
        foreach (var l in tail) sb.Append("  ").Append(Redact(l)).Append('\n');
        return sb.ToString();
    }
}

/// <summary>When to show the launcher's own "what's new" after it has been updated.</summary>
public static class LauncherWhatsNew
{
    /// <summary>True on the first launch of a version newer than the last one seen. A fresh install (nothing
    /// seen yet) shows nothing — the caller just records the current version.</summary>
    public static bool ShouldShow(string? lastSeen, string current) =>
        !string.IsNullOrWhiteSpace(lastSeen) && VersionCompare.IsNewer(current, lastSeen);
}
