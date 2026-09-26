using System.Text.Json;

namespace JBTheatreTools;

/// <summary>The activity history file (%LOCALAPPDATA%\JBTheatreTools\history.json). Parsing, capping and wording
/// live in Core's <see cref="ActivityHistory"/>; this is only the load / append / save, serialised by a lock and
/// written atomically (temp file + move) so a crash mid-write can't corrupt the history.</summary>
internal static class History
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(InstallManager.Shared.SupportDir, "history.json");

    /// <summary>Writes run one after another on a background chain: <see cref="Add"/> used to read, parse and rewrite
    /// the whole file on the UI thread for every install. <see cref="Load"/> waits for queued writes first.</summary>
    private static Task _tail = Task.CompletedTask;

    /// <summary>Waits (up to 5 s) for queued writes, so the window calls it off the UI thread.</summary>
    public static List<ActivityEvent> Load()
    {
        Flush();
        lock (Gate)
        {
            try { return File.Exists(FilePath) ? ActivityHistory.Parse(File.ReadAllText(FilePath)) : new List<ActivityEvent>(); }
            catch { return new List<ActivityEvent>(); }
        }
    }

    /// <summary>Waits (bounded, ~5 s) for every queued write to land — the command line calls it before it exits, or
    /// a process that ends right after an install would drop the line it had just queued.</summary>
    public static void Flush()
    {
        Task pending;
        lock (Gate) pending = _tail;
        try { pending.Wait(TimeSpan.FromSeconds(5)); } catch { /* a failed write is logged; still read what's there */ }
    }

    /// <summary>Records one event (queued; never blocks the caller). Best effort — history never gets in the way of an install.</summary>
    public static void Add(string app, string name, string action, string? from = null, string? to = null, string? note = null)
    {
        var ev = new ActivityEvent(DateTimeOffset.UtcNow, app, name, action, from, to, note);
        lock (Gate) _tail = _tail.ContinueWith(_ => Write(ev, action, app), TaskScheduler.Default);
    }

    private static void Write(ActivityEvent ev, string action, string app)
    {
        lock (Gate)
        {
            try
            {
                var list = new List<ActivityEvent>();
                if (File.Exists(FilePath))
                {
                    var text = File.ReadAllText(FilePath);
                    // A file that exists but isn't a JSON list (damaged): keep it aside rather than overwrite the history.
                    if (!string.IsNullOrWhiteSpace(text) && !IsJsonList(text))
                    {
                        File.Move(FilePath, Path.Combine(Path.GetDirectoryName(FilePath)!, "history.json.bad"), overwrite: true);
                        Log.Write("history: history.json was damaged — kept as history.json.bad, starting a new one");
                    }
                    else list = ActivityHistory.Parse(text);
                }
                list = ActivityHistory.Append(list, ev);
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, ActivityHistory.Serialize(list));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex) { Log.Write($"history: could not record {action} {app}: {ex.Message}"); }
        }
    }

    private static bool IsJsonList(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Array;
        }
        catch (JsonException) { return false; }
    }
}
