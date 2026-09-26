package com.jamesbreedon.jbtheatretools.core

import android.content.Context
import com.jamesbreedon.jbtheatretools.BuildConfig
import com.jamesbreedon.jbtheatretools.install.ApkInstaller
import com.jamesbreedon.jbtheatretools.install.ApkVerifier
import com.jamesbreedon.jbtheatretools.install.InstalledApps
import com.jamesbreedon.jbtheatretools.net.DownloadCancelledException
import com.jamesbreedon.jbtheatretools.net.GitHubClient
import com.jamesbreedon.jbtheatretools.net.GitHubException
import com.jamesbreedon.jbtheatretools.net.RelayPolicy
import com.jamesbreedon.jbtheatretools.net.ReleaseInfo
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.io.File
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

/** What the launcher knows about one app right now. */
data class AppStatus(
    val app: CatalogApp,
    val installedVersion: String? = null,
    val latestVersion: String? = null,
    val apkAssetName: String? = null,
    val apkSizeBytes: Long = 0,
    val note: String? = null,          // "No Android build", an error line, …
    val whatsNew: String? = null,
    val whatsNewVersion: String? = null,
    /** Dev channel is off but a dev build is still installed, and a release can replace it (see [LauncherRepository]). */
    val backToRelease: Boolean = false,
    /** Not shown in the lists (an app with no release on this device's channel, not installed). */
    val hidden: Boolean = false,
    /** The latest release's publish time (ISO 8601), for "3 days ago". */
    val latestPublished: String? = null,
    /** Held at the installed version: no Update, left out of Update all and notifications. */
    val held: Boolean = false,
    /** This check couldn't reach the release feed for the app (not "no release"): a scheduled check keeps the old row. */
    val checkFailed: Boolean = false,
) {
    val isInstalled: Boolean get() = installedVersion != null
    /** A development build is installed, or is what's on offer (Dev channel on). */
    val isDev: Boolean
        get() = (installedVersion?.let { VersionCompare.isDev(it) } ?: false) ||
            (latestVersion?.let { VersionCompare.isDev(it) } ?: false)
    val hasUpdate: Boolean
        get() = installedVersion != null && latestVersion != null &&
            VersionCompare.isNewer(latestVersion, installedVersion)
    val canInstall: Boolean get() = apkAssetName != null
    /** An update is on offer AND the app isn't held — what Update all, the badges and notifications act on. */
    val updatePending: Boolean get() = hasUpdate && !held
}

/** Progress of one install, for the row and the Updates tab. */
data class InstallProgress(
    val catalogId: String,
    val phase: Phase,
    val fraction: Double = 0.0,
    val message: String? = null,
) {
    enum class Phase { DOWNLOADING, VERIFYING, INSTALLING, DONE, FAILED, CANCELLED }

    /** Still running (a finished, failed or cancelled install shows no progress bar). */
    val isActive: Boolean get() = phase == Phase.DOWNLOADING || phase == Phase.VERIFYING || phase == Phase.INSTALLING
}

/** How one install ended, as the caller reports it. */
enum class InstallResult {
    INSTALLED,
    /** Refused or failed (the row and the history say why). */
    NOT_INSTALLED,
    /** The download was cancelled, the system's install dialog was cancelled, or show lock stopped it. */
    CANCELLED,
    /** That app is already downloading or installing (a second tap, or a batch has it): nothing new started. */
    ALREADY_RUNNING,
}

/**
 * The apps being installed right now, plus the ones an Update all / import still has queued — process-wide, so the
 * background check (its own repository instance) never announces an update that's already being installed.
 */
object ActiveInstalls {
    /** One install per app at a time (see [LauncherRepository.install]). */
    val running: MutableSet<String> = ConcurrentHashMap.newKeySet()
    private val queued: MutableSet<String> = ConcurrentHashMap.newKeySet()

    fun queue(ids: Collection<String>) { queued.clear(); queued.addAll(ids) }
    fun clearQueue() = queued.clear()
    fun ids(): Set<String> = running + queued
}

/**
 * The launcher's engine: catalog -> release feed -> verify -> `PackageInstaller`.
 *
 * Everything that reaches the network or the disk lives here; the Compose layer only renders what it
 * publishes. The verification chain is the desktop one plus the Android certificate pin, and it is
 * fail-closed at every step — a download that can't be proven is deleted, never installed.
 */
