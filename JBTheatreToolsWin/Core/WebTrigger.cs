using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace JBTheatreTools;

/// <summary>
/// The suite's web trigger, as the launcher offers it: a small local HTTP endpoint that runs an action by id, so a Show
/// Dashboard (Node-RED) "Saved action" button can drive this computer's launcher. The same contract as every suite app
/// (so one dashboard control works for all of them):
///
///     POST http://&lt;this computer&gt;:&lt;port&gt;/actions/&lt;action-id&gt;[?reply=text]
///     Content-Type: application/json
///     {} or {"token": "…"} when a token is set       (also accepted: X-Token / "Authorization: Bearer")
///
/// Reply: JSON {ok, action, name, summary, seconds, ...}; ?reply=text gives the one-line summary instead. Status: 200 done
/// · 502 it ran but something failed · 400 not a JSON request · 403 token, or a browser-shaped request · 404 no such action
/// · 409 busy (one run at a time, never queued) · 422 can't run as things are. GET /actions lists [{id, name}]; GET / or
/// /health names the app.
///
/// Deliberately narrow: OFF until switched on in Settings; 127.0.0.1 unless another address of this computer is chosen,
/// and every address (0.0.0.0) only with a token; ids only — the request never carries a value, path or text; a request
/// with an Origin header, a non-JSON body or a Host that isn't an IP literal (or localhost) is refused; the token is
/// compared in constant time and never echoed or logged; the port is bound EXCLUSIVELY (on Windows no other program can
/// share it, even with SO_REUSEADDR); it listens in the launcher's window process, or in the hidden --trigger-host copy
/// Stagehand keeps running (TriggerHost.cs) — never in any other command-line run. Whichever of the two has the port
/// answers; the other tries again every 3 s (<see cref="WebTriggerOwner"/>), so the port moves on its own.
/// </summary>
public static class WebTriggerContract
{
    public const string Version = "1.0.0";
    public const int MaxBody = 4096;
    /// <summary>The launcher's web-trigger port in the suite's registry (the apps use 42130-42153).</summary>
    public const int DefaultPort = 42154;
    public const string DefaultBind = "127.0.0.1";
    /// <summary>How often a taken port is tried again (the suite's Owner rule).</summary>
    public const int RetrySeconds = 3;
    private const string IdChars = "abcdefghijklmnopqrstuvwxyz0123456789-._";

    /// <summary>[a-z0-9][a-z0-9._-]{0,63} — the only ids the trigger (and Show Dashboard's control) accept.</summary>
    public static bool ValidActionId(string? actionId)
    {
        var a = actionId ?? "";
        return a.Length is > 0 and <= 64 && char.IsAsciiLetterOrDigit(a[0]) && a.All(c => IdChars.Contains(c));
    }

    /// <summary>A strict IP literal: dotted-quad IPv4 (no short forms, no leading zeros) or IPv6 (no zone).</summary>
    public static IPAddress? ParseIp(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0 || s.Contains('%')) return null;
        if (s.Contains(':'))
            return IPAddress.TryParse(s, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6 ? v6 : null;
        var parts = s.Split('.');
        if (parts.Length != 4) return null;
        foreach (var p in parts)
            if (p.Length is 0 or > 3 || !p.All(char.IsAsciiDigit) || (p.Length > 1 && p[0] == '0') || int.Parse(p, CultureInfo.InvariantCulture) > 255)
                return null;
        return IPAddress.Parse(s);
    }

