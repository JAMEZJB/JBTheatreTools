namespace JBTheatreTools;

/// <summary>
/// Normalises a suite passphrase so entry is forgiving: case-insensitive and indifferent to spaces and
/// punctuation. e.g. "Some Phrase!", "some phrase" and "SomePhrase" all reduce to the same token (ASCII
/// letters/digits only, lowercased).
///
/// The launcher sends this normalised form to the download server, which recognises the same normalised
/// phrases. The set of *valid* phrases lives ONLY on the server — never in this public repo — so this helper
/// is deliberately generic: it carries no phrase list, just the reduction rule (identical to the macOS side).
/// </summary>
public static class Passphrase
{
    public static string Normalize(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (char c in raw.ToLowerInvariant())
            if (c <= 0x7F && char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }
}
