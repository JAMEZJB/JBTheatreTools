using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>
/// Creates / removes Start Menu + Desktop <c>.lnk</c> shortcuts — the Windows equivalent of installing
/// a macOS app into the Applications folder, so an installed tool is launchable from the Start menu /
/// Windows Search / Desktop without going through this launcher.
///
/// Uses the built-in Windows Script Host COM object (<c>WScript.Shell</c>) via late binding, so there's
/// no extra NuGet dependency. All operations are best-effort — a failure never blocks an install.
/// </summary>
internal static class Shortcuts
{
    // Per-user Start Menu\Programs and the Desktop — no admin rights needed.
    private static string StartMenuDir => Environment.GetFolderPath(Environment.SpecialFolder.Programs);
    private static string DesktopDir => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    /// <summary>Creates a Start Menu and a Desktop shortcut named <paramref name="name"/> → the exe.</summary>
    public static void Create(string name, string targetExe)
    {
        CreateAt(Path.Combine(StartMenuDir, name + ".lnk"), targetExe);
        CreateAt(Path.Combine(DesktopDir, name + ".lnk"), targetExe);
    }

    /// <summary>Removes both shortcuts previously created for <paramref name="name"/>.</summary>
    public static void Remove(string name)
    {
        TryDelete(Path.Combine(StartMenuDir, name + ".lnk"));
        TryDelete(Path.Combine(DesktopDir, name + ".lnk"));
    }

    /// <summary>Sanitises an app's display name into a valid .lnk file name.</summary>
    public static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return name.Trim();
    }

    private static void CreateAt(string lnkPath, string targetExe)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                var shortcut = shell.CreateShortcut(lnkPath);
                shortcut.TargetPath = targetExe;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe) ?? "";
                shortcut.IconLocation = targetExe + ",0";
                shortcut.Save();
                Marshal.FinalReleaseComObject(shortcut);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
        catch { /* shortcuts are best-effort */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
