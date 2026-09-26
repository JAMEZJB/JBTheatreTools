using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>Which Windows build of an app this PC installs. Lives in Core so the pick is unit-testable off Windows.
/// Parity with the macOS launcher's <c>MacArch</c>: an ARM64 PC runs an x64 build through Windows' built-in
/// emulation, so an edition with only an x64 build still installs there, and an edition with both builds can be
/// switched to x64 per install slot (the row's ⋯ → "Use the x64 build (emulated)").</summary>
public static class Platform
{
    public const string Arm64Key = "windows-arm64";
    public const string X64Key = "windows-x64";

    /// <summary>The asset key for the LAUNCHER's own build (its self-update): the architecture this process runs as.</summary>
    public static string AssetKey => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => Arm64Key,
        Architecture.X64 => X64Key,
        // No 32-bit/other build is published — resolve to a key with no catalog asset so the row
        // cleanly reports "no Windows build" rather than silently handing back a non-runnable x64 exe.
        _ => "windows-x86",
    };

    /// <summary>True on an ARM64 PC. The OS architecture, not the process's: the launcher itself may be the x64 build
    /// running emulated, and it must still install ARM64 builds there. (An ARM64 process is on ARM64 whatever the
    /// runtime reports.)</summary>
    public static bool OsIsArm64 { get; } =
        RuntimeInformation.OSArchitecture == Architecture.Arm64 || RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>The asset key of this PC's native build (for messages).</summary>
    public static string NativeKey => OsIsArm64 ? Arm64Key : X64Key;

    /// <summary>The build a slot installs, and whether it runs emulated. Not ARM64: the x64 build. ARM64 with
    /// <paramref name="preferX64"/> (the slot's choice): the x64 build when there is one, else the ARM64 build. ARM64
    /// otherwise: the ARM64 build, and only when there is none the x64 build (emulated). Null when there's no usable
    /// build. Pure, for the tests.</summary>
    public static (string Name, bool Emulated)? Pick(IReadOnlyDictionary<string, string> assets, bool osIsArm64, bool preferX64)
    {
        assets.TryGetValue(X64Key, out var x64);
        if (!osIsArm64) return x64 == null ? null : (x64, false);
        assets.TryGetValue(Arm64Key, out var arm);
        if (preferX64 && x64 != null) return (x64, true);
        if (arm != null) return (arm, false);
        return x64 == null ? null : (x64, true);
    }

    /// <summary>The choice exists only when the slot can run both ways: an ARM64 PC, and both an ARM64 and an x64
    /// build. An x64-only edition (PDF Tools Full) already runs emulated and never offers it.</summary>
    public static bool CanChoose(IReadOnlyDictionary<string, string> assets, bool osIsArm64) =>
        osIsArm64 && assets.ContainsKey(Arm64Key) && assets.ContainsKey(X64Key);
}

/// <summary>Which CPU an executable is built for — read from its PE header (parity with the macOS <c>MachO</c> reader).
/// Used when the x64 / ARM64 choice changes: an installed build that already matches isn't reinstalled.</summary>
public static class PeArch
{
    public const ushort X64 = 0x8664;
    public const ushort Arm64 = 0xAA64;

    /// <summary>Far enough for any real exe's PE header offset; a larger one is treated as not a PE file.</summary>
    private const int MaxHeaderOffset = 1 << 20;

    /// <summary>The COFF Machine field of a PE image ("MZ", e_lfanew at 0x3C, "PE\0\0", then Machine); null when the
    /// bytes aren't a PE header or are cut short.</summary>
    public static ushort? Machine(byte[] data)
    {
        if (data.Length < 0x40 || data[0] != (byte)'M' || data[1] != (byte)'Z') return null;
        int pe = BitConverter.ToInt32(data, 0x3C);   // little-endian on disk; every Windows / .NET host is little-endian
        if (pe < 0 || pe > MaxHeaderOffset || (long)pe + 6 > data.Length) return null;
        if (data[pe] != (byte)'P' || data[pe + 1] != (byte)'E' || data[pe + 2] != 0 || data[pe + 3] != 0) return null;
        return (ushort)(data[pe + 4] | data[pe + 5] << 8);
    }

    /// <summary>The Machine field of an exe on disk (reads only its headers); null when it can't be read or isn't a PE
    /// file. Disk I/O — never on the UI thread.</summary>
    public static ushort? OfFile(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = ReadUpTo(f, 0x40);
            if (head.Length < 0x40 || head[0] != (byte)'M' || head[1] != (byte)'Z') return null;
            int pe = BitConverter.ToInt32(head, 0x3C);
            if (pe < 0 || pe > MaxHeaderOffset) return null;
            f.Position = 0;
            return Machine(ReadUpTo(f, Math.Max(0x40, pe + 6)));
        }
        catch { return null; }
    }

    private static byte[] ReadUpTo(Stream s, int count)
    {
        var buf = new byte[count];
        int n = 0;
        while (n < count)
        {
            int r = s.Read(buf, n, count - n);
            if (r == 0) break;
            n += r;
        }
        return n == count ? buf : buf[..n];
    }
}
