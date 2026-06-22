using System.Text.Json;

namespace JBTheatreTools;

/// <summary>Tiny persisted settings (JSON at %APPDATA%\JBTheatreTools\settings.json).</summary>
public sealed class AppSettings
{
    public string Appearance { get; set; } = "system";   // "system" | "light" | "dark"
    public string UpdateMode { get; set; } = "everyLaunch"; // "everyLaunch" | "manual" | "never"
    public string CloseBehavior { get; set; } = "quit";   // "quit" | "keepRunning" (X = quit, or minimise to tray)
    public bool InstallToApplications { get; set; }       // true: also add Start menu + Desktop shortcuts on install

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JBTheatreTools");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
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