class LauncherRepository(private val context: Context) {

    private val log = AppLog.get(context)
    val settings = Settings(context)
    val secrets = SecretStore(context)
    private val installed = InstalledApps(context)
    private val installer = ApkInstaller(context)

    val catalog: Catalog by lazy {
        context.assets.open("catalog.json").use { Catalog.parse(it.readBytes().toString(Charsets.UTF_8)) }
    }

    val launcherVersion: String get() = BuildConfig.VERSION_NAME

    private var notes: WhatsNewNotes = WhatsNewNotes()

    // ── the release feed ─────────────────────────────────────────────────────

    /** The API base in use, or null when the launcher has no credential yet. */
    fun client(): GitHubClient? {
        if (BuildConfig.ALLOW_DEBUG_FEED && settings.authMode == AuthMode.DEBUG_FEED) {
            val base = settings.debugFeedBase
            return if (base.isBlank()) null else GitHubClient.unauthenticated(base)
        }
        return when (settings.authMode) {
            AuthMode.TOKEN -> secrets.token()?.let { GitHubClient.direct(it) }
            else -> {
                val pass = secrets.passphrase() ?: return null
                val base = relayBase() ?: return null
                GitHubClient.server(base, pass)
            }
        }
    }

    /** The download server in use in server mode (a validated override, else the catalog's) — for diagnostics. */
    fun relayBase(): String? = RelayPolicy.validatedOverride(settings.relayOverride) ?: catalog.downloadServer

    fun hasCredential(): Boolean =
        (BuildConfig.ALLOW_DEBUG_FEED && settings.authMode == AuthMode.DEBUG_FEED &&
            settings.debugFeedBase.isNotBlank()) ||
            secrets.hasPassphrase() || secrets.hasToken()

    // ── release choice (stable, or the Dev channel) ─────────────────────────

    /**
     * The release an app installs from. Dev channel OFF: GitHub's `/releases/latest`, which never returns a
     * pre-release — so a `-dev.N` build is never offered. ON: the full list, picked by [ReleasePick] (highest
     * semver including dev builds; a stable X.Y.Z supersedes its own dev builds).
     */
    private fun pickRelease(client: GitHubClient, app: CatalogApp): ReleaseInfo {
        if (!settings.devChannel) return client.latestRelease(app.owner, app.repo)
        return ReleasePick.latest(client.releases(app.owner, app.repo), devChannel = true)
            ?: throw GitHubException.noRelease()
    }

    /**
     * The installed version as a release tag: the recorded tag when the package still reports that tag's
     * X.Y.Z (so a dev build reads as `0.1.0-dev.2`, not `0.1.0`), else the package's own versionName.
     */
    fun installedVersionFor(catalogId: String): String? {
        val name = installed.forCatalogId(catalogId)?.versionName ?: return null
        val tag = settings.installedTag(catalogId) ?: return name
        return if (VersionCompare.core(tag) == VersionCompare.norm(name)) VersionCompare.norm(tag) else name
    }

    // ── self-update ──────────────────────────────────────────────────────────

    /** The launcher itself as a catalog entry (`self` in catalog.json), or null if the catalog has none. */
    val launcherApp: CatalogApp? get() = catalog.selfInfo?.asApp()

    /**
     * The launcher's newer version (normalised, e.g. "1.28.0") when its latest release is newer than this
     * build AND carries an Android APK; else null. Never throws — a failed check just means "no update".
     */
    suspend fun checkLauncherUpdate(): String? = withContext(Dispatchers.IO) {
        val self = launcherApp ?: return@withContext null
        val client = client() ?: return@withContext null
        try {
            // Dev channel ON: the launcher's own dev builds too (a dev build's versionName is its full tag,
            // stamped from JBTT_VERSION at build time). Android can't install an OLDER launcher over a newer one,
            // so there's no "back to release" here: a dev build is replaced when its release ships.
            val release = pickRelease(client, self)
            val latest = VersionCompare.norm(release.tagName)
            if (!VersionCompare.isNewer(latest, launcherVersion)) return@withContext null
            AndroidAsset.resolve(self, release.tagName, release.assets.map { it.name }) ?: return@withContext null
            latest
        } catch (e: Exception) {
            log.log("self-update check: ${e.message}")
            null
        }
    }

