using System.IO.Compression;
using System.Text;

namespace JBTheatreTools;

public sealed record InstalledPayload(string Executable, string Directory);

/// <summary>Prepare a new generation without touching the current install. Publishing its manifest
/// pointer is the commit point; a failed preparation or commit only removes the new generation.</summary>
public static class InstallTransaction
{
    public static InstalledPayload Install(string appDirectory, string slot, string download,
        string assetName, string preferredStem, Action<InstalledPayload> commit,
        CancellationToken cancellationToken = default)
    {
        ValidateComponent(slot);
        ValidateComponent(assetName);
        bool zip = assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (!zip && !assetName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The install asset must be an EXE or ZIP.");
        cancellationToken.ThrowIfCancellationRequested();
        System.IO.Directory.CreateDirectory(appDirectory);
        if ((File.GetAttributes(appDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The installation folder cannot be a filesystem link.");
        string generation = slot + "-" + Guid.NewGuid().ToString("N");
        string staging = Path.Combine(appDirectory, ".install-" + generation);
        string published = Path.Combine(appDirectory, generation);
        bool committed = false;
        try
        {
            System.IO.Directory.CreateDirectory(staging);
            string executable;
            if (zip)
            {
                Extract(download, staging, cancellationToken);
                var exes = System.IO.Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories)
                    .Where(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
                executable = FullApp.PickMainExe(exes, preferredStem)
                    ?? throw new InvalidDataException("No launcher executable was found in the ZIP.");
            }
            else
            {
                executable = Path.Combine(staging, assetName);
                using var source = File.OpenRead(download);
                using var destination = new FileStream(executable, FileMode.CreateNew, FileAccess.Write);
                Copy(source, destination, cancellationToken);
            }
            if (new FileInfo(executable).Length == 0)
                throw new InvalidDataException("The launcher executable is empty.");
            cancellationToken.ThrowIfCancellationRequested();
            string relativeExe = Path.GetRelativePath(staging, executable);
            System.IO.Directory.Move(staging, published);
            var payload = new InstalledPayload(Path.Combine(published, relativeExe), published);
            cancellationToken.ThrowIfCancellationRequested();
            // No cancellation after this callback succeeds: the installed pointer has committed.
            commit(payload);
            committed = true;
            return payload;
        }
        finally
        {
            DeleteUnusedDirectory(staging);
            if (!committed) DeleteUnusedDirectory(published);
        }
    }

    /// <summary>Atomic same-directory replacement; failure preserves the previous manifest bytes.</summary>
    public static void WriteManifest(string path, string json)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        System.IO.Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, ".manifest-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static void ValidateComponent(string component)
    {
        if (string.IsNullOrEmpty(component) || component is "." or ".." ||
            component.EndsWith('.') || component.EndsWith(' ') ||
            component.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c)))
            throw new InvalidDataException("An install path contains an invalid Windows filename.");
        string stem = component.Split('.')[0];
        if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
             "123456789¹²³".Contains(stem[3])))
            throw new InvalidDataException("An install path contains a reserved Windows filename.");
    }

    private static void Extract(string download, string staging, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(download);
        var entries = new List<(ZipArchiveEntry Entry, string Destination, bool IsDirectory)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            string normalized = entry.FullName.Replace('\\', '/');
            bool directory = normalized.EndsWith('/');
            string relative = directory ? normalized[..^1] : normalized;
            foreach (string component in relative.Split('/')) ValidateComponent(component);
            int fileType = (entry.ExternalAttributes >> 16) & 0xF000;
            if ((fileType != 0 && fileType != 0x8000 && fileType != 0x4000) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("ZIP filesystem links and special files are not supported.");
            if (!seen.Add(relative)) throw new InvalidDataException("The ZIP has duplicate Windows paths.");
            string destination = Path.GetFullPath(Path.Combine(staging,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar,
                                        StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A ZIP entry is outside the installation folder.");
            entries.Add((entry, destination, directory));
        }
        foreach (var item in entries)
        {
            token.ThrowIfCancellationRequested();
            if (item.IsDirectory) { System.IO.Directory.CreateDirectory(item.Destination); continue; }
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
            using var source = item.Entry.Open();
            using var destination = new FileStream(item.Destination, FileMode.CreateNew, FileAccess.Write);
            Copy(source, destination, token);
        }
    }

    private static void Copy(Stream source, Stream destination, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        int count;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            count = source.Read(buffer, 0, buffer.Length);
            if (count == 0) break;
            destination.Write(buffer, 0, count);
        }
        destination.Flush();
    }

    private static void DeleteUnusedDirectory(string path)
    {
        try { if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
