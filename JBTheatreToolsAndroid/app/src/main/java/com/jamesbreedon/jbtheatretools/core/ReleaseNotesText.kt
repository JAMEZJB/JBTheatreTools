package com.jamesbreedon.jbtheatretools.core

/**
 * Turns a release's GitHub-flavoured Markdown body into readable plain text for the in-app release notes — the
 * same rules (and the same patterns) as the macOS and Windows launchers: headings lose their #s, bullets become
 * "•", emphasis / code ticks / links collapse to their text, images, HTML comments and tags go, fenced code keeps
 * its lines verbatim, runs of blank lines collapse to one, and the result is capped.
 */
object ReleaseNotesText {
    const val MAX_LENGTH = 20_000
    const val EMPTY = "No notes for this release."

    private val comment = Regex("""<!--.*?-->""", RegexOption.DOT_MATCHES_ALL)
    private val fence = Regex("""^\s*(```|~~~)""")
    private val heading = Regex("""^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$""")
    private val rule = Regex("""^\s*([-*_=])(\s*\1){2,}\s*$""")
    private val quote = Regex("""^\s*>\s?""")
    private val bullet = Regex("""^(\s*)[-*+]\s+(?:\[[ xX]\]\s+)?(.*)$""")
    private val image = Regex("""!\[([^\]]*)\]\([^)]*\)""")
    private val link = Regex("""\[([^\]]+)\]\(([^)]+)\)""")
    private val autoLink = Regex("""<(https?://[^>\s]+)>""")
    private val tag = Regex("""</?[A-Za-z][^>]*>""")
    private val bold = Regex("""\*\*(.+?)\*\*""")
    private val boldUnderscore = Regex("""__(.+?)__""")
    private val strike = Regex("""~~(.+?)~~""")
    private val italic = Regex("""(?<![A-Za-z0-9_*])\*(?!\s)(.+?)(?<!\s)\*(?![A-Za-z0-9_*])""")
    private val italicUnderscore = Regex("""(?<![A-Za-z0-9_])_(?!\s)(.+?)(?<!\s)_(?![A-Za-z0-9_])""")
    private val code = Regex("""`([^`]+)`""")
    private val escape = Regex("""\\([\\`*_{}\[\]()#+\-.!>~|])""")

    fun plain(markdown: String?): String {
        if (markdown.isNullOrBlank()) return EMPTY
        var text = markdown.replace("\r\n", "\n").replace('\r', '\n')
        text = comment.replace(text, "")

        val lines = ArrayList<String>()
        var inFence = false
        for (raw in text.split('\n')) {
            var line = raw.trimEnd()
            if (fence.containsMatchIn(line)) { inFence = !inFence; continue }
            if (inFence) { lines.add(line); continue }
            if (rule.matches(line)) { lines.add(""); continue }
            heading.matchEntire(line)?.let { line = it.groupValues[1] }
            line = quote.replaceFirst(line, "")
            bullet.matchEntire(line)?.let { line = it.groupValues[1] + "• " + it.groupValues[2] }
            val processed = inline(line).trimEnd()
            // A line that held only an image / tag is dropped, not turned into a paragraph break.
            if (processed.isBlank() && line.isNotBlank()) continue
            lines.add(processed)
        }

        val sb = StringBuilder()
        var pendingBlank = false
        for (l in lines) {
            if (l.isBlank()) { pendingBlank = sb.isNotEmpty(); continue }
            if (pendingBlank) sb.append('\n')
            if (sb.isNotEmpty()) sb.append('\n')
            sb.append(l)
            pendingBlank = false
        }
        var result = sb.toString()
        if (result.isEmpty()) return EMPTY
        if (result.length > MAX_LENGTH) result = result.substring(0, MAX_LENGTH).trimEnd() + "\n…"
        return result
    }

    private fun inline(input: String): String {
        var s = input
        s = image.replace(s, "")
        s = link.replace(s, "$1")
        s = autoLink.replace(s, "$1")
        s = tag.replace(s, "")
        s = bold.replace(s, "$1")
        s = boldUnderscore.replace(s, "$1")
        s = strike.replace(s, "$1")
        s = italic.replace(s, "$1")
        s = italicUnderscore.replace(s, "$1")
        s = code.replace(s, "$1")
        s = escape.replace(s, "$1")
        return s.replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"")
            .replace("&#39;", "'").replace("&nbsp;", " ").replace("&amp;", "&")
    }
}
