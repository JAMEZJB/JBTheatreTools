using System.Text.Json;

namespace JBTheatreTools;

/// <summary>Wires the log to the settings file and writes the start-up header (always, verbose or not).</summary>
internal static class StartupLog
{
    /// <summary>Call first thing in every start (window, command line, background trigger copy).</summary>
    public static void Init()
    {
        Log.VerboseSource = AppSettings.ReadVerbose;
    }

    /// <summary>The header for this start: version and the settings that explain behaviour (never a credential).</summary>
    public static void Header(string mode, string? startedWith = null)
    {
        try
        {
            // The file only: AppSettings.Load() can read the credential store (first run), and a header must never do that.
            AppSettings s;
            try
            {
                var file = Path.Combine(AppSettings.Dir, "settings.json");
                s = (File.Exists(file) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(file)) : null) ?? new AppSettings();
            }
            catch { s = new AppSettings(); }
            string host = "(none)";
            try
            {
                var url = RelayUrl.Resolve(s.ServerUrl, Catalog.Load().DownloadServer);
                if (url != null && Uri.TryCreate(url, UriKind.Absolute, out var u)) host = u.Host;
            }
            catch { /* the catalog may be unreadable: say so below */ host = "(catalog unreadable)"; }
            var list = new List<(string, string)>();
            if (startedWith != null) list.Add(("started with", startedWith));
            list.Add(("download access", $"{(s.AuthMode.Length == 0 ? "not chosen yet" : s.AuthMode)} (relay host {host})"));
            list.Add(("install location", s.InstallToApplications ? "Start menu and Desktop shortcuts" : "Start menu shortcut only"));
            list.Add(("update checks", $"{s.UpdateMode}, every {s.AutoCheckInterval}"));
            list.Add(("development builds", s.DevChannel ? "on" : "off"));
            list.Add(("show lock", s.ShowLock ? "on" : "off"));
            list.Add(("web trigger", s.WebTriggerEnabled ? $"on, {s.WebTriggerBind}:{s.WebTriggerPort}" : "off"));
            list.Add(("settings file", Path.Combine(AppSettings.Dir, "settings.json")));
            list.Add(("log file", Log.FilePath));
            Log.WriteHeader(mode, MainForm.CurrentVersion(), list);
        }
        catch (Exception ex) { Log.Warn("start", $"the start-up header could not be completed: {ex.Message}"); }
    }
}