    /// <summary>'' if (bind, port) is an acceptable trigger address, else why not (plain words for the Settings panel).</summary>
    public static string BindProblem(string? bind, string? port, bool tokenSet)
    {
        if (ParseIp(bind) is not { } addr) return "The trigger address must be an IP address of this computer, e.g. 127.0.0.1.";
        if ((addr.Equals(IPAddress.Any) || addr.Equals(IPAddress.IPv6Any)) && !tokenSet)
            return "Listening on every network (0.0.0.0) needs a token: set one first.";
        if (!int.TryParse((port ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return "The trigger port must be a number.";
        if (n is < 1024 or > 65535) return "The trigger port must be between 1024 and 65535.";
        return "";
    }

    /// <summary>'' or why a new token can't be used.</summary>
    public static string TokenProblem(string token) =>
        token.Length is < 8 or > 200 || token.Any(char.IsControl) ? "A token is 8 to 200 characters on one line." : "";

    /// <summary>The Host header must name an IP literal or localhost — a rebound DNS name never reaches the actions.</summary>
    public static bool HostOk(string? host)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0) return true;   // HTTP/1.0 clients may omit it
        string name;
        if (h.StartsWith('['))
        {
            int end = h.IndexOf(']');
            if (end < 0) return false;
            name = h[1..end];
        }
        else name = h.Count(c => c == ':') == 1 ? h[..h.IndexOf(':')] : h;
        return name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || ParseIp(name) != null;
    }

    /// <summary>True when <paramref name="given"/> is the token — in constant time (both are hashed first, so even the
    /// length isn't revealed).</summary>
    public static bool TokenMatches(string given, string want) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)),
                                                SHA256.HashData(Encoding.UTF8.GetBytes(want)));

    /// <summary>The contract's reply. <paramref name="summary"/> is the one line ?reply=text returns — short and plain.</summary>
    public static JsonObject Reply(bool ok, string action, string name, string summary, double seconds, params (string Key, JsonNode? Value)[] details)
    {
        var o = new JsonObject
        {
            ["ok"] = ok, ["action"] = action, ["name"] = name, ["summary"] = summary, ["seconds"] = Math.Round(seconds, 1),
        };
        foreach (var (k, v) in details) o[k] = v;
        return o;
    }

    /// <summary>Settings, while another program (or a launcher that is still closing) has the port.</summary>
    public static string WaitingText(int port) =>
        $"Waiting for port {port}: another program, or a copy of JB Theatre Tools that is still closing, is using it. " +
        $"Trying again every {RetrySeconds} seconds.";

    public static (int Status, JsonObject Reply) Busy() =>
        (409, new JsonObject { ["ok"] = false, ["error"] = "Another action is already running.", ["summary"] = "busy: another action is running" });

    public static (int Status, JsonObject Reply) NotFound(string actionId) =>
        (404, new JsonObject
        {
            ["ok"] = false, ["action"] = actionId, ["error"] = $"No saved action called '{actionId}'.", ["summary"] = $"no action '{actionId}'",
        });
}

/// <summary>A non-blocking run lock: <see cref="TryEnter"/> is false while another run holds it (answer 409).</summary>
public sealed class OneAtATime
{
    private int _held;
    public bool Busy => Volatile.Read(ref _held) == 1;
    public bool TryEnter() => Interlocked.CompareExchange(ref _held, 1, 0) == 0;
    public void Exit() => Volatile.Write(ref _held, 0);
}

/// <summary>Owns the listening socket. <c>run(actionId, source)</c> does the work (the launcher's own checks, as if the
/// person had pressed the button in the window); <c>listing()</c> → [{id, name}]; <c>token()</c> → '' when none is set;
/// <c>log</c> gets one plain line per start / stop / refusal (never the token).</summary>
public sealed class WebTriggerServer
{
    public delegate (int Status, JsonObject Reply) Runner(string actionId, string source);

    private const int MaxHeaderBytes = 8192;
    private const int MaxConnections = 16;
    private static readonly JsonSerializerOptions JsonOut = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Runner _run;
    private readonly Func<IReadOnlyList<(string Id, string Name)>> _listing;
    private readonly Func<string> _expected;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public string Product { get; }
    public string Address { get; private set; } = "";
    public int Port { get; private set; }
    public string Error { get; set; } = "";
    /// <summary>The last start failed because another program (or a launcher that is still closing) has the port.</summary>
    public bool PortTaken { get; private set; }
    public bool Running => _listener != null;

