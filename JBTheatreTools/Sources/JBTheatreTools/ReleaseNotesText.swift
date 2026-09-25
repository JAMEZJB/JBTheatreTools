import Foundation

/// Turns a release's GitHub-flavoured Markdown body into readable plain text for the in-app release notes — the
/// same rules (and the same ICU/.NET/Java-portable patterns) as the Windows and Android launchers: headings lose
/// their #s, bullets become "•", emphasis / code ticks / links collapse to their text, images, HTML comments and
/// tags go, fenced code keeps its lines verbatim, runs of blank lines collapse to one, and the result is capped.
enum ReleaseNotesText {
    static let maxLength = 20_000
    static let empty = "No notes for this release."

    private static func rx(_ p: String, _ o: NSRegularExpression.Options = []) -> NSRegularExpression {
        // swiftlint:disable:next force_try — the patterns are constants, exercised by the unit tests.
        try! NSRegularExpression(pattern: p, options: o)
    }
    private static let comment = rx(#"<!--.*?-->"#, [.dotMatchesLineSeparators])
    private static let fence = rx(#"^\s*(```|~~~)"#)
    private static let heading = rx(#"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$"#)
    private static let rule = rx(#"^\s*([-*_=])(\s*\1){2,}\s*$"#)
    private static let quote = rx(#"^\s*>\s?"#)
    private static let bullet = rx(#"^(\s*)[-*+]\s+(?:\[[ xX]\]\s+)?(.*)$"#)
    private static let image = rx(#"!\[([^\]]*)\]\([^)]*\)"#)
    private static let link = rx(#"\[([^\]]+)\]\(([^)]+)\)"#)
    private static let autoLink = rx(#"<(https?://[^>\s]+)>"#)
    private static let tag = rx(#"</?[A-Za-z][^>]*>"#)
    private static let bold = rx(#"\*\*(.+?)\*\*"#)
    private static let boldUnderscore = rx(#"__(.+?)__"#)
    private static let strike = rx(#"~~(.+?)~~"#)
    private static let italic = rx(#"(?<![A-Za-z0-9_*])\*(?!\s)(.+?)(?<!\s)\*(?![A-Za-z0-9_*])"#)
    private static let italicUnderscore = rx(#"(?<![A-Za-z0-9_])_(?!\s)(.+?)(?<!\s)_(?![A-Za-z0-9_])"#)
    private static let code = rx(#"`([^`]+)`"#)
    private static let escape = rx(#"\\([\\`*_{}\[\]()#+\-.!>~|])"#)

    private static func replace(_ r: NSRegularExpression, _ s: String, _ template: String) -> String {
        r.stringByReplacingMatches(in: s, range: NSRange(s.startIndex..., in: s), withTemplate: template)
    }

    private static func firstMatch(_ r: NSRegularExpression, _ s: String) -> NSTextCheckingResult? {
        r.firstMatch(in: s, range: NSRange(s.startIndex..., in: s))
    }

    private static func group(_ m: NSTextCheckingResult, _ i: Int, _ s: String) -> String {
        Range(m.range(at: i), in: s).map { String(s[$0]) } ?? ""
    }

    static func plain(_ markdown: String?) -> String {
        guard let markdown, !markdown.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return empty }
        var text = markdown.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n")
        text = replace(comment, text, "")

        var lines: [String] = []
        var inFence = false
        for raw in text.components(separatedBy: "\n") {
            var line = trimEnd(raw)
            if firstMatch(fence, line) != nil { inFence.toggle(); continue }
            if inFence { lines.append(line); continue }
            if firstMatch(rule, line) != nil { lines.append(""); continue }
            if let h = firstMatch(heading, line) { line = group(h, 1, line) }
            line = replace(quote, line, "")
            if let b = firstMatch(bullet, line) { line = group(b, 1, line) + "• " + group(b, 2, line) }
            let processed = trimEnd(inline(line))
            // A line that held only an image / tag is dropped, not turned into a paragraph break.
            if isBlank(processed) && !isBlank(line) { continue }
            lines.append(processed)
        }

        var out = ""
        var pendingBlank = false
        for l in lines {
            if isBlank(l) { pendingBlank = !out.isEmpty; continue }
            if pendingBlank { out += "\n" }
            if !out.isEmpty { out += "\n" }
            out += l
            pendingBlank = false
        }
        if out.isEmpty { return empty }
        // Capped in UTF-16 units, as .NET and Java count string length.
        let utf16 = out.utf16
        if utf16.count > maxLength {
            let cut = String(decoding: Array(utf16.prefix(maxLength)), as: UTF16.self)
            out = trimEnd(cut) + "\n…"
        }
        return out
    }

    private static func inline(_ input: String) -> String {
        var s = input
        s = replace(image, s, "")
        s = replace(link, s, "$1")
        s = replace(autoLink, s, "$1")
        s = replace(tag, s, "")
        s = replace(bold, s, "$1")
        s = replace(boldUnderscore, s, "$1")
        s = replace(strike, s, "$1")
        s = replace(italic, s, "$1")
        s = replace(italicUnderscore, s, "$1")
        s = replace(code, s, "$1")
        s = replace(escape, s, "$1")
        return s.replacingOccurrences(of: "&lt;", with: "<").replacingOccurrences(of: "&gt;", with: ">")
            .replacingOccurrences(of: "&quot;", with: "\"").replacingOccurrences(of: "&#39;", with: "'")
            .replacingOccurrences(of: "&nbsp;", with: " ").replacingOccurrences(of: "&amp;", with: "&")
    }

    private static func trimEnd(_ s: String) -> String {
        var t = Substring(s)
        while let last = t.last, last.isWhitespace { t.removeLast() }
        return String(t)
    }

    private static func isBlank(_ s: String) -> Bool { s.allSatisfy { $0.isWhitespace } }
}
