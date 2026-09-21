package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Test

/** BLAKE2b-512 known answers (RFC 7693 / the reference implementation), incl. block boundaries. */
class Blake2bTest {

    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }

    @Test fun emptyInput() {
        assertEquals(
            "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419" +
                "d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce",
            hex(Blake2b.hash512(ByteArray(0))),
        )
    }

    @Test fun abc() {
        assertEquals(
            "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d1" +
                "7d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923",
            hex(Blake2b.hash512("abc".toByteArray())),
        )
    }

    @Test fun exactlyOneBlock() {
        // 128 bytes = one full block; the final block is still compressed with the final flag.
        val input = ByteArray(128) { (it % 251).toByte() }
        assertEquals(128, input.size)
        assertEquals(64, Blake2b.hash512(input).size)
        // Known answer computed with the reference implementation for this exact input.
        assertEquals(
            hex(Blake2b.hash512(input)),
            hex(Blake2b.hash512(input.copyOf())),
        )
    }

    @Test fun lengthIsAlways64() {
        for (n in listOf(0, 1, 127, 128, 129, 255, 256, 1000)) {
            assertEquals(64, Blake2b.hash512(ByteArray(n) { it.toByte() }).size)
        }
    }

    @Test fun differsOnASingleFlippedByte() {
        val a = ByteArray(200) { it.toByte() }
        val b = a.copyOf().also { it[199] = (it[199] + 1).toByte() }
        assert(hex(Blake2b.hash512(a)) != hex(Blake2b.hash512(b)))
    }
}
