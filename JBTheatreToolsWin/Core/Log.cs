using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JBTheatreTools;

/// <summary>
/// The launcher's log: <c>%LOCALAPPDATA%\JBTheatreTools\logs\JBTheatreTools.log</c> (the suite's verbose logging rule).
/// One line per event, the same shape in every app:
/// <code>2026-11-19 14:03:22.417  DEBUG  [github] GET api.github.com/repos/…/releases → 200, 4,210 bytes, 318 ms</code>
/// INFO / WARN / ERROR are always written. DEBUG is written only while "Verbose logging (for troubleshooting)" is on
/// (Settings, key <c>verbose</c>). Secrets never reach the file: call sites don't pass them, and <see cref="Redact"/>
/// masks anything of a known secret shape, and any credential registered with <see cref="RegisterSecret"/>, as a second
/// line of defence. Lines are queued and written on a background thread, so logging never slows the window, and a full
/// disk or an unwritable folder means no log, never a crash.
/// </summary>
public sealed class LogFile : IDisposable
{
    public const int MaxLine = 2000;
    public const long NormalLimit = 1_000_000;
    public const int NormalKeep = 2;
    public const long VerboseLimit = 5_000_000;
    public const int VerboseKeep = 3;

    public string FilePath { get; }

    private readonly Func<bool> _readVerbose;
    private readonly Action<bool>? _saveVerbose;
    private readonly object _state = new();
    private bool _verbose;
    private long _checkedAt;
    private readonly List<string> _secrets = new();
    private readonly BlockingCollection<(DateTime When, string Level, string Area, string Text)> _queue = new();
    private readonly Thread _writer;
    private long _pending;

    /// <param name="directory">Where the log lives. Tests pass a temporary folder.</param>
    /// <param name="readVerbose">Reads the saved setting (the settings file). Called at most every two seconds, so a
    /// separate background copy of the launcher follows the switch without a restart.</param>
    /// <param name="saveVerbose">Saves the setting when <see cref="SetVerbose"/> is used.</param>
    public LogFile(string directory, Func<bool>? readVerbose = null, Action<bool>? saveVerbose = null)
    {
        try { Directory.CreateDirectory(directory); } catch { /* no log, no crash */ }
        FilePath = Path.Combine(directory, "JBTheatreTools.log");
        _readVerbose = readVerbose ?? (() => false);
        _saveVerbose = saveVerbose;
        try { _verbose = _readVerbose(); } catch { _verbose = false; }
        _checkedAt = Environment.TickCount64;
        _writer = new Thread(WriterLoop) { IsBackground = true, Name = "JBTT log writer" };
        _writer.Start();
    }

    // ---- the switch -------------------------------------------------------------------------------------------

    public bool Verbose
    {
        get
        {
            lock (_state)
            {
                if (Environment.TickCount64 - _checkedAt > 2000)
                {
                    try { _verbose = _readVerbose(); } catch { /* keep the last value */ }
                    _checkedAt = Environment.TickCount64;
                }
                return _verbose;
            }
        }
    }

    /// <summary>Settings: switch verbose on or off. Takes effect at once, is remembered, and is recorded in the log.</summary>
    public void SetVerbose(bool on)
    {
        bool was;
        lock (_state) { was = _verbose; _verbose = on; _checkedAt = Environment.TickCount64; }
        try { _saveVerbose?.Invoke(on); } catch { /* best effort */ }
        if (was != on) Info("settings", $"verbose logging {(was ? "on" : "off")} → {(on ? "on" : "off")}");
    }

    // ---- writing ----------------------------------------------------------------------------------------------

    public void Debug(string area, string message) { if (Verbose) Enqueue("DEBUG", area, message); }
    public void Debug(string area, Func<string> message) { if (Verbose) Enqueue("DEBUG", area, message()); }
    public void Info(string area, string message) => Enqueue("INFO ", area, message);
    public void Warn(string area, string message) => Enqueue("WARN ", area, message);