    public WebTriggerServer(Runner run, Func<IReadOnlyList<(string Id, string Name)>> listing, Func<string> expected,
                            Action<string>? log = null, string product = "JB Theatre Tools")
    {
        _run = run;
        _listing = listing;
        _expected = expected;
        _log = log ?? (_ => { });
        Product = product;
    }

    /// <summary>(Re)starts on bind:port. Returns '' or a plain reason (e.g. the port is taken). Port 0 = the OS picks one
    /// (tests); <see cref="Port"/> is then the real port.</summary>
    public string Start(string bind, int port)
    {
        Stop();
        TcpListener listener;
        try
        {
            var ip = WebTriggerContract.ParseIp(bind) ?? throw new FormatException("not an IP address");
            listener = NewListener(ip, port);
            listener.Start(16);
        }
        catch (Exception ex) when (ex is SocketException or FormatException or ArgumentException)
        {
            PortTaken = ex is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied };
            Error = $"The web trigger couldn't listen on {bind}:{port} ({Reason(ex, port)}).";
            return Error;
        }
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Address = $"{bind}:{Port}";
        Error = "";
        PortTaken = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
        _log($"web trigger listening on {Address}");
        return "";
    }

    /// <summary>The listening socket, not yet bound. A taken port is an error, never shared: SO_EXCLUSIVEADDRUSE on Windows
    /// (never SO_REUSEADDR) — so no other program can bind this port on the same address, and while this listens on every
    /// address (0.0.0.0) none can slip in on a single one of them (127.0.0.1) and take the presses meant for it.</summary>
    public static TcpListener NewListener(IPAddress ip, int port)
    {
        var listener = new TcpListener(ip, port);
        if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
        return listener;
    }

    private static string Reason(Exception ex, int port) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied } =>
            $"port {port} is already in use by another program",
        SocketException { SocketErrorCode: SocketError.AddressNotAvailable } => "that address isn't one of this computer's",
        FormatException => "not an IP address",
        _ => ex.Message.TrimEnd('.'),
    };

    /// <summary>Stops listening. Never waits for a request in progress (it finishes on its own thread), so stopping from
    /// the window's thread can't deadlock against an action that is waiting for that thread.</summary>
    public void Stop()
    {
        var listener = _listener;
        _listener = null;
        _cts?.Cancel();
        _cts = null;
        if (listener != null)
        {
            try { listener.Stop(); } catch (SocketException) { }
            _log($"web trigger stopped ({Address})");
        }
        Address = "";
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }   // stopped
            catch (SocketException)
            {
                if (ct.IsCancellationRequested) return;
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            if (!_connections.Wait(0)) { client.Dispose(); continue; }   // too many at once: drop, never queue
            _ = Task.Run(() =>
            {
                try { Serve(client); }
                catch (Exception) { /* one bad connection never stops the listener */ }
                finally { client.Dispose(); _connections.Release(); }
            });
        }
    }

    // ── One request per connection ───────────────────────────────────────────────────────────────────────────

    private sealed record Request(string Method, string Target, Dictionary<string, string> Headers, byte[]? Body, int Length);

    private void Serve(TcpClient client)
    {
        client.ReceiveTimeout = 5000;
        client.SendTimeout = 5000;
        client.NoDelay = true;
        var stream = client.GetStream();
        var remote = client.Client.RemoteEndPoint as IPEndPoint;
        var ip = remote == null ? "?" : remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4().ToString() : remote.Address.ToString();
        Request? req;
        try { req = ReadRequest(stream); }
        catch (IOException) { return; }
        if (req == null)
        {
            Write(stream, 400, new JsonObject { ["ok"] = false, ["error"] = "Bad request." }, false);
            return;
        }
        var (status, reply, text) = Handle(req, ip);
        Write(stream, status, reply, text);
        Linger(client, stream);
    }

    /// <summary>Closes gently: whatever the client still sends (a body that was too large) is read and dropped first, so
    /// the close doesn't reset the connection before the client has read the answer.</summary>
    private static void Linger(TcpClient client, Stream stream)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
            client.ReceiveTimeout = 1000;
            var sink = new byte[4096];
            for (int total = 0; total < 65536;)
            {
                int n = stream.Read(sink, 0, sink.Length);
                if (n <= 0) break;
                total += n;
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
    }

    private static Request? ReadRequest(Stream stream)
    {
        var buf = new byte[MaxHeaderBytes];
        int have = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (have >= MaxHeaderBytes) return null;
            int n = stream.Read(buf, have, MaxHeaderBytes - have);
            if (n <= 0) return null;
            have += n;
            headerEnd = IndexOf(buf, have, "\r\n\r\n"u8);
        }
        var head = Encoding.Latin1.GetString(buf, 0, headerEnd);
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            var name = line[..colon].Trim();
            if (!headers.ContainsKey(name)) headers[name] = line[(colon + 1)..].Trim();   // the first one counts
        }
        int length = 0;
        if (headers.TryGetValue("Content-Length", out var cl) && !int.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out length))
            length = -1;
        byte[]? body = null;
        if (length is >= 0 and <= WebTriggerContract.MaxBody)
        {
            int start = headerEnd + 4, got = have - start;
            if (got > length) got = length;
            body = new byte[length];
            Array.Copy(buf, start, body, 0, Math.Max(0, got));
            while (got < length)
            {
                int n = stream.Read(body, got, length - got);
                if (n <= 0) return null;
                got += n;
            }
        }
        return new Request(first[0], first[1], headers, body, length);
    }

    private static int IndexOf(byte[] buf, int count, ReadOnlySpan<byte> needle) => buf.AsSpan(0, count).IndexOf(needle);

    private static string? Header(Request r, string name) => r.Headers.TryGetValue(name, out var v) ? v : null;

    private static (string Path, string Query) SplitTarget(string target)
    {
        if (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out var abs))
            target = abs.PathAndQuery;
        int q = target.IndexOf('?');
        return q < 0 ? (target, "") : (target[..q], target[(q + 1)..]);
    }

    private static string QueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string Dec(string s) { try { return Uri.UnescapeDataString(s.Replace('+', ' ')); } catch (Exception) { return s; } }
            if (Dec(pair[..eq]) == key) return Dec(pair[(eq + 1)..]);
        }
        return "";
    }

    private static string Printable(string s) => new(s.Take(64).Select(c => c < ' ' || c == 0x7f ? '?' : c).ToArray());

    private static readonly JsonObject NotFoundRoute = new()
        { ["ok"] = false, ["error"] = "Not found. POST /actions/<action-id> runs a saved action." };

    private (int, JsonObject, bool) Handle(Request req, string ip)
    {
        var (path, query) = SplitTarget(req.Target);
        bool text = QueryValue(query, "reply") == "text";
        if (!string.IsNullOrEmpty(Header(req, "Origin")))
            return (403, new JsonObject { ["ok"] = false, ["error"] = "Requests from a web page are refused." }, text);
        if (!WebTriggerContract.HostOk(Header(req, "Host")))
            return (403, new JsonObject { ["ok"] = false, ["error"] = "Use this computer's IP address (or localhost) in the URL." }, text);
        if (req.Method == "GET")
        {
            var (getStatus, getReply) = Get(path);
            return (getStatus, getReply, text);
        }
        if (req.Method != "POST") return (501, new JsonObject { ["ok"] = false, ["error"] = "Unsupported method." }, text);

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != "actions") return (404, (JsonObject)NotFoundRoute.DeepClone(), text);
        var ctype = (Header(req, "Content-Type") ?? "").Split(';')[0].Trim().ToLowerInvariant();
        if (ctype != "application/json")
            return (400, new JsonObject { ["ok"] = false, ["error"] = "Send a JSON body (e.g. {} or {\"token\": \"…\"})." }, text);
        if (req.Length is < 0 or > WebTriggerContract.MaxBody || req.Body == null)
            return (400, new JsonObject { ["ok"] = false, ["error"] = "The request body is too large." }, text);
        JsonNode? body;
        try { body = req.Body.Length == 0 ? new JsonObject() : JsonNode.Parse(req.Body); }
        catch (JsonException) { return (400, new JsonObject { ["ok"] = false, ["error"] = "The body isn't valid JSON." }, text); }

        string given = "";
        if (body is JsonObject o && o["token"] is JsonValue tv && tv.TryGetValue<string>(out var bodyToken)) given = bodyToken;
        var auth = Header(req, "Authorization") ?? "";
        if (given.Length == 0) given = Header(req, "X-Token") ?? "";
        if (given.Length == 0 && auth.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) given = auth[7..];
        var want = _expected() ?? "";
        if (want.Length > 0 && !WebTriggerContract.TokenMatches(given, want))
        {
            _log($"web trigger: refused /actions/{Printable(parts[1])} from {ip} (wrong or no token)");
            return (403, new JsonObject { ["ok"] = false, ["error"] = "Wrong or missing token." }, text);
        }
        var actionId = parts[1];
        if (!WebTriggerContract.ValidActionId(actionId))
        {
            var (s404, r404) = WebTriggerContract.NotFound(actionId.Length > 64 ? actionId[..64] : actionId);
            return (s404, r404, text);
        }
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        int status;
        JsonObject reply;
        try
        {
            var (s, r) = _run(actionId, $"web trigger from {ip}");
            status = s;
            reply = (JsonObject)r.DeepClone();   // never mutate a shared reply
        }
        catch (Exception ex)
        {
            _log($"web trigger: {actionId} raised {ex.GetType().Name}: {ex.Message}");
            status = 502;
            reply = new JsonObject
            {
                ["ok"] = false, ["action"] = actionId, ["error"] = "The action failed unexpectedly.",
                ["summary"] = $"{actionId}: failed unexpectedly (see the app's log)",
            };
        }
        if (!reply.ContainsKey("seconds")) reply["seconds"] = Math.Round(t0.Elapsed.TotalSeconds, 1);
        return (status, reply, text);
    }

    private (int, JsonObject) Get(string path)
    {
        if (path is "/" or "/health")
            return (200, new JsonObject { ["ok"] = true, ["summary"] = $"{Product} web trigger" });
        if (path == "/actions")
        {
            var items = _listing();
            var arr = new JsonArray();
            foreach (var (id, name) in items) arr.Add(new JsonObject { ["id"] = id, ["name"] = name });
            return (200, new JsonObject
            {
                ["ok"] = true, ["actions"] = arr,
                ["summary"] = items.Count == 0 ? "no saved actions" : string.Join(", ", items.Select(a => a.Id)),
            });
        }
        return (404, (JsonObject)NotFoundRoute.DeepClone());
    }

    private void Write(Stream stream, int status, JsonObject reply, bool text)
    {
        string payload;
        if (text)
        {
            var summary = reply["summary"] is JsonValue sv && sv.TryGetValue<string>(out var s) ? s : "";
            var error = reply["error"] is JsonValue ev && ev.TryGetValue<string>(out var e) ? e : "";
            payload = (summary.Length > 0 ? summary : error) + "\n";
        }
        else payload = reply.ToJsonString(JsonOut) + "\n";
        var body = Encoding.UTF8.GetBytes(payload);
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(ReasonPhrase(status)).Append("\r\n")
            .Append("Server: ").Append(new string(Product.Where(char.IsAsciiLetterOrDigit).ToArray())).Append("-trigger\r\n")
            .Append("Content-Type: ").Append(text ? "text/plain; charset=utf-8" : "application/json; charset=utf-8").Append("\r\n")
            .Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("Connection: close\r\n\r\n");
        try
        {
            stream.Write(Encoding.ASCII.GetBytes(head.ToString()));
            stream.Write(body);
            stream.Flush();
        }
        catch (IOException) { /* the client went away */ }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 409 => "Conflict",
        422 => "Unprocessable Entity", 501 => "Not Implemented", 502 => "Bad Gateway", 503 => "Service Unavailable", _ => "Status",
    };
}

