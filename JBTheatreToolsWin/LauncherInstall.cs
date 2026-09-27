using System.Diagnostics;

namespace JBTheatreTools;

/// <summary>Installs the launcher itself: this exe is copied (verified) to a proper place — by default the per-user
/// programs folder, see <see cref="LauncherHome"/> — with a Start menu shortcut, and the installed copy is started in
/// its place. Settings and installed apps live in AppData, so nothing else moves.</summary>
internal static class LauncherInstall
{
    /// <summary>Set on the installed copy's first start: "&lt;previous pid&gt;|&lt;exe it was installed from&gt;".</summary>
    public const string InstalledFromVar = "JBTT_INSTALLED_FROM";

    /// <remarks><c>JBTT_LAUNCHER_HOME</c> (a folder) stands in for the default place in tests, so a test run never
    /// touches a real install.</remarks>
    public static string DefaultExe =>
        Environment.GetEnvironmentVariable("JBTT_LAUNCHER_HOME") is { Length: > 0 } testHome
            ? LauncherHome.ExeIn(testHome)
            : LauncherHome.DefaultExe(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <remarks><c>JBTT_DOWNLOADS_DIR</c> stands in for Downloads in tests.</remarks>
    public static string DownloadsDir =>
        Environment.GetEnvironmentVariable("JBTT_DOWNLOADS_DIR") is { Length: > 0 } testDownloads
            ? testDownloads
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Copies <paramref name="currentExe"/> to <paramref name="targetExe"/> — through a temporary name in the
    /// target folder, checked against this exe's hash, then renamed into place (replacing an older copy there that isn't
    /// running) — and adds the Start menu shortcut (and a desktop one when asked). On failure nothing has changed.</summary>
    public static void Install(string currentExe, string targetExe, bool desktopShortcut)
    {
        var dir = Path.GetDirectoryName(targetExe) ?? throw new IOException($"No folder for {targetExe}");
        if (File.Exists(targetExe) && RunningFrom(targetExe))
            throw new IOException("JB Theatre Tools is already installed there and it's open. Close that copy, then try again.");
        var incoming = targetExe + ".new";
        try
        {
            Directory.CreateDirectory(dir);
            File.Copy(currentExe, incoming, overwrite: true);
            File.SetAttributes(incoming, FileAttributes.Normal);
        }
        catch (UnauthorizedAccessException)
        {
            TryDelete(incoming);
            throw new IOException($"Windows doesn't let JB Theatre Tools write to {dir} without an administrator. Choose another folder.");
        }
        if (!string.Equals(SelfReplace.Sha256(incoming), SelfReplace.Sha256(currentExe), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(incoming);
            throw new IOException("The copy didn't match — nothing was installed. Try again.");
        }
        string? old = null;
        try
        {
            if (File.Exists(targetExe)) { old = SelfReplace.FreeOldPath(targetExe); File.Move(targetExe, old); }
            File.Move(incoming, targetExe);
        }
        catch
        {
            if (old != null && !File.Exists(targetExe)) { try { File.Move(old, targetExe); } catch { /* keep what we can */ } }
            TryDelete(incoming);
            throw;
        }
        if (old != null) TryDelete(old);
        Shortcuts.CreateStartMenu(LauncherHome.FolderName, targetExe);
        if (desktopShortcut) Shortcuts.CreateDesktop(LauncherHome.FolderName, targetExe);
        Log.Write($"launcher installed at {targetExe} (from {currentExe}){(desktopShortcut ? " + desktop shortcut" : "")}");
    }

    /// <summary>The version of the launcher exe at <paramref name="exe"/> (its product version, i.e. the full dev tag), or
    /// null when there's none / it can't be read.</summary>
    public static string? VersionOf(string exe)
    {
        try { return File.Exists(exe) ? FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split('+')[0] : null; }
        catch { return null; }
    }

    /// <summary>Starts the installed copy, telling it which exe it replaced.</summary>
    public static void Start(string targetExe, string fromExe)
    {
        var psi = new ProcessStartInfo(targetExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(targetExe) ?? "" };
        psi.Environment[InstalledFromVar] = $"{Environment.ProcessId}|{fromExe}";
        using var _ = Process.Start(psi) ?? throw new IOException("The installed copy couldn't be started.");
    }

    /// <summary>On the installed copy's first start: once the copy it was installed from has closed, a download of it
    /// (in Downloads) goes to the Recycle Bin — anywhere else it's left alone. Clears the variable either way.</summary>
    public static void AfterInstall()
    {
        var v = Environment.GetEnvironmentVariable(InstalledFromVar);
        Environment.SetEnvironmentVariable(InstalledFromVar, null);   // apps launched from here mustn't inherit it
        if (v?.Split('|', 2) is not [var pidText, var from] || !int.TryParse(pidText, out var pid)) return;
        var self = Environment.ProcessPath;
        if (self == null || LauncherHome.Same(from, self) || !LauncherHome.IsInside(from, DownloadsDir)) return;
        _ = Task.Run(() =>
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(15_000); }
            catch (ArgumentException) { /* already gone */ }
            catch (Exception ex) { Log.Write($"install: waiting for the downloaded copy: {ex.Message}"); }
            try { if (File.Exists(from)) { RecycleBin.Move(from); Log.Write($"install: moved the downloaded copy {from} to the Recycle Bin"); } }
            catch (Exception ex) { Log.Write($"install: left the downloaded copy {from}: {ex.Message}"); }
        });
    }

    private static bool RunningFrom(string exe)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (p)
            {
                try { if (p.MainModule?.FileName is { } f && LauncherHome.Same(f, exe)) return true; }
                catch { /* another user's / elevated process */ }
            }
        }
        return false;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); } } catch { /* best effort */ }
    }
}