    /**
     * Updates the launcher through the SAME chain as every app (signed SHA256SUMS with trusted comment
     * `JBTheatreTools <tag>`, APK SHA-256, suite certificate, package id). Android replaces the running app
     * once the user confirms, which ends this process — so on success this usually never returns.
     */
    suspend fun installLauncher(onProgress: (InstallProgress) -> Unit): InstallResult {
        val self = launcherApp ?: return InstallResult.NOT_INSTALLED
        log.log("self-update: installing a new launcher (the app closes while Android replaces it)")
        return install(self, onProgress)
    }

    /** Refreshes every app's status. Network failures land in the row's note, never as a crash. */
    suspend fun refresh(): List<AppStatus> = withContext(Dispatchers.IO) {
        val client = client()
        client?.whatsNewNotes()?.let { (parsed, _) -> notes = parsed }
        catalog.apps.map { app -> statusFor(app, client) }
    }

    private fun statusFor(app: CatalogApp, client: GitHubClient?): AppStatus {
        val (line, lineVersion) = notes.resolved(app)
        val installedVersion = installedVersionFor(app.id)
        if (client == null) {
            return AppStatus(
                app = app, installedVersion = installedVersion,
                note = "Sign in to see releases", whatsNew = line, whatsNewVersion = lineVersion,
            )
        }
        return try {
            val release = pickRelease(client, app)
            val assetName = AndroidAsset.resolve(app, release.tagName, release.assets.map { it.name })
            val asset = assetName?.let { name -> release.assets.firstOrNull { it.name == name } }
            AppStatus(
                app = app,
                installedVersion = installedVersion,
                latestVersion = VersionCompare.norm(release.tagName),
                latestPublished = release.publishedAt,
                held = installedVersion != null && app.id in settings.heldApps,
                apkAssetName = if (asset != null) assetName else null,
                apkSizeBytes = asset?.size ?: 0,
                note = if (asset == null) "No Android build yet" else null,
                // Dev channel OFF → `release` is the latest stable. When it isn't NEWER than the dev build that's
                // installed, nothing would ever replace the dev build until the next release — offer the way back.
                backToRelease = !settings.devChannel && asset != null &&
                    installedVersion != null && VersionCompare.isDev(installedVersion) &&
                    !VersionCompare.isDev(release.tagName) &&
                    !VersionCompare.isNewer(release.tagName, installedVersion),
                whatsNew = line,
                whatsNewVersion = lineVersion,
            )
        } catch (e: Exception) {
            // Nothing released on this device's channel (only dev builds so far, or none): shown only with the
            // Development builds switch on, so everyone else never sees a dead tile.
            val noRelease = e is GitHubException && e.kind == GitHubException.Kind.NO_RELEASE
            if (!noRelease) log.log("check ${app.id}: ${e.javaClass.simpleName}: ${e.message}")
            AppStatus(
                app = app, installedVersion = installedVersion,
                held = installedVersion != null && app.id in settings.heldApps,
                note = if (noRelease) "No release yet" else UserMessage.of(e),
                whatsNew = line, whatsNewVersion = lineVersion,
                hidden = noRelease && installedVersion == null && !settings.devChannel,
                checkFailed = !noRelease,
            )
        }
    }

    // ── install ──────────────────────────────────────────────────────────────

    /** Guards the PackageInstaller session step — see the note in [install]. */
    private val sessionMutex = Mutex()

    /** Catalog ids whose download should stop (the Cancel button, or Stop on Update all). */
    private val cancelRequests: MutableSet<String> = ConcurrentHashMap.newKeySet()

    /** Asks an in-flight download of [catalogId] to stop; a no-op once it has moved on to verify / install. */
    fun requestCancel(catalogId: String) { cancelRequests.add(catalogId) }

    /** Forgets a stale Cancel for [catalogId] — called just before a new install of it starts. */
    fun clearCancel(catalogId: String) { cancelRequests.remove(catalogId) }

    /** True when [catalogId] is downloading or installing right now. */
    fun isInstalling(catalogId: String): Boolean = catalogId in ActiveInstalls.running

    /** Cancel, a batch's Stop, or show lock: the download of [app] stops (show lock stops EVERY download). */
    private fun cancelled(app: CatalogApp, stop: () -> Boolean): Boolean =
        app.id in cancelRequests || stop() || settings.showLock

