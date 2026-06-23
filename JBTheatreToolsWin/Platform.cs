using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>Maps the host architecture to the catalog asset key for this OS.</summary>
public static class Platform
{
    public static string AssetKey => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "windows-arm64",
        Architecture.X64 => "windows-x64",
        // No 32-bit/other build is published — resolve to a key with no catalog asset so the row
        // cleanly reports "no Windows build" rather than silently handing back a non-runnable x64 exe.
        _ => "windows-x86",
    };
}
