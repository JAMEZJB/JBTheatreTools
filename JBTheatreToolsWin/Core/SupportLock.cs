namespace JBTheatreTools;

/// <summary>One lock shared by every launcher process that uses a support folder: the window, a command-line install
/// and one started remotely can all run at once on a PC. Whoever holds it is the only one changing installed.json,
/// the apps folder or the connector entry points, and reads them from disk. It is an exclusively opened file
/// (<c>.lock</c> in the folder), so a crashed holder releases it with its handle. Re-entrant on the holding thread:
/// an install that commits its manifest brings the connector list in step under the same hold.</summary>
public sealed class SupportLock
{
    private readonly string _path;
    private readonly object _gate = new();
    private int _depth;
    private FileStream? _file;

    public SupportLock(string directory) => _path = Path.Combine(directory, ".lock");

    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(2);

    /// <summary>Waits up to <paramref name="wait"/> (default two minutes) for the other processes; then throws
    /// <see cref="BusyException"/>.</summary>
    public IDisposable Hold(TimeSpan? wait = null)
    {
        Monitor.Enter(_gate);
        try
        {
            if (_depth == 0) _file = OpenExclusive(wait ?? DefaultWait);
            _depth++;
            return new Release(this);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    private FileStream OpenExclusive(TimeSpan wait)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(50); }
            catch (IOException) { throw new BusyException(); }
        }
    }

    private void Exit()
    {
        try
        {
            if (--_depth == 0)
            {
                _file?.Dispose();
                _file = null;
            }
        }
        finally { Monitor.Exit(_gate); }
    }

    private sealed class Release(SupportLock owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Exit();
        }
    }

    public sealed class BusyException()
        : IOException("Another JB Theatre Tools window or command is still installing. Try again when it has finished.");
}