    /**
     * Download -> verify -> install one app, reporting progress. The chain:
     *   size -> suite-signed SHA256SUMS (trusted comment `<repo> <tag>`) -> the APK's SHA-256 ->
     *   the APK's signing certificate -> the session.
     */
    suspend fun install(
        app: CatalogApp,
        onProgress: (InstallProgress) -> Unit,
        tag: String? = null,
        /** A batch's Stop: cancels this download too, and keeps a queued one from starting. Show lock always does. */
        stop: () -> Boolean = { false },
    ): InstallResult = withContext(Dispatchers.IO) {
        // One install per app at a time: a row's Update and a batch's Update all used to download, verify and
        // delete the SAME cache file concurrently (a spurious failure, and a window where the session could stream
        // bytes other than the ones just hashed). A second request is reported as ALREADY_RUNNING — not a failure.
        if (!ActiveInstalls.running.add(app.id)) {
            log.log("install ${app.id}: already downloading or installing — not started again")
            return@withContext InstallResult.ALREADY_RUNNING
        }
        try { installOnce(app, onProgress, tag, stop) } finally {
            ActiveInstalls.running.remove(app.id)
            cancelRequests.remove(app.id)
        }
    }

    /**
     * The release a setup file's held [tag] names, by version equality (so "1.2.0" finds "v1.2.0"); a development
     * build only with Development builds on. Pages past the first [GitHubClient.PAGE_SIZE] releases (up to
     * [MAX_RELEASE_PAGES] pages) for an old version, and says precisely when it still isn't there.
     */
    private fun releaseForTag(client: GitHubClient, app: CatalogApp, tag: String): ReleaseInfo {
        if (VersionCompare.isDev(tag) && !settings.devChannel) {
            throw IllegalStateException("${VersionCompare.display(tag)} is a development build (not switched on here)")
        }
        var searched = 0
        for (page in 1..MAX_RELEASE_PAGES) {
            val batch = client.releasesPage(app.owner, app.repo, page)
            searched += batch.size
            ReleasePick.byVersion(batch.filter { !it.draft }, tag, allowDev = settings.devChannel)?.let { return it }
            if (batch.size < GitHubClient.PAGE_SIZE) {
                throw IllegalStateException("${VersionCompare.display(tag)} isn't one of ${app.name}'s releases")
            }
        }
        throw IllegalStateException("${VersionCompare.display(tag)} isn't in ${app.name}'s last $searched releases")
    }

