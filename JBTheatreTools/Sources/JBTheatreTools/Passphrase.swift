import Foundation

/// Normalises a suite passphrase so entry is forgiving: **case-insensitive and indifferent to spaces and
/// punctuation**. e.g. "Some Phrase!", "some phrase" and "SomePhrase" all reduce to the same token (ASCII
/// letters/digits only, lowercased).
///
/// The launcher sends this normalised form to the download server, which recognises the same normalised
/// phrases. The set of *valid* phrases lives ONLY on the server — never in this public repo — so this helper
/// is deliberately generic: it carries no phrase list, just the reduction rule (kept identical on both ends).
enum Passphrase {
    static func normalize(_ raw: String) -> String {
        String(raw.lowercased().filter { $0.isASCII && ($0.isLetter || $0.isNumber) })
    }
}
