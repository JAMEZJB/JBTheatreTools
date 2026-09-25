namespace JBTheatreTools;

/// <summary>The activity history file (%LOCALAPPDATA%\JBTheatreTools\history.json). Parsing, capping and wording
/// live in Core's <see cref="ActivityHistory"/>; this is only the load / append / save, serialised by a lock and
/// written atomically (temp file + move) so a crash mid-write can't corrupt the history.</summary>
internal static class History
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(InstallManager.Shared.SupportDir, "history.json");

    public static List<ActivityEvent> Load()
    {
        lock (Gate)
        {
            try { return File.Exists(FilePath) ? ActivityHistory.Parse(File.ReadAllText(FilePath)) : new List<ActivityEvent>(); }
            catch { return new List<ActivityEvent>(); }
        }
    }

    /// <summary>Records one event. Best effort — history never gets in the way of an install.</summary>
    public static void Add(string app, string name, string action, string? from = null, string? to = null, string? note = null)
    {
        lock (Gate)
        {
            try
            {
                var list = File.Exists(FilePath) ? ActivityHistory.Parse(File.ReadAllText(FilePath)) : new List<ActivityEvent>();
                list = ActivityHistory.Append(list, new ActivityEvent(DateTimeOffset.UtcNow, app, name, action, from, to, note));
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, ActivityHistory.Serialize(list));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { Log.Write($"history: could not record {action} {app}: {ex.Message}"); }
        }
    }
}
