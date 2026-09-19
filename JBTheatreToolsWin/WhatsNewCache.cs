namespace JBTheatreTools;

/// <summary>The last relay-served "New in" document, kept in %APPDATA%\JBTheatreTools\whats-new.json so an
/// offline start shows the latest lines the launcher saw. The cache is the document itself (already validated
/// by <see cref="WhatsNewNotes.Parse"/> before it's saved); a corrupt file just means "bundled lines".</summary>
public static class WhatsNewCache
{
    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JBTheatreTools", "whats-new.json");

    public static WhatsNewNotes? Load()
    {
        try { return File.Exists(FilePath) ? WhatsNewNotes.Parse(File.ReadAllText(FilePath)) : null; }
        catch { return null; }
    }

    public static void Save(string raw)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, raw);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort — the bundled lines still show */ }
    }
}
