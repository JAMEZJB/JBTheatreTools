using System.Text.Json;

namespace JBTheatreTools;

/// <summary>Tiny persisted settings (JSON at %APPDATA%\JBTheatreTools\settings.json).</summary>
public sealed class AppSettings
{
    public string Appearance { get; set; } = "system";   // "system" | "light" | "dark"
    public string UpdateMode { get; set; } = "everyLaunch"; // "everyLaunch" | "manual" | "never"
    public string CloseBehavior { get; set; } = "quit";   // "quit" | "keepRunning" (X = quit, or minimise to tray)
    public bool InstallToApplications { get; set; }       // true: also add Start menu + Desktop shortcuts on install
    public string AuthMode { get; set; } = "";            // "token" (GitHub PAT) | "server" (relay, DEFAULT); "" = resolved in Load()
    public string ViewMode { get; set; } = "list";        // "list" | "grid" (icon-tile layout)
    public bool DevChannel { get; set; }                  // offer vX.Y.Z-dev.N pre-releases (hidden: Settings → click the version 7×)
    public List<string> AppOrder { get; set; } = new();   // user's row order (app ids); empty = catalog order
    public List<string> PinnedApps { get; set; } = new(); // app ids pinned to the top of the list
    public List<string> HiddenApps { get; set; } = new(); // app ids hidden from the list
    public List<string> CategoryOrder { get; set; } = new();       // user's category section order; empty = catalog order
    public List<string> CollapsedCategories { get; set; } = new(); // category keys the user collapsed
    public Dictionary<string, string> AppVariants { get; set; } = new(); // app id → selected variant id (e.g. NDI standard/full)
    public bool ShowLock { get; set; }                    // show lock: installs / updates / removals paused, Launch still works
    public List<string> HeldApps { get; set; } = new();   // app ids held at their installed version (Update All leaves them)
    public string AutoCheckInterval { get; set; } = UpdatePolicy.DefaultInterval; // "off" | "1h" | "4h" | "12h" | "24h" (while open)
    public bool NotifyUpdates { get; set; } = true;       // notify (tray balloon) when a check finds new updates
    public bool AutoInstallUpdates { get; set; }          // install updates automatically after a check (never for open apps)
    public List<string> NotifiedUpdates { get; set; } = new(); // "id version" keys already announced
    public string LastSeenLauncherVersion { get; set; } = ""; // drives the one-time "Updated to vX" banner
    public bool AlwaysShowTray { get; set; }              // keep the notification-area icon (quick launch) while the window is open
    public string ServerUrl { get; set; } = "";           // user-INVISIBLE relay-URL override (settings.json only, no UI); normally
                                                          // empty — the URL comes from the catalog's downloadServer

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JBTheatreTools");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        AppSettings s = new();
        try
        {
            if (File.Exists(FilePath))
            {
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
                s.LoadedFromFile = true;
            }
        }
        catch { /* fall through to defaults */ }
        // First run of this build generation: materialise the default auth mode. Server (passphrase)
        // is the default for fresh installs, but a machine that already has a PAT saved stays in
        // token mode — updating must never silently break a working token setup.
        if (string.IsNullOrEmpty(s.AuthMode))
        {
            bool hasPat = false;
            try { hasPat = TokenStore.Load() != null; } catch { /* no Credential Manager → fresh */ }
            s.AuthMode = hasPat ? "token" : "server";
        }
        return s;
    }

    /// <summary>Settings existed on disk at start-up (the launcher has been used before).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public bool LoadedFromFile { get; private set; }

    /// <summary>Written to a temp file, then moved over the old one: a crash mid-write can't leave a truncated file
    /// (which would load as defaults — dropping show lock and holds, which the command line also reads).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* non-fatal */ }
    }
}