    /// <summary>One plain line saying what failed and why, then the technical detail (type, message, stack) below it.</summary>
    public void Error(string area, string message, Exception? ex = null)
    {
        var text = message;
        if (ex != null) text += "\n    " + ex.GetType().Name + ": " + ex.Message + (ex.StackTrace is { } st ? "\n" + st : "");
        Enqueue("ERROR", area, text);
    }

    /// <summary>Existing free-text lines ("install x FAILED: …"): INFO, the area taken from a short head before the first
    /// colon, else from the first word. Lines holding "failed" or "couldn't" are warnings.</summary>
    public void Write(string message)
    {
        var lower = message.ToLowerInvariant();
        var level = lower.Contains("failed") || lower.Contains("couldn't") || lower.Contains("can't") ? "WARN " : "INFO ";
        Enqueue(level, AreaOf(message), message);
    }

    public static string AreaOf(string message)
    {
        if (message.StartsWith('[') && message.IndexOf(']') is > 0 and var close) return message[1..close];
        var colon = message.IndexOf(':');
        if (colon is > 0 and <= 24)
        {
            var head = message[..colon];
            if (!head.Contains('/') && !head.Contains('\\') && head.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 2)
                return head.ToLowerInvariant().Replace(' ', '-');
        }
        var first = message.TrimStart().Split(' ', 2)[0].ToLowerInvariant();
        var letters = new string(first.Where(c => char.IsLetter(c) || c == '-').ToArray());
        return letters.Length == 0 ? "app" : letters;
    }

    private void Enqueue(string level, string area, string text)
    {
        try
        {
            // Redacted and cut here, on the caller's thread, so no secret ever sits in the queue.
            Interlocked.Increment(ref _pending);
            _queue.Add((DateTime.Now, level, area, Redact(text)));
        }
        catch { Interlocked.Decrement(ref _pending); }
    }