    private suspend fun installOnce(
        app: CatalogApp,
        onProgress: (InstallProgress) -> Unit,
        tag: String?,
        stop: () -> Boolean,
    ): InstallResult = withContext(Dispatchers.IO) {
        val client = client() ?: run {
            onProgress(InstallProgress(app.id, InstallProgress.Phase.FAILED, message = "Not signed in"))
            return@withContext InstallResult.NOT_INSTALLED
        }
        val cache = File(context.cacheDir, "downloads").apply { mkdirs() }
        var apk: File? = null
        val before = installedVersionFor(app.id)
        var releaseTag: String? = tag
        try {
            // A specific version (a setup file's held version), or the channel's latest.
            if (cancelled(app, stop)) throw DownloadCancelledException()
            val release = if (tag != null) releaseForTag(client, app, tag) else pickRelease(client, app)
            releaseTag = release.tagName
            val assetName = AndroidAsset.resolve(app, release.tagName, release.assets.map { it.name })
                ?: throw IllegalStateException(
                    "release ${release.tagName} has no Android build (looked for " +
                        AndroidAsset.candidates(app, release.tagName).joinToString(", ") + ")"
                )
            val asset = release.assets.first { it.name == assetName }

            // Refuse up front rather than failing mid-install on a full device (usableSpace is 0 when unknown).
            val free = cache.usableSpace.takeIf { it > 0 } ?: -1L
            DiskSpace.shortfall(DiskSpace.required(asset.size, assetName), free)?.let { throw IllegalStateException(it) }

            if (cancelled(app, stop)) throw DownloadCancelledException()
            onProgress(InstallProgress(app.id, InstallProgress.Phase.DOWNLOADING, 0.0))
            apk = File(cache, assetName)
            client.downloadAsset(
                app.owner, app.repo, asset.id, apk,
                progress = { f -> onProgress(InstallProgress(app.id, InstallProgress.Phase.DOWNLOADING, f)) },
                shouldCancel = { cancelled(app, stop) },
            )
            cancelRequests.remove(app.id)   // past the download: a late Cancel no longer applies

            if (asset.size > 0 && apk.length() != asset.size) {
                throw IllegalStateException(
                    "download is ${apk.length()} bytes but the release lists ${asset.size}"
                )
            }

            onProgress(InstallProgress(app.id, InstallProgress.Phase.VERIFYING, 1.0))
            verify(client, app, release, asset.name, apk)

            onProgress(InstallProgress(app.id, InstallProgress.Phase.INSTALLING, 1.0))
            val expectedPackage = PackageIds.packageId(app.id)
            val declared = ApkVerifier.packageNameOf(context, apk)
            if (expectedPackage != null && declared != null && declared != expectedPackage) {
                throw IllegalStateException(
                    "the APK declares $declared, but the catalog expects $expectedPackage"
                )
            }
            // ONE PackageInstaller session at a time (downloads + hashing above still run in parallel): two
            // overlapping sessions made Android's package verifier reject one of them with
            // INSTALL_FAILED_VERIFICATION_FAILURE ("Install not allowed for file:///data/app/vmdl….tmp") when
            // several tiles were tapped in a row — each succeeded on a retry. So: serialise, and retry that
            // one error once after a pause.
            // Show lock is checked INSIDE the session lock (and again before the retry): an install queued behind
            // another's confirm dialog must not go ahead if the lock was switched on while it waited.
            val outcome = sessionMutex.withLock {
                if (settings.showLock) throw ShowLockedException()
                var result = installer.install(apk, expectedPackage ?: declared, app.id)
                if (result is ApkInstaller.Outcome.Failed && result.detail.contains("VERIFICATION_FAILURE")) {
                    log.log("install ${app.id} ${release.tagName}: verifier busy — retrying once")
                    delay(2_500)
                    if (settings.showLock) throw ShowLockedException()
                    result = installer.install(apk, expectedPackage ?: declared, app.id)
                }
                result
            }
            when (outcome) {
                is ApkInstaller.Outcome.Succeeded -> {
                    settings.setInstalledTag(app.id, release.tagName)
                    log.log("install ${app.id} ${release.tagName}: $assetName verified + installed")
                    addHistory(app.id, app.name, ActivityHistory.actionFor(before, release.tagName), before, release.tagName)
                    onProgress(InstallProgress(app.id, InstallProgress.Phase.DONE, 1.0))
                    InstallResult.INSTALLED
                }
                is ApkInstaller.Outcome.Cancelled -> {
                    // The system's install dialog was cancelled: recorded as cancelled, never as a failure.
                    log.log("install ${app.id} ${release.tagName}: the install dialog was cancelled")
                    addHistory(app.id, app.name, "cancelled", null, release.tagName)
                    onProgress(InstallProgress(app.id, InstallProgress.Phase.CANCELLED))
                    InstallResult.CANCELLED
                }
                is ApkInstaller.Outcome.Failed -> {
                    log.log("install ${app.id} ${release.tagName}: ${outcome.detail}")
                    addHistory(app.id, app.name, "failed", null, release.tagName, outcome.message)
                    onProgress(
                        InstallProgress(app.id, InstallProgress.Phase.FAILED, message = outcome.message)
                    )
                    InstallResult.NOT_INSTALLED
                }
            }
        } catch (e: ShowLockedException) {
            apk?.delete()
            log.log("install ${app.id}: not installed — show lock is on")
            onProgress(InstallProgress(app.id, InstallProgress.Phase.CANCELLED))
            InstallResult.CANCELLED
        } catch (e: DownloadCancelledException) {
            cancelRequests.remove(app.id)
            apk?.delete()
            log.log("install ${app.id}: download cancelled" + if (settings.showLock) " (show lock is on)" else "")
            onProgress(InstallProgress(app.id, InstallProgress.Phase.CANCELLED))
            InstallResult.CANCELLED
        } catch (e: Exception) {
            apk?.delete()
            // The log keeps the detail; the row and the history get a plain line (no host names, no socket text).
            log.log("install ${app.id}: refused — ${e.javaClass.simpleName}: ${e.message}")
            val message = UserMessage.of(e)
            addHistory(app.id, app.name, "failed", null, releaseTag, message)
            onProgress(InstallProgress(app.id, InstallProgress.Phase.FAILED, message = message))
            InstallResult.NOT_INSTALLED
        } finally {
            apk?.delete()
        }
    }

