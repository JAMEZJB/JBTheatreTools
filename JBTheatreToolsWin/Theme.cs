using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>Light/dark theming helpers (parity with the macOS appearance toggle).</summary>
public static class Theme
{
    public static bool IsDark(string appearance)
    {
        if (appearance == "dark") return true;
        if (appearance == "light") return false;
        return SystemPrefersDark();
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        }
        catch { /* default to light */ }
        return false;
    }

    public static Color Bg(bool dark) => dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(245, 245, 245);
    public static Color Card(bool dark) => dark ? Color.FromArgb(46, 46, 46) : Color.White;
    public static Color Fg(bool dark) => dark ? Color.FromArgb(235, 235, 235) : Color.FromArgb(20, 20, 20);
    public static Color Sub(bool dark) => dark ? Color.FromArgb(165, 165, 165) : Color.FromArgb(110, 110, 110);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark-mode title bar via DWMWA_USE_IMMERSIVE_DARK_MODE (attr 20).</summary>
    public static void ApplyTitleBar(Form form, bool dark)
    {
        try
        {
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(form.Handle, 20, ref v, sizeof(int));
        }
        catch { /* non-fatal on older Windows */ }
    }
}