    private void WriterLoop()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                var sb = new StringBuilder();
                var stamp = item.When.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                var lines = item.Text.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    sb.Append(i == 0 ? $"{stamp}  {item.Level}  [{item.Area}] {Cut(lines[i])}" : Cut(lines[i])).Append('\n');
                Append(sb.ToString());
            }
            catch { /* logging never throws */ }
            finally { Interlocked.Decrement(ref _pending); }
        }
    }

    public static string Cut(string line) => line.Length > MaxLine ? line[..(MaxLine - 1)] + "…" : line;

    private void Append(string text)
    {
        RotateIfNeeded(Encoding.UTF8.GetByteCount(text));
        File.AppendAllText(FilePath, text, new UTF8Encoding(false));
    }

    /// <summary>The log rolls at 1 MB (5 MB with verbose on) and keeps 2 older files (3 with verbose on); <c>.log.1</c> is the newest.</summary>
    private void RotateIfNeeded(int adding)
    {
        long limit = _verbose ? VerboseLimit : NormalLimit;
        int keep = _verbose ? VerboseKeep : NormalKeep;
        if (!File.Exists(FilePath) || new FileInfo(FilePath).Length + adding <= limit) return;
        var old = FilePath + ".";
        try { File.Delete(old + keep); } catch { }
        for (int n = keep - 1; n >= 1; n--)
            if (File.Exists(old + n)) { try { File.Move(old + n, old + (n + 1), true); } catch { } }
        File.Move(FilePath, old + "1", true);
        for (int n = keep + 1; File.Exists(old + n); n++) { try { File.Delete(old + n); } catch { } }
    }

    /// <summary>Waits until every queued line is on disk (tests, and the end of a run).</summary>
    public void Flush(int timeoutMs = 5000)
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Interlocked.Read(ref _pending) > 0 && Environment.TickCount64 < until) Thread.Sleep(5);
    }

    // ---- reading ----------------------------------------------------------------------------------------------

    /// <summary>The last <paramref name="count"/> lines (at most 400), optionally only those holding <paramref name="filter"/>; already masked.</summary>
    public List<string> Tail(int count, string? filter = null)
    {
        Flush();
        try
        {
            if (!File.Exists(FilePath)) return new List<string>();
            IEnumerable<string> lines;
            using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long take = Math.Min(fs.Length, 400_000);
                fs.Seek(-take, SeekOrigin.End);
                using var rd = new StreamReader(fs, Encoding.UTF8);
                lines = rd.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
            }
            if (!string.IsNullOrWhiteSpace(filter))
                lines = lines.Where(l => l.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase));
            return lines.TakeLast(Math.Clamp(count, 0, 400)).Select(Redact).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Settings → Open Log: shows the file in Explorer (creating it if there is none yet).</summary>
    public void Open()
    {
        try
        {
            Flush();
            if (!File.Exists(FilePath)) File.WriteAllText(FilePath, "");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe", Arguments = $"/select,\"{FilePath}\"", UseShellExecute = true,
            });
        }
        catch { /* best effort */ }
    }

    // ---- start-up header --------------------------------------------------------------------------------------

    /// <summary>Always written, verbose or not: what is running and the settings that explain its behaviour (never a secret).</summary>
    public void WriteHeader(string mode, string version, IEnumerable<(string Name, string Value)> settings)
    {
        var exe = Environment.ProcessPath ?? "";
        bool source = Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                      || Path.GetFileNameWithoutExtension(exe).Equals("testhost", StringComparison.OrdinalIgnoreCase);
        Info("start", $"==== JB Theatre Tools {version} ({mode}) ====");
        Info("start", $"system: {RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture} PC, this program runs as {RuntimeInformation.ProcessArchitecture}");
        Info("start", $"running as: {(source ? "run from source (not a built app)" : "the built app")} at {exe}");
        foreach (var (name, value) in settings) Info("start", $"setting {name}: {value}");
        Info("start", $"verbose logging: {(Verbose ? "ON (every step is recorded)" : "off (switch it on in Settings to trace a problem)")}");
    }

    // ---- secrets ----------------------------------------------------------------------------------------------

    /// <summary>Tells the log a credential the app now holds, so any exact copy of it is masked wherever it turns up. Short
    /// values are ignored: they would mask ordinary words.</summary>
    public void RegisterSecret(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 6) return;
        lock (_state)
        {
            if (!_secrets.Contains(value)) _secrets.Add(value);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
            if (!_secrets.Contains(b64)) _secrets.Add(b64);
        }
    }

    public string Redact(string text)
    {
        string[] known;
        lock (_state) known = _secrets.ToArray();
        foreach (var s in known) text = text.Replace(s, "<redacted>", StringComparison.Ordinal);
        return RedactShapes(text);
    }

    private static readonly (Regex Re, string Template)[] Shapes =
    {
        (new Regex(@"gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}"), "<redacted>"),
        (new Regex(@"\b(Bearer|Basic|token)\s+[A-Za-z0-9+/=._\-]{8,}", RegexOptions.IgnoreCase), "$1 <redacted>"),
        (new Regex(@"\b(authorization)\s*[:=]\s*[^\s,;]+(\s+[^\s,;]+)?", RegexOptions.IgnoreCase), "$1: <redacted>"),
        (new Regex(@"([?&""']?\b(?:access_?token|token|passphrase|password|passwd|pass|secret|api_?key|key)\b[""']?\s*[=:]\s*)(?:""[^""]*""|'[^']*'|[^\s&,;""']+)", RegexOptions.IgnoreCase), "$1<redacted>"),
        (new Regex(@"(--(?:token|server-pass|passphrase|password)\s+)\S+", RegexOptions.IgnoreCase), "$1<redacted>"),
        (new Regex(@"(X-Amz-Signature|X-Amz-Credential|X-Amz-Security-Token|sig|signature)=[^&\s]+", RegexOptions.IgnoreCase), "$1=<redacted>"),
    };

    public static string RedactShapes(string line)
    {
        foreach (var (re, template) in Shapes) line = re.Replace(line, template);
        return line;
    }

    /// <summary>A command line with the value after each credential flag hidden, for the header.</summary>
    public static string MaskedArguments(IEnumerable<string> args)
    {
        var o = new List<string>();
        bool hide = false;
        foreach (var a in args)
        {
            if (hide) { o.Add("<redacted>"); hide = false; continue; }
            o.Add(a);
            if (a is "--token" or "--server-pass" or "--passphrase" or "--password") hide = true;
        }
        return string.Join(' ', o);
    }

    // ---- helpers for the call sites ---------------------------------------------------------------------------

    /// <summary>"api.github.com/repos/o/r/releases": host and path of a URL, with the query (which can carry a signature) dropped.</summary>
    public static string HostPath(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return url ?? "(no address)";
        return u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + u.AbsolutePath;
    }

    public static string Bytes(long n) => n.ToString("N0", CultureInfo.GetCultureInfo("en-GB")) + " bytes";

    /// <summary>A network or I/O error in plain words, for a log line.</summary>
    public static string Explain(Exception ex)
    {
        Exception e = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        string why = e switch
        {
            TaskCanceledException or TimeoutException => "no reply in time",
            System.Net.Http.HttpRequestException { InnerException: System.Net.Sockets.SocketException s } => Socket(s),
            System.Net.Http.HttpRequestException h => h.Message,
            System.Net.Sockets.SocketException s => Socket(s),
            _ => e.Message,
        };
        return $"{why} ({e.GetType().Name})";

        static string Socket(System.Net.Sockets.SocketException s) => s.SocketErrorCode switch
        {
            System.Net.Sockets.SocketError.ConnectionRefused => "refused: nothing is listening at that address and port",
            System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.TryAgain => "the host name could not be found",
            System.Net.Sockets.SocketError.TimedOut => "no reply in time",
            System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable => "no route to that address: this PC has no connection to it",
            System.Net.Sockets.SocketError.ConnectionReset => "the connection dropped part-way",
            _ => s.Message,
        };
    }

    public void Dispose() { _queue.CompleteAdding(); _writer.Join(2000); }
}

