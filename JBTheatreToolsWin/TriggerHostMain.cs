using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace JBTheatreTools;

/// <summary><c>JBTheatreTools.exe --trigger-host</c>: ONLY the Dashboard web trigger, headless — no form, no tray icon,
/// no console (the exe is a windowed program, and Stagehand starts it with no window). Started and kept alive by
/// Stagehand; the same settings, token and Owner retry as the window, and the same actions run against the install
/// manifest. Exit 0 when the trigger is switched off in Settings.</summary>
internal static class TriggerHostMain
{
    public static int Run()
    {
        static void L(string line) => Log.Write("[host] " + line);
        Catalog catalog;
        try { catalog = Catalog.Load(); }
        catch (Exception ex)
        {
            L($"couldn't read the app list ({ex.Message}) — the background copy stops");
            return 1;
        }
        var apps = catalog.Apps
            .Select(a => new LauncherActions.App(a.Id, a.Name,
                a.SlotsFullFirst().Select(s => new LauncherActions.Slot(s.Key, a.VariantSuffix(s.Edition))).ToList()))
            .ToList();
        var host = new HeadlessActionHost(apps,
            showLock: () => AppSettings.Load().ShowLock || ClaudeSettingsFile.SuiteShowLock() == true,
            turnShowLockOn: () =>
            {
                var s = AppSettings.Load();
                s.ShowLock = true;
                s.Save();
                ClaudeSettingsFile.SetSuiteShowLock(true);
            },
            tickedApps: () => AppSettings.Load().WebTriggerApps,
            installedVersion: k => InstallManager.Shared.InstalledVersion(k),
            isOpen: k =>
            {
                var open = InstallManager.Shared.RunningInstances(k);
                foreach (var p in open) p.Dispose();
                return open.Length > 0;
            },
            launch: Launch,
            version: Version(),
            log: L);
        // The token is read again on every pass (a change made in the window applies at once).
        string Token() => HeadlessTrigger.Word(TokenStore.LoadTriggerToken);
        var trigger = new LauncherTrigger(host,
            HeadlessTrigger.Gate(() => AppSettings.Load().WebTriggerEnabled, InstallManager.Shared.Refresh), Token, L);
        var owner = new WebTriggerOwner(trigger.Server, log: L);
        var runner = new TriggerHostRunner(owner, () =>
        {
            var s = AppSettings.Load();
            return new TriggerSettings(s.WebTriggerEnabled, s.WebTriggerBind, s.WebTriggerPort);
        }, Token, TriggerRegistration.StateDir(), log: L,
            windowOpen: () => TriggerRegistration.WindowRunning(TriggerRegistration.StateDir(), Environment.ProcessId, pid =>
                TriggerRegistration.PidAlive(pid) && ProcessInfo.ImagePath(pid) is { } image && Environment.ProcessPath is { } self &&
                string.Equals(Path.GetFullPath(image), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase)));
        return runner.Run(TimeSpan.FromSeconds(WebTriggerContract.RetrySeconds));
    }

    /// <summary>The launcher's version, as the window shows it (a dev build's full tag).</summary>
    private static string Version()
    {
        var info = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info)) return info.Split('+')[0];
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v == null ? "1.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>This copy runs inside Stagehand's kill-on-close job, and anything it starts the plain way joins that job:
    /// a restart or update of the hidden copy would close the show app with it. So the app is started outside the job
    /// (CREATE_BREAKAWAY_FROM_JOB; Stagehand's job allows it), and only the plain way if the job refuses.</summary>
    private static void Launch(string slotKey)
    {
        var exe = InstallManager.Shared.InstalledPath(slotKey) ?? throw new InvalidOperationException("App is not installed.");
        if (LaunchOutsideJob(exe)) return;
        Log.Write($"[host] couldn't start {slotKey} outside the background copy's job (error {Marshal.GetLastWin32Error()}) — starting it inside");
        InstallManager.Shared.Launch(slotKey);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string? app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
                                              IntPtr env, string? cwd, ref StartupInfo si, out ProcessInformation pi);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
    private const uint CreateBreakawayFromJob = 0x01000000;

    private static bool LaunchOutsideJob(string exe)
    {
        var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        if (!CreateProcessW(exe, new StringBuilder("\"" + exe + "\""), IntPtr.Zero, IntPtr.Zero, false, CreateBreakawayFromJob,
                            IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi)) return false;
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }
}
