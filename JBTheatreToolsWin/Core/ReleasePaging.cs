namespace JBTheatreTools;

/// <summary>How far the releases list is read. GitHub lists releases newest first, a page at a time. One page is
/// nearly always enough; but an app with a long run of development builds since its last full release would have that
/// release fall off the first page, and a launcher that isn't on development builds would then see no release at all.
/// So further pages are read only while every page so far was full AND none of them held a full release (published,
/// not a pre-release, not a development build), up to <see cref="MaxPages"/>.</summary>
public static class ReleasePaging
{
    public const int PageSize = 50;
    public const int MaxPages = 10;

    /// <summary>The non-draft releases from <paramref name="fetchPage"/> (pages count from 1; drafts included in what
    /// it returns), newest first, each tag once. An error on the first page is thrown; an error on a later page ends
    /// the reading with what the earlier pages gave, which is never less than the single page read before.</summary>
    public static async Task<List<T>> CollectAsync<T>(Func<int, Task<List<T>>> fetchPage, Func<T, string> tag,
        Func<T, bool> isDraft, Func<T, bool> isPrerelease, int pageSize = PageSize, int maxPages = MaxPages)
    {
        bool IsFull(T r) => !isDraft(r) && !isPrerelease(r) && !VersionCompare.IsDev(tag(r));
        var batch = await fetchPage(1).ConfigureAwait(false);
        var all = new List<T>(batch);
        for (int page = 2; batch.Count >= pageSize && page <= maxPages && !all.Any(IsFull); page++)
        {
            try { batch = await fetchPage(page).ConfigureAwait(false); }
            catch (Exception) { break; }
            all.AddRange(batch);
        }
        // A release published between two page reads moves the rest down one, so a tag can come back twice.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return all.Where(r => !isDraft(r) && seen.Add(tag(r))).ToList();
    }
}