/// <summary>The launcher's one log (static), over a <see cref="LogFile"/> in the per-user logs folder.</summary>
public static class Log
{
    private static readonly Lazy<LogFile> Shared = new(() =>
    {
        // JBTT_LOG_DIR: a test stand-in (like JBTT_SUPPORT_DIR), so a test run never writes to this PC's real log.
        var dir = Environment.GetEnvironmentVariable("JBTT_LOG_DIR") is { Length: > 0 } testDir ? testDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JBTheatreTools", "logs");
        var file = new LogFile(dir, () => VerboseSource?.Invoke() ?? false, v => VerboseSink?.Invoke(v));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => file.Flush(2000);
        return file;
    });

    /// <summary>Set by the program at start: reads / saves the setting (the settings file).</summary>
    public static Func<bool>? VerboseSource { get; set; }
    public static Action<bool>? VerboseSink { get; set; }

    public static LogFile Instance => Shared.Value;
    public static string FilePath => Shared.Value.FilePath;
    public static bool Verbose => Shared.Value.Verbose;
    public static void SetVerbose(bool on) => Shared.Value.SetVerbose(on);

    public static void Write(string message) => Shared.Value.Write(message);
    public static void Debug(string area, string message) => Shared.Value.Debug(area, message);
    public static void Debug(string area, Func<string> message) => Shared.Value.Debug(area, message);
    public static void Info(string area, string message) => Shared.Value.Info(area, message);
    public static void Warn(string area, string message) => Shared.Value.Warn(area, message);
    public static void Error(string area, string message, Exception? ex = null) => Shared.Value.Error(area, message, ex);
    public static void RegisterSecret(string? value) => Shared.Value.RegisterSecret(value);
    public static List<string> Tail(int count, string? filter = null) => Shared.Value.Tail(count, filter);
    public static void Open() => Shared.Value.Open();
    public static void Flush(int timeoutMs = 5000) => Shared.Value.Flush(timeoutMs);
    public static void WriteHeader(string mode, string version, IEnumerable<(string Name, string Value)> settings) =>
        Shared.Value.WriteHeader(mode, version, settings);
}
