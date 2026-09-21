package com.jamesbreedon.jbtheatretools.core

/**
 * Normalises a suite passphrase so entry is forgiving: **case-insensitive and indifferent to spaces
 * and punctuation**. e.g. "Some Phrase!", "some phrase" and "SomePhrase" all reduce to the same token
 * (ASCII letters/digits only, lowercased).
 *
 * The launcher sends this normalised form to the download server, which recognises the same normalised
 * phrases. The set of *valid* phrases lives ONLY on the server — never in this public repo — so this
 * helper is deliberately generic: it carries no phrase list, just the reduction rule. Identical to the
 * macOS `Passphrase.normalize` and the Windows `Passphrase.Normalize`; the three are tested against the
 * same cases so the three launchers always produce the same token.
 */
object Passphrase {
    fun normalize(raw: String): String = buildString(raw.length) {
        for (c in raw.lowercase()) {
            if (c.code <= 0x7F && (c.isLetter() || c.isDigit())) append(c)
        }
    }
}
