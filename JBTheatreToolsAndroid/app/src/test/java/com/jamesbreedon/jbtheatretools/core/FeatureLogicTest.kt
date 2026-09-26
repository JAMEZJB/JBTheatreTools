package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime

/**
 * v1.30 feature logic — the SAME cases as the Windows Core.Tests (FeatureLogicTests.cs) and the macOS XCTests, so
 * the three launchers stay word-for-word identical.
 */
class FeatureLogicTest {

    @Test fun byteSizes() {
        val cases = listOf(
            0L to "0 bytes", 1L to "1 byte", 999L to "999 bytes", 1000L to "1.0 KB", 1049L to "1.0 KB",
            1050L to "1.1 KB", 9_949L to "9.9 KB", 9_950L to "10 KB", 12_400_000L to "12 MB", 4_200_000L to "4.2 MB",
            450_000_000L to "450 MB", 999_499L to "999 KB", 999_500L to "1.0 MB", 1_234_000_000L to "1.2 GB",
            5_000_000_000_000_000L to "5000 TB", -5L to "0 bytes",
        )
        for ((bytes, expected) in cases) assertEquals("bytes=$bytes", expected, ByteSize.format(bytes))
        assertEquals(30L, ByteSize.sum(listOf(10L, -1L, 0L, 20L)))
        assertEquals(Long.MAX_VALUE, ByteSize.sum(listOf(Long.MAX_VALUE - 1, 5L)))
    }

    private val now: Instant = Instant.parse("2026-09-25T12:00:00Z")
    private fun daysAgo(d: Double): Instant = now.minusMillis((d * 86_400_000L).toLong())

    @Test fun relativeAges() {
        val cases = listOf(
            -3.0 to "today", 0.0 to "today", 0.9 to "today", 1.0 to "yesterday", 1.99 to "yesterday",
            2.0 to "2 days ago", 6.5 to "6 days ago", 7.0 to "1 week ago", 13.0 to "1 week ago", 14.0 to "2 weeks ago",
            29.0 to "4 weeks ago", 30.0 to "1 month ago", 59.0 to "1 month ago", 60.0 to "2 months ago",
            364.0 to "12 months ago", 365.0 to "1 year ago", 730.0 to "2 years ago",
        )
        for ((d, expected) in cases) assertEquals("days=$d", expected, RelativeAge.describe(daysAgo(d), now))
        assertEquals(Instant.parse("2026-09-12T08:30:00Z"), RelativeAge.parseIso("2026-09-12T08:30:00Z"))
        assertNull(RelativeAge.parseIso(null))
        assertNull(RelativeAge.parseIso(""))
        assertNull(RelativeAge.parseIso("not a date"))
    }

