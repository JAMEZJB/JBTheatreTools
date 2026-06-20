using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JBTheatreTools;

public sealed class ReleaseAsset
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
}

public sealed class ReleaseInfo
{
    [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
    [JsonPropertyName("assets")] public List<ReleaseAsset> Assets { get; set; } = new();
    [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    [JsonPropertyName("draft")] public bool Draft { get; set; }
}

public enum GitHubErrorKind { NoRelease, NotAccessible, Unauthorized, Http, AssetNotFound, Bad }

public sealed class GitHubException : Exception
{
    public GitHubErrorKind Kind { get; }
    public GitHubException(GitHubErrorKind kind, string message) : base(message) { Kind = kind; }
}

/// <summary>
/// Talks to the GitHub REST API with a personal access token.
///
/// Private release assets cannot be fetched from <c>browser_download_url</c>; you must hit the
/// API asset endpoint with <c>Accept: application/octet-stream</c>, follow the 302 to the signed
/// S3 URL, and <b>not</b> forward the Authorization header on that redirect (S3 rejects a request
/// carrying both a Bearer header and its own signed query params). We disable auto-redirect and
/// re-issue the request to S3 without auth.
/// </summary>
public sealed class GitHubClient : IDisposable
{
    private readonly string? _token;
    private readonly HttpClient _http;

    /// <summary>`token` may be null for unauthenticated calls against public repos (self-update check).</summary>
    public GitHubClient(string? token)
    {
        _token = token;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    private HttpRequestMessage NewRequest(string url, string accept)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(_token))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_token}");
        req.Headers.TryAddWithoutValidation("Accept", accept);
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        req.Headers.TryAddWithoutValidation("User-Agent", "JBTheatreTools");
        return req;
    }

    public async Task<ReleaseInfo> LatestReleaseAsync(string owner, string repo)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        using var req = NewRequest(url, "application/vnd.github+json");
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubException(GitHubErrorKind.NoRelease, "No published release found.");
        if (!resp.IsSuccessStatusCode)
            throw new GitHubException(GitHubErrorKind.Http, $"GitHub returned HTTP {(int)resp.StatusCode}.");
        var json = await resp.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ReleaseInfo>(json)
            ?? throw new GitHubException(GitHubErrorKind.Bad, "Unexpected response from GitHub.");
    }

    /// <summary>
    /// Fetches all (non-draft) releases, newest first — used for the version picker and to gate
    /// which apps are shown. The list endpoint returns <c>200 []</c> for an accessible repo with no
    /// releases and <c>404</c> only when the token can't see the repo, so a 404 here means
    /// <b>no access</b> (not "no release") and a 401 means the token itself is bad.
    /// </summary>
    public async Task<List<ReleaseInfo>> ReleasesAsync(string owner, string repo)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=50";
        using var req = NewRequest(url, "application/vnd.github+json");
        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new GitHubException(GitHubErrorKind.Unauthorized, "GitHub token is invalid or expired.");
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new GitHubException(GitHubErrorKind.NotAccessible, "This token can’t access that repository.");
        if (!resp.IsSuccessStatusCode)
            throw new GitHubException(GitHubErrorKind.Http, $"GitHub returned HTTP {(int)resp.StatusCode}.");
        var json = await resp.Content.ReadAsStringAsync();
        var all = JsonSerializer.Deserialize<List<ReleaseInfo>>(json) ?? new();
        return all.Where(r => !r.Draft).ToList();
    }

    public async Task DownloadAssetAsync(string owner, string repo, long assetId, string dest,
                                         IProgress<double>? progress = null)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/assets/{assetId}";
        using var req = NewRequest(url, "application/octet-stream");
        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        try
        {
            // Follow GitHub's 302 to S3 once, WITHOUT the Authorization header.
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location != null)
            {
                var location = resp.Headers.Location;
                resp.Dispose();
                using var s3req = new HttpRequestMessage(HttpMethod.Get, location);
                s3req.Headers.TryAddWithoutValidation("User-Agent", "JBTheatreTools");
                resp = await _http.SendAsync(s3req, HttpCompletionOption.ResponseHeadersRead);
            }
            if (!resp.IsSuccessStatusCode)
                throw new GitHubException(GitHubErrorKind.Http, $"Download failed: HTTP {(int)resp.StatusCode}.");

            var total = resp.Content.Headers.ContentLength ?? -1L;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(dest);
            var buffer = new byte[81920];
            long received = 0;
            int n;
            while ((n = await src.ReadAsync(buffer)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n));
                received += n;
                if (total > 0) progress?.Report((double)received / total);
            }
            progress?.Report(1.0);
        }
        finally
        {
            resp.Dispose();
        }
    }

    public void Dispose() => _http.Dispose();
}