    /**
     * The verification chain. Throws with a one-line reason on any failure; the caller deletes the
     * download. Nothing is "installed anyway".
     */
    private fun verify(
        client: GitHubClient,
        app: CatalogApp,
        release: ReleaseInfo,
        assetName: String,
        apk: File,
    ) {
        val cache = File(context.cacheDir, "downloads")
        val sumsAsset = release.assets.firstOrNull { it.name == "SHA256SUMS" }
            ?: throw IllegalStateException("this release publishes no SHA256SUMS checksums")
        val sigAsset = release.assets.firstOrNull { it.name == "SHA256SUMS.minisig" }
            ?: throw IllegalStateException("this release’s SHA256SUMS isn’t signed with the suite key")

        val sums = File(cache, "${app.id}-${PathSafe.component(release.tagName)}-SHA256SUMS")
        val sig = File(cache, "${app.id}-${PathSafe.component(release.tagName)}-SHA256SUMS.minisig")
        try {
            client.downloadAsset(app.owner, app.repo, sumsAsset.id, sums)
            client.downloadAsset(app.owner, app.repo, sigAsset.id, sig)
            val sumsBytes = sums.readBytes()
            // 1. authenticity: the suite key signed THIS release's manifest. A DEBUG build pointed at
            //    a local feed may verify against a throwaway key instead (see the README) — a release
            //    build has no such path and only ever trusts the pinned suite key.
            Minisign.verify(
                manifest = sumsBytes,
                minisig = sig.readText(),
                expectedTrustedComment = "${app.repo} ${release.tagName}",
                publicKeyBase64 = verificationKey(),
            )
            // 2. integrity: the APK is the file that manifest lists.
            when (val r = ApkVerifier.checkChecksum(apk, assetName, String(sumsBytes, Charsets.UTF_8))) {
                is ApkVerifier.Result.Failed -> throw IllegalStateException(r.reason)
                else -> Unit
            }
            // 3. provenance: the APK carries the suite signing certificate.
            when (val r = ApkVerifier.checkSigningCert(context, apk)) {
                is ApkVerifier.Result.Failed -> throw IllegalStateException(r.reason)
                else -> Unit
            }
            log.log("verify ${app.id} ${release.tagName}: $assetName — signed manifest ok, sha256 ok, suite cert ok")
        } finally {
            sums.delete()
            sig.delete()
        }
    }

    /**
     * The minisign public key a manifest must verify against: always the pinned suite key, except in a
     * DEBUG build explicitly pointed at a local feed with its own throwaway key.
     */
    private fun verificationKey(): String {
        if (BuildConfig.ALLOW_DEBUG_FEED && settings.authMode == AuthMode.DEBUG_FEED) {
            val key = settings.debugFeedMinisignKey
            if (key.isNotBlank()) return key
        }
        return Minisign.SUITE_PUBLIC_KEY
    }

    /** What a batch did: [done] installed, out of [attempted] (a held or already-running app isn't attempted). */
    data class BatchCount(val done: Int, val attempted: Int)

    /**
     * Update every app with a pending update (never a held one), one session at a time; a failure never blocks
     * the rest. [shouldStop] ends the run between apps (Stop). An app already installing from its own row is left
     * to that install and isn't counted as a failure.
     */
    suspend fun updateAll(
        statuses: List<AppStatus>,
        shouldStop: () -> Boolean,
        onProgress: (InstallProgress) -> Unit,
    ): BatchCount {
        val pending = statuses.filter { it.updatePending && it.canInstall }
        var done = 0
        var attempted = 0
        for (status in pending) {
            if (shouldStop()) break
            if (status.app.id in settings.heldApps) continue   // held after Update all started
            // Re-checked just before its turn, as on the desktop: updated from its row meanwhile (already at the
            // latest), or uninstalled meanwhile — either way the batch leaves it alone.
            val now = installedVersionFor(status.app.id)
            if (now == null && status.installedVersion != null) continue
            if (now != null && status.latestVersion != null && VersionCompare.equal(now, status.latestVersion)) continue
            if (status.app.id !in ActiveInstalls.running) clearCancel(status.app.id)
            when (install(status.app, onProgress, stop = shouldStop)) {
                InstallResult.INSTALLED -> { done++; attempted++ }
                InstallResult.ALREADY_RUNNING -> Unit
                else -> attempted++
            }
        }
        return BatchCount(done, attempted)
    }

