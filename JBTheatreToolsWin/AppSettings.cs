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
    public List<string> AppOrder { get; set; } = new();   // user's row order (app ids); empty = catalog order
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
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
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

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { /* non-fatal */ }
    }
}