    @Test fun findAndFilter() {
        assertTrue(AppFilter.matchesQuery("dmx", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"))
        assertTrue(AppFilter.matchesQuery("  tools   DMX ", "DMX Tools", "", null, "dmxtools"))
        assertTrue(AppFilter.matchesQuery("show art-net", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"))
        assertFalse(AppFilter.matchesQuery("dmx cisco", "DMX Tools", "Art-Net monitor", "Show control", "dmxtools"))
        assertTrue(AppFilter.matchesQuery("", "anything"))
        assertTrue(AppFilter.matchesQuery(null, "anything"))
        assertFalse(AppFilter.matchesQuery("x", null, ""))

        assertTrue(AppFilter.matchesStatus(StatusFilter.ALL, false, false, false))
        assertTrue(AppFilter.matchesStatus(StatusFilter.INSTALLED, true, false, false))
        assertFalse(AppFilter.matchesStatus(StatusFilter.INSTALLED, false, false, true))
        assertTrue(AppFilter.matchesStatus(StatusFilter.UPDATES, true, true, false))
        assertFalse(AppFilter.matchesStatus(StatusFilter.UPDATES, true, false, false))
        assertTrue(AppFilter.matchesStatus(StatusFilter.NOT_INSTALLED, false, false, true))
        assertFalse(AppFilter.matchesStatus(StatusFilter.NOT_INSTALLED, false, false, false))
        assertFalse(AppFilter.matchesStatus(StatusFilter.NOT_INSTALLED, true, false, true))

        assertFalse(AppFilter.isActive("  ", StatusFilter.ALL))
        assertTrue(AppFilter.isActive("a", StatusFilter.ALL))
        assertTrue(AppFilter.isActive(null, StatusFilter.UPDATES))
    }

    @Test fun releaseNotes() {
        assertEquals(ReleaseNotesText.EMPTY, ReleaseNotesText.plain(null))
        assertEquals(ReleaseNotesText.EMPTY, ReleaseNotesText.plain(" \n\n "))
        val md = "## What's new\r\n\r\n- **Faster** start-up (`--fast`)\n* Fixed [the crash](https://x.y/z) on _launch_\n  - nested ~~old~~ item\n\n\n\n### Notes\n> Quoted *text*\n\n---\n1. Step one\n![shot](a.png)\n<!-- hidden\ncomment -->Done &amp; dusted <br/>"
        val expected = "What's new\n\n• Faster start-up (--fast)\n• Fixed the crash on launch\n  • nested old item\n\nNotes\nQuoted text\n\n1. Step one\nDone & dusted"
        assertEquals(expected, ReleaseNotesText.plain(md))
        assertEquals("Run:\n**not bold** `x`\nafter", ReleaseNotesText.plain("Run:\n```bash\n**not bold** `x`\n```\nafter"))
        assertEquals("use install_to_applications and 2 * 3 * 4", ReleaseNotesText.plain("use install_to_applications and 2 * 3 * 4"))
        assertEquals("a_b_c", ReleaseNotesText.plain("a_b_c"))
        assertEquals("Task done", ReleaseNotesText.plain("- [x] Task done").trimStart('•', ' '))
        assertEquals("see https://x.y", ReleaseNotesText.plain("see <https://x.y>"))
        assertEquals("1*2", ReleaseNotesText.plain("1\\*2"))
        val long = ReleaseNotesText.plain("a".repeat(30_000))
        assertEquals(ReleaseNotesText.MAX_LENGTH + 2, long.length)
        assertTrue(long.endsWith("\n…"))
    }

    @Test fun scheduledChecks() {
        assertNull(UpdatePolicy.interval("off"))
        assertEquals(Duration.ofHours(1), UpdatePolicy.interval("1h"))
        assertEquals(Duration.ofHours(4), UpdatePolicy.interval("4h"))
        assertEquals(Duration.ofHours(12), UpdatePolicy.interval("12h"))
        assertEquals(Duration.ofHours(24), UpdatePolicy.interval("24h"))
        assertEquals(Duration.ofHours(4), UpdatePolicy.interval("garbage"))
        assertEquals(Duration.ofHours(4), UpdatePolicy.interval(null))
        assertTrue(UpdatePolicy.intervals.any { it.first == UpdatePolicy.DEFAULT_INTERVAL })
        // The desktop wording, shortest first; the default is DEFAULT_INTERVAL, not whatever comes first.
        assertEquals(listOf("Every hour", "Every 4 hours", "Every 12 hours", "Once a day", "Never"), UpdatePolicy.intervals.map { it.second })
        assertEquals(listOf("1h", "4h", "12h", "24h", "off"), UpdatePolicy.intervals.map { it.first })

        assertFalse(UpdatePolicy.isDue(null, now, "off"))
        assertTrue(UpdatePolicy.isDue(null, now, "4h"))
        assertFalse(UpdatePolicy.isDue(now.minus(Duration.ofMinutes(234)), now, "4h"))
        assertTrue(UpdatePolicy.isDue(now.minus(Duration.ofHours(4)), now, "4h"))
        assertTrue(UpdatePolicy.isDue(now.plus(Duration.ofHours(1)), now, "4h"))
        assertFalse(UpdatePolicy.isDue(now.minus(Duration.ofMinutes(59)), now, "1h"))
    }

    @Test fun notifications() {
        val pending = listOf(UpdatePolicy.Pending("dmx", "DMX Tools", "v1.2.0"), UpdatePolicy.Pending("psn", "PSN Tools", "0.4.1"))
        val (toNotify, notified) = UpdatePolicy.notify(pending, listOf("dmx 1.2.0", "gone 9.9.9"))
        assertEquals(listOf("psn"), toNotify.map { it.id })
        assertEquals(listOf("dmx 1.2.0", "psn 0.4.1"), notified)
        assertTrue(UpdatePolicy.notify(pending, notified).first.isEmpty())
        assertEquals(1, UpdatePolicy.notify(listOf(UpdatePolicy.Pending("dmx", "DMX Tools", "v1.3.0")), notified).first.size)
        // A check that failed for dmx keeps its key, so the next good check doesn't announce v1.2.0 again.
        val (_, afterFailure) = UpdatePolicy.notify(pending.drop(1), notified)
        assertEquals(listOf("psn 0.4.1", "dmx 1.2.0"), UpdatePolicy.remembered(afterFailure, notified, setOf("dmx")))
        assertEquals(listOf("psn 0.4.1"), UpdatePolicy.remembered(afterFailure, notified, emptySet()))
        assertTrue(UpdatePolicy.notify(pending, UpdatePolicy.remembered(afterFailure, notified, setOf("dmx"))).first.isEmpty())

        val items = listOf(
            UpdatePolicy.Pending("a", "A", "1.0.0"), UpdatePolicy.Pending("b", "B", "v2.0"),
            UpdatePolicy.Pending("c", "Convert", "build-20260912"), UpdatePolicy.Pending("d", "D", "1"), UpdatePolicy.Pending("e", "E", "1"),
        )
        assertEquals("A v1.0.0, B v2.0, Convert build-20260912 and 2 more", UpdatePolicy.notificationBody(items))
        assertEquals("A v1.0.0", UpdatePolicy.notificationBody(items.take(1)))
        assertEquals("Update available", UpdatePolicy.notificationTitle(1))
        assertEquals("Updates available", UpdatePolicy.notificationTitle(2))
        assertEquals("Updated DMX Tools to v1.2.0", UpdatePolicy.autoUpdateSummary(listOf("DMX Tools" to "1.2.0")))
        assertEquals("Updated 2 apps", UpdatePolicy.autoUpdateSummary(listOf("A" to "1", "B" to "2")))
        assertEquals("v1.2.0", VersionCompare.display("1.2.0"))
        assertEquals("v1.2.0", VersionCompare.display("V1.2.0"))
        assertEquals("build-20260912", VersionCompare.display(" build-20260912 "))
    }

    private val t: Instant = Instant.parse("2026-09-25T13:02:00Z")

    @Test fun historyClassifiesRoundTripsAndCaps() {
        assertEquals("install", ActivityHistory.actionFor(null, "v1.0.0"))
        assertEquals("update", ActivityHistory.actionFor("v1.0.0", "v1.1.0"))
        assertEquals("downgrade", ActivityHistory.actionFor("v1.1.0", "v1.0.0"))
        assertEquals("reinstall", ActivityHistory.actionFor("v1.1.0", "1.1.0"))

        var list: List<ActivityEvent> = emptyList()
        for (i in 0 until ActivityHistory.CAP + 5)
            list = ActivityHistory.append(list, ActivityEvent(t.plusSeconds(60L * i), "dmx", "DMX Tools", "update", "1.0", "1.$i"))
        assertEquals(ActivityHistory.CAP, list.size)
        assertEquals("1.5", list[0].to)
        assertEquals(list, ActivityHistory.parse(ActivityHistory.serialize(list)))

        val e = ActivityHistory.append(emptyList(), ActivityEvent(t, "a", "A", "failed", note = "  " + "x".repeat(300) + " "))[0]
        assertEquals(ActivityHistory.MAX_NOTE_LENGTH + 1, e.note!!.length)
        assertTrue(e.note!!.endsWith("…"))
        assertNull(ActivityHistory.append(emptyList(), ActivityEvent(t, "a", "A", "failed", note = "  "))[0].note)

        assertTrue(ActivityHistory.parse("{not json").isEmpty())
        // A damaged file (content, but not a JSON list) is kept aside before a new one is written; empty / missing isn't.
        assertTrue(ActivityHistory.isDamaged("{not json"))
        assertTrue(ActivityHistory.isDamaged("{}"))
        assertFalse(ActivityHistory.isDamaged("[]"))
        assertFalse(ActivityHistory.isDamaged(ActivityHistory.serialize(list)))
        assertFalse(ActivityHistory.isDamaged(""))
        assertFalse(ActivityHistory.isDamaged(null))
        assertTrue(ActivityHistory.parse("{}").isEmpty())
        assertTrue(ActivityHistory.parse(null).isEmpty())
        val one = ActivityHistory.parse("[{\"at\":\"2026-09-25T13:02:00Z\",\"app\":\"a\",\"action\":\"install\",\"to\":\"1.0\"},{\"app\":\"b\"},7]")
        assertEquals(1, one.size)
        assertEquals("a", one[0].name)
    }

    @Test fun historyWording() {
        assertEquals("Installed DMX Tools v1.0.0", ActivityHistory.describe(ActivityEvent(t, "d", "DMX Tools", "install", null, "1.0.0")))
        assertEquals("Updated DMX Tools v1.0.0 → v1.1.0", ActivityHistory.describe(ActivityEvent(t, "d", "DMX Tools", "update", "v1.0.0", "v1.1.0")))
        assertEquals("Rolled back DMX Tools v1.1.0 → v1.0.0", ActivityHistory.describe(ActivityEvent(t, "d", "DMX Tools", "downgrade", "1.1.0", "1.0.0")))
        assertEquals("Reinstalled DMX Tools v1.0.0", ActivityHistory.describe(ActivityEvent(t, "d", "DMX Tools", "reinstall", "1.0.0", "1.0.0")))
        assertEquals("Removed DMX Tools v1.0.0", ActivityHistory.describe(ActivityEvent(t, "d", "DMX Tools", "uninstall", "1.0.0")))
        assertEquals("Removed DMX Tools", ActivityHistory.describe(ActivityEvent(t, "d", "DMX Tools", "uninstall")))
        assertEquals("Couldn't install DMX Tools v1.1.0: offline", ActivityHistory.describe(ActivityEvent(t, "d", "DMX Tools", "failed", null, "1.1.0", "offline")))
        // A cancelled install dialog is recorded as cancelled — not as a failure.
        assertEquals("Cancelled installing DMX Tools v1.1.0", ActivityHistory.describe(ActivityEvent(t, "d", "DMX Tools", "cancelled", null, "1.1.0")))
        val nowLocal = LocalDateTime.of(2026, 9, 25, 18, 0)
        assertEquals("Today 14:02", ActivityHistory.`when`(LocalDateTime.of(2026, 9, 25, 14, 2), nowLocal))
        assertEquals("Yesterday 09:10", ActivityHistory.`when`(LocalDateTime.of(2026, 9, 24, 9, 10), nowLocal))
        assertEquals("2 Sep 2026 07:05", ActivityHistory.`when`(LocalDateTime.of(2026, 9, 2, 7, 5), nowLocal))
    }

    private val catalog = listOf(
        SetupPlanner.CatalogEntry("dmx", "DMX Tools", emptyList()),
        SetupPlanner.CatalogEntry("ndi", "NDI Tools", listOf("standard" to "Light", "full" to "Full")),
        SetupPlanner.CatalogEntry("psn", "PSN Tools", emptyList()),
    )

    private fun sample() = SetupProfile(
        createdAt = "2026-09-25T12:00:00Z", createdBy = "JB Theatre Tools 1.30.0 (Windows)",
        apps = listOf(
            SetupProfile.Entry("dmx", null, "v1.2.0", true),
            SetupProfile.Entry("ndi", "standard", "v2.0.0", false),
            SetupProfile.Entry("ndi", "full", "v2.0.0", false),
            SetupProfile.Entry("psn", null, "0.4.1", false),
            SetupProfile.Entry("gone", null, "1.0", false),
            SetupProfile.Entry("ndi", "ultra", "1.0", false),
        ),
        layout = SetupProfile.Layout(listOf("dmx"), listOf("psn"), listOf("ndi", "dmx"), listOf("Networking"), emptyList()),
    )

    @Test fun setupRoundTripsAndRejectsForeignFiles() {
        val p = SetupProfile.parse(sample().serialize())
        assertEquals(sample(), p)
        for (bad in listOf(
            "[]",
            "{\"kind\":\"something-else\",\"schemaVersion\":1,\"apps\":[]}",
            "{\"kind\":\"jbtheatretools-setup\",\"apps\":[]}",
            "{\"kind\":\"jbtheatretools-setup\",\"schemaVersion\":2,\"apps\":[]}",
            "{\"kind\":\"jbtheatretools-setup\",\"schemaVersion\":1}",
            "not json",
            " ".repeat(SetupProfile.MAX_BYTES + 1),
        )) {
            try { SetupProfile.parse(bad); fail("accepted: ${bad.take(40)}") } catch (_: SetupProfile.FormatError) { }
        }
        val junk = SetupProfile.parse("{\"kind\":\"jbtheatretools-setup\",\"schemaVersion\":1,\"apps\":[7,{\"id\":\"\"},{\"id\":\" dmx \",\"variant\":\"\",\"held\":\"yes\"}]}")
        assertEquals(listOf(SetupProfile.Entry("dmx", null, null, false)), junk.apps)
        assertNull(junk.layout)
    }

    @Test fun setupPlans() {
        val plan = SetupPlanner.build(sample(), catalog, setOf("psn"))
        assertEquals(listOf("DMX Tools", "NDI Tools", "NDI Tools (Full)"), plan.toInstall.map { it.label })
        assertEquals("v1.2.0", plan.toInstall[0].tag)
        assertNull(plan.toInstall[1].tag)
        assertNull(plan.toInstall[1].variantId)
        assertEquals("full", plan.toInstall[2].variantId)
        assertEquals(listOf("PSN Tools"), plan.alreadyInstalled)
        assertEquals(listOf("dmx"), plan.holdIds)
        assertEquals(2, plan.skipped.size)
        assertTrue("gone — not in this launcher's catalog" in plan.skipped)
        assertTrue("NDI Tools (ultra) — no such edition" in plan.skipped)

        val android = SetupPlanner.build(sample(), catalog, emptySet(), supportsVariants = false)
        assertTrue(android.toInstall.none { it.variantId != null })
        assertTrue("NDI Tools (Full) — not available here" in android.skipped)

        val twice = SetupProfile(apps = listOf(SetupProfile.Entry("dmx", null, null, false), SetupProfile.Entry("dmx", null, null, false)))
        assertEquals(1, SetupPlanner.build(twice, catalog, emptySet()).toInstall.size)

        val s = SetupPlanner.summary(plan)
        assertTrue(s.startsWith("Install 3 apps:\n  • DMX Tools v1.2.0 (held)\n  • NDI Tools\n  • NDI Tools (Full)"))
        assertTrue(s.contains("Already installed (left as they are): PSN Tools"))
        assertTrue(s.contains("Held at their versions: 1 app"))
        assertTrue(s.contains("Skipped:\n  • "))
        assertTrue(SetupPlanner.summary(SetupPlanner.build(SetupProfile(), catalog, emptySet())).startsWith("Nothing to install"))
        assertEquals("JB Theatre Tools setup 2026-09-25.json", SetupProfile.suggestedFileName(LocalDate.of(2026, 9, 25)))
    }

    @Test fun setupDevBuildsNeedTheSwitch() {
        val profile = SetupProfile(apps = listOf(
            SetupProfile.Entry("dmx", null, "v1.3.0-dev.2", true),
            SetupProfile.Entry("psn", null, "0.4.1", true),
            SetupProfile.Entry("ndi", null, "v2.1.0-dev.1", false),   // not held: installs the latest, the tag is ignored
        ))
        // Development builds off here: the held dev build is skipped — not installed and NOT held.
        val off = SetupPlanner.build(profile, catalog, emptySet(), supportsVariants = false, allowDevTags = false)
        assertEquals(listOf("PSN Tools", "NDI Tools"), off.toInstall.map { it.label })
        assertEquals(listOf("psn"), off.holdIds)
        assertEquals(listOf("DMX Tools v1.3.0-dev.2 — a development build (not switched on here)"), off.skipped)
        assertTrue(off.toInstall.none { it.tag != null && VersionCompare.isDev(it.tag!!) })
        // Switched on: it installs at that build and is held.
        val on = SetupPlanner.build(profile, catalog, emptySet(), supportsVariants = false, allowDevTags = true)
        assertEquals("v1.3.0-dev.2", on.toInstall.first { it.appId == "dmx" }.tag)
        assertEquals(listOf("dmx", "psn"), on.holdIds)
        // Already installed + held at a dev build, switch off: still skipped (no hold is set).
        assertTrue(SetupPlanner.build(profile, catalog, setOf("dmx"), allowDevTags = false).holdIds == listOf("psn"))
    }

    @Test fun setupPreviewWordingPerPlatform() {
        val plan = SetupPlanner.build(sample(), catalog, setOf("psn"), supportsVariants = false)
        val android = SetupPlanner.summary(plan, SetupPlanner.Wording.ANDROID)
        assertTrue(android.contains("— Update all leaves them at the version this device has"))
        assertFalse(android.contains("automatic"))
        assertFalse(android.contains("machine"))
        assertTrue(SetupPlanner.summary(SetupPlanner.build(SetupProfile(), catalog, emptySet()), SetupPlanner.Wording.ANDROID)
            .startsWith("Nothing to install — this device already has every app in the file."))
        // The desktop wording is unchanged (and the default).
        assertTrue(SetupPlanner.summary(plan).contains("— Update All and automatic updates leave them at the version this machine has"))
    }

    @Test fun userMessagesCarryNoHostNames() {
        val host = "jbtheatretools.jamesbreedon.com"
        for (e in listOf(
            java.net.UnknownHostException("Unable to resolve host \"$host\": No address associated with hostname"),
            java.net.ConnectException("Failed to connect to $host/1.2.3.4:443"),
            java.net.SocketTimeoutException("timeout reading from $host"),
            javax.net.ssl.SSLHandshakeException("Chain validation failed for $host"),
            java.io.IOException("unexpected end of stream on $host"),
            RuntimeException("boom at $host"),
        )) {
            val m = UserMessage.of(e)
            assertFalse(m, m.contains(host))
            assertFalse(m, m.contains("1.2.3.4"))
        }
        // The launcher's own refusals pass through as they are.
        assertEquals("this release publishes no SHA256SUMS checksums", UserMessage.of(IllegalStateException("this release publishes no SHA256SUMS checksums")))
    }

    @Test fun diskSpace() {
        assertEquals(3 * 100_000_000L + DiskSpace.MARGIN, DiskSpace.required(100_000_000, "NDITools-Full-macOS-arm64.zip"))
        assertEquals(2 * 100_000_000L + DiskSpace.MARGIN, DiskSpace.required(100_000_000, "DMXTools-android-arm64.apk"))
        assertEquals(DiskSpace.MARGIN, DiskSpace.required(0, "x.zip"))
        assertEquals(Long.MAX_VALUE, DiskSpace.required(Long.MAX_VALUE / 2, "x.zip"))
        assertNull(DiskSpace.shortfall(1000, 1000))
        assertNull(DiskSpace.shortfall(1000, -1))
        assertEquals("Not enough disk space — needs about 1.2 GB, 300 MB free.", DiskSpace.shortfall(1_200_000_000, 300_000_000))
    }

    @Test fun diagnosticsCarryNoSecrets() {
        val log = (1..50).map { "line $it" } + listOf(
            "oops Authorization: Bearer ghp_abcdefghijklmnop leaked",
            "pat github_pat_11ABCDEFG0123456789_abcdef here",
        )
        val report = Diagnostics.build(Diagnostics.Info(
            "1.30.0", "Windows 11", "x64", "Download server", "jbtheatretools.jamesbreedon.com", false, true,
            "Launcher only", listOf(Diagnostics.AppLine("DMX Tools", "v1.1.0", "v1.2.0", "Update", true)), log,
            Instant.parse("2026-09-25T12:00:00Z"),
        ))
        assertTrue(report.startsWith("JB Theatre Tools diagnostics — 2026-09-25 12:00:00 UTC\nLauncher: v1.30.0\nSystem: Windows 11 (x64)\n"))
        assertTrue(report.contains("Downloads via: Download server (jbtheatretools.jamesbreedon.com)\n"))
        assertTrue(report.contains("Show lock: on · Development builds: off\n"))
        assertTrue(report.contains("  DMX Tools — installed v1.1.0, latest v1.2.0, Update, held\n"))
        assertTrue(report.contains("Recent log (40 lines):"))
        assertFalse(report.contains("line 12\n"))
        assertTrue(report.contains("line 13\n"))
        assertFalse(report.contains("ghp_abcdefghijklmnop"))
        assertFalse(report.contains("github_pat_11ABCDEFG"))
        assertTrue(report.contains("[redacted]"))
        assertEquals("Basic [redacted]", Diagnostics.redact("Basic c3VpdGU6cGFzcw=="))
        assertEquals("installed dmx v1.2.0", Diagnostics.redact("installed dmx v1.2.0"))
        assertEquals("refresh: token rejected (token mode)", Diagnostics.redact("refresh: token rejected (token mode)"))
    }

    @Test fun launcherWhatsNew() {
        assertFalse(LauncherWhatsNew.shouldShow(null, "1.30.0"))
        assertFalse(LauncherWhatsNew.shouldShow("", "1.30.0"))
        assertFalse(LauncherWhatsNew.shouldShow("1.30.0", "1.30.0"))
        assertFalse(LauncherWhatsNew.shouldShow("1.31.0", "1.30.0"))
        assertTrue(LauncherWhatsNew.shouldShow("1.29.1", "1.30.0"))
        assertTrue(LauncherWhatsNew.shouldShow("1.30.0-dev.2", "1.30.0"))
        // Nothing recorded: an update only when the launcher was already installed (pre-1.30 recorded nothing).
        assertTrue(LauncherWhatsNew.shouldShow(null, "1.30.0", existingInstall = true))
        assertTrue(LauncherWhatsNew.shouldShow("", "1.30.0", existingInstall = true))
        assertFalse(LauncherWhatsNew.shouldShow("1.30.0", "1.30.0", existingInstall = true))
    }
}
