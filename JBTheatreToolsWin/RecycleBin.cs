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

    private const uint FoDelete = 3;
    private const ushort FofSilent = 0x4, FofNoConfirmation = 0x10, FofAllowUndo = 0x40, FofNoErrorUi = 0x400;

    /// <summary>Recycles <paramref name="path"/> (a full path). Runs the shell call on its own STA thread.</summary>
    public static void Move(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A full path is required.", nameof(path));
        int code = 0; bool aborted = false; Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                // pFrom is a double-NUL-terminated list; the marshaller adds the second NUL.
                var op = new ShFileOp { Func = FoDelete, From = path + "\0", Flags = FofSilent | FofNoConfirmation | FofAllowUndo | FofNoErrorUi };
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
