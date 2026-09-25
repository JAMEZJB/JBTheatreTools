using System.Text;
using System.Text.RegularExpressions;

namespace JBTheatreTools;

/// <summary>Turns a release's GitHub-flavoured Markdown body into readable plain text for the in-app release
/// notes — the same rules on every launcher (the patterns are portable between .NET, ICU and java.util.regex):
/// headings lose their #s, bullets become "•", emphasis / code ticks / links collapse to their text, images, HTML
/// comments and tags go, fenced code keeps its lines verbatim, runs of blank lines collapse to one, and the result
/// is capped so a pathological body can't bloat the view.</summary>
public static class ReleaseNotesText
{
    public const int MaxLength = 20_000;
    public const string Empty = "No notes for this release.";

    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline);
    private static readonly Regex Fence = new(@"^\s*(```|~~~)");
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$");
    private static readonly Regex Rule = new(@"^\s*([-*_=])(\s*\1){2,}\s*$");
    private static readonly Regex Quote = new(@"^\s*>\s?");
    private static readonly Regex Bullet = new(@"^(\s*)[-*+]\s+(?:\[[ xX]\]\s+)?(.*)$");
    private static readonly Regex Image = new(@"!\[([^\]]*)\]\([^)]*\)");
    private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)]+)\)");
    private static readonly Regex AutoLink = new(@"<(https?://[^>\s]+)>");
    private static readonly Regex Tag = new(@"</?[A-Za-z][^>]*>");
    private static readonly Regex Bold = new(@"\*\*(.+?)\*\*");
    private static readonly Regex BoldUnderscore = new(@"__(.+?)__");
    private static readonly Regex Strike = new(@"~~(.+?)~~");
    private static readonly Regex Italic = new(@"(?<![A-Za-z0-9_*])\*(?!\s)(.+?)(?<!\s)\*(?![A-Za-z0-9_*])");
    private static readonly Regex ItalicUnderscore = new(@"(?<![A-Za-z0-9_])_(?!\s)(.+?)(?<!\s)_(?![A-Za-z0-9_])");
    private static readonly Regex Code = new(@"`([^`]+)`");
    private static readonly Regex Escape = new(@"\\([\\`*_{}\[\]()#+\-.!>~|])");

    public static string Plain(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Empty;
        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Comment.Replace(text, "");

        var lines = new List<string>();
        bool inFence = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (Fence.IsMatch(line)) { inFence = !inFence; continue; }
            if (inFence) { lines.Add(line); continue; }
            if (Rule.IsMatch(line)) { lines.Add(""); continue; }
            var h = Heading.Match(line);
            if (h.Success) line = h.Groups[1].Value;
            line = Quote.Replace(line, "");
            var b = Bullet.Match(line);
            if (b.Success) line = b.Groups[1].Value + "• " + b.Groups[2].Value;
            var processed = Inline(line).TrimEnd();
            // A line that held only an image / tag is dropped, not turned into a paragraph break.
            if (processed.Trim().Length == 0 && line.Trim().Length > 0) continue;
            lines.Add(processed);
        }

        // Collapse runs of blank lines to one and trim blank lines at both ends.
        var sb = new StringBuilder();
        bool pendingBlank = false;
        foreach (var l in lines)
        {
            if (l.Trim().Length == 0) { pendingBlank = sb.Length > 0; continue; }
            if (pendingBlank) sb.Append('\n');
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(l);
            pendingBlank = false;
        }
        var result = sb.ToString();
        if (result.Length == 0) return Empty;
        if (result.Length > MaxLength) result = result[..MaxLength].TrimEnd() + "\n…";
        return result;
    }

    private static string Inline(string s)
    {
        s = Image.Replace(s, "");
        s = Link.Replace(s, "$1");
        s = AutoLink.Replace(s, "$1");
        s = Tag.Replace(s, "");
        s = Bold.Replace(s, "$1");
        s = BoldUnderscore.Replace(s, "$1");
        s = Strike.Replace(s, "$1");
        s = Italic.Replace(s, "$1");
        s = ItalicUnderscore.Replace(s, "$1");
        s = Code.Replace(s, "$1");
        s = Escape.Replace(s, "$1");
        return s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
                .Replace("&#39;", "'").Replace("&nbsp;", " ").Replace("&amp;", "&");
    }
}
