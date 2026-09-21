package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * The passphrase reduction rule (case-insensitive; spaces & punctuation ignored). These are the SAME
 * cases the macOS `PassphraseTests.swift` and the Windows `PassphraseTests.cs` assert, so the three
 * launchers provably produce the same token for the same typing.
 *
 * Inputs are SYNTHETIC — the real suite phrases are secrets and never appear in this public repo.
 */
class PassphraseTest {

    @Test fun lowercases() {
        assertEquals("hello", Passphrase.normalize("HELLO"))
        assertEquals("hello", Passphrase.normalize("HeLLo"))
    }

    @Test fun stripsSpacesAndPunctuation() {
        assertEquals("helloworld", Passphrase.normalize("hello world"))
        assertEquals("helloworld", Passphrase.normalize("  hello,  world!  "))
        assertEquals("dontpanic", Passphrase.normalize("don't panic"))
        assertEquals("abcd", Passphrase.normalize("a-b_c.d"))
    }

    @Test fun caseAndSpacingVariantsCollapseToOneToken() {
        val variants = listOf("Foo Bar Baz", "foo bar baz", "FOOBARBAZ", "  foo,bar.baz  ", "Foo-Bar-Baz")
        assertEquals(setOf("foobarbaz"), variants.map { Passphrase.normalize(it) }.toSet())
    }

    @Test fun keepsDigits() {
        assertEquals("studio54", Passphrase.normalize("Studio 54"))
    }

    @Test fun allPunctuationYieldsEmpty() {
        assertEquals("", Passphrase.normalize("!!! ,.- "))
    }

    @Test fun nonAsciiLettersAreDropped() {
        // The rule is ASCII letters/digits only — the same filter the other two launchers apply.
        assertEquals("caf", Passphrase.normalize("Café"))
        assertEquals("", Passphrase.normalize("こんにちは"))
    }
}