/// <summary>Keeps owning the trigger's port (the suite's "Owner" rule, the same as WebTriggerOwner on macOS): it listens on
/// bind:port, and when the port is TAKEN — a copy of the launcher that is still closing after an update, or another
/// program — it tries again every <c>retry</c> instead of giving up, until the port is free (then it listens) or
/// <see cref="Stop"/> (the trigger switched off, or new settings). Any other problem (an address that isn't this
/// computer's) is reported once and not retried. Every start and stop of the listener happens under one lock, so a retry
/// and a new setting can never both bind. <c>onChange</c> is called (on a pool thread for a retry) on each change of
/// state — the window marshals it.</summary>
public sealed class WebTriggerOwner
{
    public enum OwnerState { Idle, Owning, Waiting, Failed }

    private readonly object _gate = new();
    private readonly TimeSpan _retry;
    /// <summary>Called on each change of state (on a pool thread for a retry).</summary>
    public Action<OwnerState>? OnChange { get; set; }
    private readonly Action<string> _log;
    private CancellationTokenSource? _loop;
    private string _target = "";
    private OwnerState _state = OwnerState.Idle;

    public WebTriggerServer Server { get; }

    public WebTriggerOwner(WebTriggerServer server, TimeSpan? retry = null, Action<OwnerState>? onChange = null,
                           Action<string>? log = null)
    {
        Server = server;
        _retry = retry ?? TimeSpan.FromSeconds(WebTriggerContract.RetrySeconds);
        OnChange = onChange;
        _log = log ?? (_ => { });
    }

