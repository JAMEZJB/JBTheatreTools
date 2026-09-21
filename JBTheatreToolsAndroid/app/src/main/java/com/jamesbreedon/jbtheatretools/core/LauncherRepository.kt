package com.jamesbreedon.jbtheatretools.core

import android.content.Context
import com.jamesbreedon.jbtheatretools.BuildConfig
import com.jamesbreedon.jbtheatretools.install.ApkInstaller
import com.jamesbreedon.jbtheatretools.install.ApkVerifier
import com.jamesbreedon.jbtheatretools.install.InstalledApps
import com.jamesbreedon.jbtheatretools.net.GitHubClient
import com.jamesbreedon.jbtheatretools.net.RelayPolicy
import com.jamesbreedon.jbtheatretools.net.ReleaseInfo
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

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
) {
    val isInstalled: Boolean get() = installedVersion != null
    val hasUpdate: Boolean
        get() = installedVersion != null && latestVersion != null &&
            VersionCompare.isNewer(latestVersion, installedVersion)
    val canInstall: Boolean get() = apkAssetName != null
}

/** Progress of one install, for the row and the Updates tab. */
data class InstallProgress(
    val catalogId: String,
    val phase: Phase,
    val fraction: Double = 0.0,
    val message: String? = null,
) {
    enum class Phase { DOWNLOADING, VERIFYING, INSTALLING, DONE, FAILED }
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
                val base = RelayPolicy.validatedOverride(settings.relayOverride)
                    ?: catalog.downloadServer
                    ?: return null
                GitHubClient.server(base, pass)
            }
        }
    }

    fun hasCredential(): Boolean =
        (BuildConfig.ALLOW_DEBUG_FEED && settings.authMode == AuthMode.DEBUG_FEED &&
            settings.debugFeedBase.isNotBlank()) ||
            secrets.hasPassphrase() || secrets.hasToken()

    /** Refreshes every app's status. Network failures land in the row's note, never as a crash. */
    suspend fun refresh(): List<AppStatus> = withContext(Dispatchers.IO) {
        val client = client()
        client?.whatsNewNotes()?.let { (parsed, _) -> notes = parsed }
        catalog.apps.map { app -> statusFor(app, client) }
    }

    private fun statusFor(app: CatalogApp, client: GitHubClient?): AppStatus {
        val (line, lineVersion) = notes.resolved(app)
        val installedVersion = installed.forCatalogId(app.id)?.versionName
        if (client == null) {
            return AppStatus(
                app = app, installedVersion = installedVersion,
                note = "Sign in to see releases", whatsNew = line, whatsNewVersion = lineVersion,
            )
        }
        return try {
            val release = client.latestRelease(app.owner, app.repo)
            val assetName = AndroidAsset.apkName(app, release.tagName)
            val asset = assetName?.let { name -> release.assets.firstOrNull { it.name == name } }
            AppStatus(
                app = app,
                installedVersion = installedVersion,
                latestVersion = VersionCompare.norm(release.tagName),
                apkAssetName = if (asset != null) assetName else null,
                apkSizeBytes = asset?.size ?: 0,
                note = if (asset == null) "No Android build yet" else null,
                whatsNew = line,
                whatsNewVersion = lineVersion,
            )
        } catch (e: Exception) {
            AppStatus(
                app = app, installedVersion = installedVersion,
                note = e.message ?: "Couldn’t reach the release feed",
                whatsNew = line, whatsNewVersion = lineVersion,
            )
        }
    }

    // ── install ──────────────────────────────────────────────────────────────

    /**
     * Download -> verify -> install one app, reporting progress. The chain:
     *   size -> suite-signed SHA256SUMS (trusted comment `<repo> <tag>`) -> the APK's SHA-256 ->
     *   the APK's signing certificate -> the session.
     */
    suspend fun install(
        app: CatalogApp,
        onProgress: (InstallProgress) -> Unit,
    ): Boolean = withContext(Dispatchers.IO) {
        val client = client() ?: run {
            onProgress(InstallProgress(app.id, InstallProgress.Phase.FAILED, message = "Not signed in"))
            return@withContext false
        }
        val cache = File(context.cacheDir, "downloads").apply { mkdirs() }
        var apk: File? = null
        try {
            val release = client.latestRelease(app.owner, app.repo)
            val assetName = AndroidAsset.apkName(app, release.tagName)
                ?: throw IllegalStateException("no Android asset name for ${app.id}")
            val asset = release.assets.firstOrNull { it.name == assetName }
                ?: throw IllegalStateException("release ${release.tagName} has no $assetName")

            onProgress(InstallProgress(app.id, InstallProgress.Phase.DOWNLOADING, 0.0))
            apk = File(cache, assetName)
            client.downloadAsset(app.owner, app.repo, asset.id, apk) { f ->
                onProgress(InstallProgress(app.id, InstallProgress.Phase.DOWNLOADING, f))
            }

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
            when (val outcome = installer.install(apk, expectedPackage ?: declared, app.id)) {
                is ApkInstaller.Outcome.Succeeded -> {
                    log.log("install ${app.id} ${release.tagName}: $assetName verified + installed")
                    onProgress(InstallProgress(app.id, InstallProgress.Phase.DONE, 1.0))
                    true
                }
                is ApkInstaller.Outcome.Failed -> {
                    log.log("install ${app.id} ${release.tagName}: ${outcome.message}")
                    onProgress(
                        InstallProgress(app.id, InstallProgress.Phase.FAILED, message = outcome.message)
                    )
                    false
                }
            }
        } catch (e: Exception) {
            apk?.delete()
            log.log("install ${app.id}: refused — ${e.message}")
            onProgress(
                InstallProgress(app.id, InstallProgress.Phase.FAILED, message = e.message ?: "install failed")
            )
            false
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

    /** Update every app with a pending update, one session at a time; a failure never blocks the rest. */
    suspend fun updateAll(
        statuses: List<AppStatus>,
        onProgress: (InstallProgress) -> Unit,
    ): Int {
        var done = 0
        for (status in statuses.filter { it.hasUpdate && it.canInstall }) {
            if (install(status.app, onProgress)) done++
        }
        return done
    }

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

/** Keeps a release tag safe to use as a filename component. */
object PathSafe {
    fun component(raw: String): String {
        val cleaned = raw.map { if (it.isLetterOrDigit() || it == '.' || it == '-' || it == '_') it else '_' }
            .joinToString("")
        return cleaned.take(64).ifEmpty { "tag" }
    }
}
