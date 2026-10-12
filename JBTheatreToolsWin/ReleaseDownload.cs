namespace JBTheatreTools;

/// <summary><c>--download</c>: fetches one release asset (any platform's) plus that release's SHA256SUMS and
/// SHA256SUMS.minisig into a folder, for a PC that has no internet of its own (Stagehand copies the three files over and
/// runs <c>--install-file</c> there). They're checked with <see cref="InstallManager.VerifyLocalFile"/>, the very check
/// <c>--install-file</c> makes, plus the size the release lists. The files land in a hidden staging folder inside the
/// target first and are moved into place only once all three check out, so a failure leaves nothing behind (and never
/// touches a file that was already there).</summary>
public static class ReleaseDownload
{
    public const string SumsName = "SHA256SUMS";
    public const string SigName = "SHA256SUMS.minisig";

    public sealed record Result(string Path, string Sums, string Sig, long Size, string Sha256);

    /// <summary>A release file name that is safe to write into the folder as is (no folders, no "..", not the manifest).</summary>
    public static bool IsPlainAssetName(string name) =>
        name.Length > 0 && name == PathSafe.Component(name) && name is not ("SHA256SUMS" or "SHA256SUMS.minisig")
        && !name.StartsWith('.');

    /// <summary>Downloads <paramref name="assetName"/>, SHA256SUMS and SHA256SUMS.minisig into <paramref name="folder"/>
    /// and verifies them for "&lt;repo&gt; &lt;tag&gt;". <paramref name="fetch"/>(name, dest) writes the release file
    /// <c>name</c> to <c>dest</c> (the CLI passes the launcher's own client). <paramref name="expectedSize"/> is the size
    /// the release lists for the asset (0 = don't check).</summary>
    public static async Task<Result> RunAsync(string folder, string assetName, long expectedSize, string repo, string tag,
                                              Func<string, string, Task> fetch, string? publicKey = null)
    {
        if (!IsPlainAssetName(assetName)) throw new ArgumentException($"\"{assetName}\" isn't a release file name.");
        folder = System.IO.Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        var stage = System.IO.Path.Combine(folder, $".download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            var names = new[] { assetName, SumsName, SigName };
            foreach (var name in names)
            {
                var dest = System.IO.Path.Combine(stage, name);
                await fetch(name, dest);
                if (!File.Exists(dest)) throw new HttpRequestException($"The download of {name} left no file.");
            }
            var file = System.IO.Path.Combine(stage, assetName);
            var size = new FileInfo(file).Length;
            if (expectedSize > 0 && size != expectedSize)
                throw new InstallVerificationException($"{assetName} is {size} bytes but the release lists {expectedSize}.");
            InstallManager.VerifyLocalFile(file, assetName, System.IO.Path.Combine(stage, SumsName), repo, tag, publicKey);
            var sha = InstallManager.Sha256Hex(file);

            // All three check out: move into place (same folder tree, so each move is a rename). Should one fail, take
            // back the ones already placed, so the folder never holds an asset beside a SHA256SUMS from somewhere else.
            var placed = new List<string>();
            try
            {
                foreach (var name in names)
                {
                    var to = System.IO.Path.Combine(folder, name);
                    File.Move(System.IO.Path.Combine(stage, name), to, overwrite: true);
                    placed.Add(to);
                }
            }
            catch
            {
                foreach (var p in placed) InstallManager.TryDelete(p);
                throw;
            }
            return new Result(System.IO.Path.Combine(folder, assetName), System.IO.Path.Combine(folder, SumsName),
                              System.IO.Path.Combine(folder, SigName), size, sha);
        }
        finally
        {
            try { Directory.Delete(stage, recursive: true); } catch { /* best effort */ }
        }
    }
}