    public OwnerState State { get { lock (_gate) return _state; } }
    public bool Waiting => State == OwnerState.Waiting;

    /// <summary>Listens on bind:port. '' = listening now, or waiting for a taken port (<see cref="State"/> says which);
    /// else why not.</summary>
    public string Own(string bind, int port)
    {
        OwnerState now;
        string result;
        lock (_gate)
        {
            var wanted = $"{bind}:{port}";
            if (wanted == _target && ((_state == OwnerState.Owning && Server.Running) || _state == OwnerState.Waiting)) return "";
            CancelLoop();
            _target = wanted;
            var err = Server.Start(bind, port);
            if (err.Length == 0) { now = OwnerState.Owning; result = ""; }
            else if (Server.PortTaken)
            {
                now = OwnerState.Waiting;
                result = "";
                _log($"web trigger: port {port} is taken — trying again every {(int)_retry.TotalSeconds} seconds");
                var cts = new CancellationTokenSource();
                _loop = cts;
                _ = Task.Run(() => RetryAsync(bind, port, cts.Token));
            }
            else { now = OwnerState.Failed; result = err; }
            if (!Set(now)) return result;
        }
        OnChange?.Invoke(now);
        return result;
    }

    private async Task RetryAsync(string bind, int port, CancellationToken ct)
    {
        while (true)
        {
            try { await Task.Delay(_retry, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            OwnerState now;
            lock (_gate)
            {
                if (ct.IsCancellationRequested) return;
                var err = Server.Start(bind, port);
                if (err.Length == 0)
                {
                    _log($"web trigger: port {port} is free again — listening");
                    now = OwnerState.Owning;
                }
                else if (!Server.PortTaken) now = OwnerState.Failed;
                else continue;
                _loop = null;
                if (!Set(now)) return;
            }
            OnChange?.Invoke(now);
            return;
        }
    }

    /// <summary>Stops listening and stops trying (the trigger switched off, or the window is closing). The port closes at
    /// once.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            CancelLoop();
            _target = "";
            Server.Stop();
            if (!Set(OwnerState.Idle)) return;
        }
        OnChange?.Invoke(OwnerState.Idle);
    }

    private void CancelLoop()
    {
        _loop?.Cancel();
        _loop = null;
    }

    private bool Set(OwnerState s)
    {
        if (s == _state) return false;
        _state = s;
        return true;
    }
}
