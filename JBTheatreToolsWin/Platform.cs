using System.Runtime.InteropServices;

namespace JBTheatreTools;

/// <summary>Maps the host architecture to the catalog asset key for this OS.</summary>
public static class Platform
{
    public static string AssetKey =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "windows-arm64" : "windows-x64";
}
