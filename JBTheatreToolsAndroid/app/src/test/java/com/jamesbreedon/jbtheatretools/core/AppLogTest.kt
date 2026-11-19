package com.jamesbreedon.jbtheatretools.core

import java.io.File
import java.nio.file.Files
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/** The launcher's log on Android: the switch, the format, rotation, secrets and volume. A temporary folder only. */
class AppLogTest {
    private val dir: File = Files.createTempDirectory("jbtt-applog").toFile()
    private var saved = false

    @After fun cleanUp() { dir.deleteRecursively() }

    private fun make() = AppLog(File(dir, "JBTheatreTools.log")) { saved }

    private fun text(log: AppLog): String { log.flush(); return if (log.path.exists()) log.path.readText() else "" }

    @Test fun theSwitchChangesTheLevelAtOnceAndIsReadFromTheSavedSetting() {
        val log = make()
        assertFalse(log.verbose)
        log.debug("test", "before the switch")
        saved = true
        log.noteVerboseChanged(true)
        log.debug("test", "after the switch")
        val t = text(log)
        assertFalse("before the switch" in t)
        assertTrue("after the switch" in t)
        assertTrue("[settings] verbose logging off → on" in t)
        // a new process reads the saved setting
        assertTrue(make().verbose)
    }

    @Test fun lineFormat() {
        val log = make()
        log.info("github", "one event")
        log.error("github", "it failed", IllegalStateException("synthetic failure"))
        val lines = text(log).lines().filter { it.isNotEmpty() }
        assertTrue(lines[0], Regex("""^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}  (DEBUG|INFO |WARN |ERROR)  \[[a-z-]+\] .+""").matches(lines[0]))
        assertTrue("  INFO   [github] one event" in lines[0])
        assertTrue("  ERROR  [github] it failed" in lines[1])
        assertTrue(lines[2].startsWith("    IllegalStateException: synthetic failure"))
    }

    @Test fun anOldStyleLineGetsAnAreaAndALevel() {
        val log = make()
        log.log("install helo FAILED: boom")
        log.log("web trigger: not listening")
        val t = text(log)
        assertTrue("WARN   [install] install helo FAILED" in t)
        assertTrue("INFO   [web-trigger] web trigger: not listening" in t)
    }

    @Test fun aLongLineIsCut() {
        val log = make()
        log.info("test", "x".repeat(5000))
        val line = text(log).lines().first()
        assertTrue(line.length <= AppLog.MAX_LINE + 60)
        assertTrue(line.endsWith("…"))
    }

    @Test fun theLogRollsAndKeepsTwoOlderFiles() {
        val log = make()
        val chunk = "y".repeat(1500)
        repeat(2600) { log.info("test", chunk) }
        log.flush()
        assertTrue(File(dir, "JBTheatreTools.log.1").exists())
        assertTrue(File(dir, "JBTheatreTools.log.2").exists())
        assertFalse(File(dir, "JBTheatreTools.log.3").exists())
        assertTrue(log.path.length() <= AppLog.NORMAL_LIMIT)
    }

    @Test fun burstsOfDebugWriteNothingWhenVerboseIsOff() {
        val log = make()
        repeat(5000) { log.debug("test", "packet $it") }
        assertEquals("", text(log))
    }

    @Test fun headerIsWrittenWithTheSettingsThatExplainBehaviour() {
        // The header reads android.os.Build, which a plain JVM test cannot: only check the settings lines via info.
        val log = make()
        log.info("start", "setting download access: server")
        assertTrue("setting download access: server" in text(log))
    }

    @Test fun knownSecretShapesNeverReachTheFile() {
        val log = make()
        saved = true
        log.noteVerboseChanged(true)
        log.registerSecret("placeholder-passphrase-words")
        val token = "ghp_placeholderplaceholder1234"
        val b64 = java.util.Base64.getEncoder().encodeToString("placeholder-passphrase-words".toByteArray())
        val samples = listOf(
            "Authorization: Bearer $token",
            "GET https://example.invalid/x?token=synthetic-secret-value&a=1",
            "credential $token in an exception message",
            "the passphrase was placeholder-passphrase-words and its base64 $b64",
            "https://s3.example.invalid/f?X-Amz-Signature=syntheticsig&X-Amz-Credential=syntheticcred",
        )
        for (s in samples) { log.info("test", s); log.debug("test", s); log.error("test", s, IllegalStateException(s)) }
        val t = text(log)
        for (secret in listOf("ghp_placeholder", "synthetic-secret-value", "placeholder-passphrase-words", "syntheticsig", "syntheticcred", b64)) {
            assertFalse("$secret reached the log", secret in t)
        }
        assertTrue("<redacted>" in t)
    }

    @Test fun tailHonoursTheFilterAndTheLimit() {
        val log = make()
        repeat(500) { log.info("test", "line $it") }
        log.info("other", "needle here")
        assertEquals(400, log.tail(1000).size)
        assertEquals(1, log.tail(5, "needle").size)
    }
}