    // ── v1.30: history, release notes, storage, launcher shortcuts ──────────

    private val historyFile = File(context.filesDir, "history.json")
    private val historyLock = Any()

    /** The activity history, oldest first. File IO: call it off the main thread. */
    fun history(): List<ActivityEvent> = synchronized(historyLock) {
        runCatching { if (historyFile.isFile) ActivityHistory.parse(historyFile.readText()) else emptyList() }
            .getOrDefault(emptyList())
    }

    /**
     * Records one event (best effort, written atomically — history never gets in the way of an install). File IO:
     * call it off the main thread. A damaged history file is kept aside as history.json.bad, not overwritten.
     */
    fun addHistory(app: String, name: String, action: String, from: String? = null, to: String? = null, note: String? = null) {
        synchronized(historyLock) {
            runCatching {
                val existing = if (historyFile.isFile) historyFile.readText() else null
                if (ActivityHistory.isDamaged(existing)) {
                    val aside = File(historyFile.path + ".bad")
                    aside.delete()
                    if (historyFile.renameTo(aside)) log.log("history: history.json was damaged — kept as history.json.bad, starting a new one")
                }
                val list = ActivityHistory.append(
                    ActivityHistory.parse(existing), ActivityEvent(Instant.now(), app, name, action, from, to, note),
                )
                val tmp = File(historyFile.path + ".tmp")
                tmp.writeText(ActivityHistory.serialize(list))
                if (!tmp.renameTo(historyFile)) { historyFile.delete(); tmp.renameTo(historyFile) }
            }.onFailure { log.log("history: could not record $action $app: ${it.message}") }
        }
    }

    /** Every (non-draft) release of an app, for the in-app release notes. Throws on a network / access error. */
    suspend fun releasesFor(app: CatalogApp): List<ReleaseInfo> = withContext(Dispatchers.IO) {
        val client = client() ?: throw IllegalStateException("Sign in to see release notes")
        // Development builds exist only for devices with the Development builds switch on — never in release
        // notes, version lists or a setup file's version pick anywhere else.
        client.releases(app.owner, app.repo).filter { settings.devChannel || !VersionCompare.isDev(it.tagName) }
    }

    /** The launcher's own releases newer than [since] and not newer than this build (for "what's new"). */
    suspend fun launcherReleasesSince(since: String?): List<ReleaseInfo> {
        val self = launcherApp ?: return emptyList()
        return releasesFor(self).filter {
            !VersionCompare.isNewer(it.tagName, launcherVersion) &&
                // Nothing seen before (an update from a version that didn't record it): just this version's notes.
                (if (since.isNullOrBlank()) VersionCompare.equal(it.tagName, launcherVersion) else VersionCompare.isNewer(it.tagName, since)) &&
                (settings.devChannel || !VersionCompare.isDev(it.tagName))
        }
    }

    /** This launcher was installed before its current version (it has been updated at least once). */
    fun launcherWasUpdated(): Boolean = runCatching {
        val info = context.packageManager.getPackageInfo(context.packageName, 0)
        info.lastUpdateTime > info.firstInstallTime
    }.getOrDefault(false)

    /** The launcher's one-line what's-new from the catalog (the offline fallback for the notes). */
    fun launcherWhatsNewLine(): String? {
        val self = catalog.selfInfo ?: return null
        val line = self.whatsNew?.takeIf { it.isNotBlank() } ?: return null
        return self.whatsNewVersion?.takeIf { it.isNotBlank() }?.let { "New in $it: $line" } ?: line
    }

    /**
     * When [catalogId]'s installed version was installed (epoch ms) and its APK size, or null if not installed.
     * Measures files: call it off the main thread.
     */
    fun installedDetails(catalogId: String): Pair<Long, Long>? =
        installed.forCatalogId(catalogId, withSize = true)?.let { it.lastUpdateTime to it.apkBytes }

