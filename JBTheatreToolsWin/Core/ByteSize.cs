using System.Globalization;

namespace JBTheatreTools;

/// <summary>Human byte counts for download sizes, storage use and the disk-space check — the same wording on
/// macOS, Windows and Android. Decimal units (1 KB = 1000 bytes, as Finder and the Settings apps show them); one
/// decimal under 10 ("4.2 MB"), whole numbers from 10 up ("450 MB"). A value that rounds up to 1000 moves up a
/// unit ("1.0 MB", never "1000 KB").</summary>
public static class ByteSize
{
    private static readonly string[] Units = { "KB", "MB", "GB", "TB" };

    public static string Format(long bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1000) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        var ci = CultureInfo.InvariantCulture;
        double v = bytes / 1000.0;
        int u = 0;
        while (true)
        {
            if (v < 10)
            {
                double r = Math.Round(v, 1, MidpointRounding.AwayFromZero);
                if (r < 10) return $"{r.ToString("0.0", ci)} {Units[u]}";
            }
            double n = Math.Round(v, MidpointRounding.AwayFromZero);
            if (n < 1000 || u == Units.Length - 1) return $"{n.ToString("0", ci)} {Units[u]}";
            v /= 1000;
            u++;
        }
    }

    /// <summary>Sum that never overflows (saturates at long.MaxValue) and ignores negative "unknown" sizes.</summary>
    public static long Sum(IEnumerable<long> sizes)
    {
        long total = 0;
        foreach (var s in sizes)
        {
            if (s <= 0) continue;
            total = s > long.MaxValue - total ? long.MaxValue : total + s;
        }
        return total;
    }
}
