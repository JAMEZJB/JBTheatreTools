using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>Moves a file or folder to the Recycle Bin (so an app's removed settings can still be got back), with no
/// shell dialogs of any kind — a failure comes back as an exception for the caller to report.</summary>
internal static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp
    {
        public IntPtr Hwnd;
        public uint Func;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref ShFileOp op);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo { public int Size; public long SizeBytes; public long NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string rootPath, ref ShQueryRbInfo info);

    private const uint FoDelete = 3;
    // FOF_WANTNUKEWARNING: if the shell would destroy the item instead of recycling it (too big for the Bin, say), it
    // asks first instead of silently deleting it for good — FOF_NOCONFIRMATION alone answers that question "yes".
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400, FofWantNukeWarning = 0x4000;

    /// <summary>True when <paramref name="path"/> is on a local fixed drive that has a Recycle Bin. A network share (a
    /// redirected AppData on a managed PC), removable media or a drive with the Bin turned off would delete for good.</summary>
    public static bool IsAvailableFor(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || new DriveInfo(root).DriveType != DriveType.Fixed) return false;
            var info = new ShQueryRbInfo { Size = Marshal.SizeOf<ShQueryRbInfo>() };
            return SHQueryRecycleBin(root, ref info) == 0;
        }
        catch { return false; }
    }

    /// <summary>Recycles <paramref name="path"/> (a full path). Never deletes for good: where there's no Recycle Bin it
    /// throws and nothing is touched. Runs the shell call on its own STA thread.</summary>
    public static void Move(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A full path is required.", nameof(path));
        if (!IsAvailableFor(path)) throw new IOException("that drive has no Recycle Bin, so it was left where it is.");
        int code = 0; bool aborted = false; Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                // pFrom is a double-NUL-terminated list; the marshaller adds the second NUL.
                var op = new ShFileOp { Func = FoDelete, From = path + "\0", Flags = FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi | FofWantNukeWarning };
                code = SHFileOperation(ref op);
                aborted = op.AnyOperationsAborted;
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw error;
        if (code != 0 || aborted || Directory.Exists(path) || File.Exists(path))
            throw new IOException($"it couldn't be moved to the Recycle Bin (it may be in use — code {code}).");
    }
}
