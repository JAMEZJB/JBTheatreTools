namespace JBTheatreTools;

/// <summary>
/// Minimal append-only diagnostics log at %LOCALAPPDATA%\JBTheatreTools\logs\JBTheatreTools.log.
/// Records installs / updates / uninstalls / launches and refresh/install errors so show-day failures
/// can be diagnosed after the fact. Never logs the token value. All operations are best-effort.
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    public static string FilePath { get; }

    static Log()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JBTheatreTools", "logs");
        try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        FilePath = Path.Combine(dir, "JBTheatreTools.log");
        TrimIfLarge();
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch { /* logging never throws into the caller */ }
    }

    /// <summary>Opens the log in the user's default text viewer.</summary>
    public static void Open()
    {
        try
        {
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = FilePath,
                UseShellExecute = true
            });
        }
        catch { /* best effort */ }
    }

    /// <summary>Keep the log bounded — on launch, if it's over ~1 MB keep only the last 2000 lines.</summary>
    private static void TrimIfLarge()
    {
        try
        {
            if (!File.Exists(FilePath) || new FileInfo(FilePath).Length <= 1_000_000) return;
            var lines = File.ReadAllLines(FilePath);
            File.WriteAllLines(FilePath, lines.Skip(Math.Max(0, lines.Length - 2000)));
        }
        catch { /* best effort */ }
    }
}