    private val downloadsDir: File get() = File(context.cacheDir, "downloads")

    /** Bytes in the download cache. */
    fun cacheBytes(): Long = downloadsDir.walkBottomUp().filter { it.isFile }.sumOf { it.length() }

    /** Bytes of every installed suite app's APK(s). */
    fun installedBytes(): Long = installed.scan(withSize = true).values.sumOf { it.apkBytes }

    /** Empties the download cache (the caller makes sure nothing is downloading). */
    fun clearCache() {
        downloadsDir.listFiles()?.forEach { runCatching { it.deleteRecursively() } }
        log.log("download cache cleared")
    }

    /** Remembers that [catalogId] was just opened (the launcher-icon shortcuts list recent apps first). */
    fun noteOpened(catalogId: String) {
        settings.recentOpens = listOf(catalogId) + settings.recentOpens.filter { it != catalogId }
    }

    /**
     * The launcher icon's long-press shortcuts: up to four installed apps, most recently opened first, each
     * opening that app directly.
     */
    fun updateShortcuts() {
        runCatching {
            val sm = context.getSystemService(android.content.pm.ShortcutManager::class.java) ?: return
            val max = minOf(4, sm.maxShortcutCountPerActivity)
            val ids = (settings.recentOpens + catalog.apps.map { it.id }).distinct().filter { isInstalled(it) }.take(max)
            val shortcuts = ids.mapNotNull { id ->
                val app = catalog.apps.firstOrNull { it.id == id } ?: return@mapNotNull null
                val pkg = PackageIds.packageId(id) ?: return@mapNotNull null
                val intent = context.packageManager.getLaunchIntentForPackage(pkg) ?: return@mapNotNull null
                val bitmap = runCatching {
                    context.assets.open("icons/$id.png").use { android.graphics.BitmapFactory.decodeStream(it) }
                }.getOrNull()
                val icon = if (bitmap != null) android.graphics.drawable.Icon.createWithBitmap(bitmap)
                else android.graphics.drawable.Icon.createWithResource(context, com.jamesbreedon.jbtheatretools.R.mipmap.ic_launcher)
                android.content.pm.ShortcutInfo.Builder(context, "app-$id")
                    .setShortLabel(app.name)
                    .setLongLabel("Open ${app.name}")
                    .setIcon(icon)
                    .setIntent(intent)
                    .build()
            }
            sm.setDynamicShortcuts(shortcuts)
        }.onFailure { log.log("shortcuts: ${it.message}") }
    }

    /** True while the app's package is on the device (cheap PackageManager read, no network). */
    fun isInstalled(catalogId: String): Boolean = installed.forCatalogId(catalogId) != null

    fun remove(catalogId: String) {
        val pkg = PackageIds.packageId(catalogId) ?: return
        log.log("remove $catalogId ($pkg): asked the system to uninstall")
        installer.requestUninstall(pkg)
    }

    fun open(catalogId: String): Boolean {
        val pkg = PackageIds.packageId(catalogId) ?: return false
        val intent = context.packageManager.getLaunchIntentForPackage(pkg) ?: return false
        intent.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(intent)
        noteOpened(catalogId)   // the caller refreshes the shortcuts, off the main thread
        return true
    }

    /** The app's release-notes page — the public Releases page for that repo. */
    fun releaseNotesUrl(app: CatalogApp): String =
        "https://github.com/${app.owner}/${app.repo}/releases"

    fun canRequestInstalls(): Boolean = installer.canRequestInstalls()

    fun openInstallPermissionSettings() = installer.openInstallPermissionSettings()

    fun logText(): String = log.read()

    fun logFile(): File = log.path
}

/** How many pages of releases a setup file's held version is looked for in. */
private const val MAX_RELEASE_PAGES = 4

/** Keeps a release tag safe to use as a filename component. */
object PathSafe {
    fun component(raw: String): String {
        val cleaned = raw.map { if (it.isLetterOrDigit() || it == '.' || it == '-' || it == '_') it else '_' }
            .joinToString("")
        return cleaned.take(64).ifEmpty { "tag" }
    }
}

/** Show lock was switched on while an install waited for its session: nothing is installed. */
class ShowLockedException : java.io.IOException("Show lock is on")
