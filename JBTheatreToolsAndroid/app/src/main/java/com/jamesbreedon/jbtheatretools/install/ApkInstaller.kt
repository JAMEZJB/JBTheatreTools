package com.jamesbreedon.jbtheatretools.install

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.net.Uri
import android.os.Build
import kotlinx.coroutines.flow.first
import java.io.File

/**
 * Installs and removes suite apps.
 *
 * Installs go through a **`PackageInstaller` session** — never an `ACTION_VIEW` intent at a file. The
 * launcher writes the verified APK into the session itself, so nothing hands an arbitrary file URI to
 * another app, and on Android 14+ it claims **update ownership** so another store can't hijack a later
 * update of an app this launcher installed. On Android 10–13 the system installer dialog appears once
 * per install, which is the documented behaviour, not a failure.
 *
 * Removal is `ACTION_UNINSTALL_PACKAGE`, behind the rule-8 confirm sheet the UI shows first.
 */
class ApkInstaller(private val context: Context) {

    sealed class Outcome {
        object Succeeded : Outcome()
        data class Failed(val message: String) : Outcome()
    }

    /**
     * Commits a session for [apk] and suspends until the system reports the result. [sessionKey] is an
     * opaque tag (the catalog id) echoed back on the broadcast so concurrent installs can't cross.
     */
    suspend fun install(apk: File, expectedPackage: String?, sessionKey: String): Outcome {
        val installer = context.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
        expectedPackage?.let { params.setAppPackageName(it) }
        params.setSize(apk.length())
        if (Build.VERSION.SDK_INT >= 31) {
            params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_UNSPECIFIED)
        }

        val sessionId = try {
            installer.createSession(params)
        } catch (e: Exception) {
            return Outcome.Failed("couldn’t open an install session: ${e.message}")
        }

        try {
            installer.openSession(sessionId).use { session ->
                session.openWrite("apk", 0, apk.length()).use { out ->
                    apk.inputStream().use { input -> input.copyTo(out, 1 shl 16) }
                    session.fsync(out)
                }
                session.commit(statusIntent(sessionKey, sessionId).intentSender)
            }
        } catch (e: Exception) {
            runCatching { installer.abandonSession(sessionId) }
            return Outcome.Failed("couldn’t write the install session: ${e.message}")
        }

        // The first PENDING_USER_ACTION is the system's own confirmation (10–13, or whenever the
        // launcher doesn't own the update); wait past it for the terminal result.
        while (true) {
            val event = InstallResultReceiver.events.first { it.sessionKey == sessionKey }
            when (event.state) {
                InstallResultReceiver.State.NEEDS_CONFIRMATION -> continue
                InstallResultReceiver.State.SUCCEEDED -> return Outcome.Succeeded
                InstallResultReceiver.State.FAILED -> return Outcome.Failed(event.message ?: "install failed")
            }
        }
    }

    /** Removal: the system uninstall flow, after the UI's rule-8 confirm sheet. */
    fun requestUninstall(packageName: String) {
        @Suppress("DEPRECATION")
        val intent = Intent(Intent.ACTION_UNINSTALL_PACKAGE, Uri.parse("package:$packageName")).apply {
            putExtra(Intent.EXTRA_RETURN_RESULT, false)
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(intent)
    }

    /** True when the user has granted "Install unknown apps" to this launcher. */
    fun canRequestInstalls(): Boolean =
        context.packageManager.canRequestPackageInstalls()

    /** The one-off system screen that grants it. The UI explains why in one line before calling this. */
    fun openInstallPermissionSettings() {
        val intent = Intent(
            android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
            Uri.parse("package:${context.packageName}"),
        ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(intent)
    }

    private fun statusIntent(sessionKey: String, sessionId: Int): PendingIntent {
        val intent = Intent(context, InstallResultReceiver::class.java).apply {
            putExtra(InstallResultReceiver.EXTRA_SESSION_KEY, sessionKey)
        }
        val flags = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE
        return PendingIntent.getBroadcast(context, sessionId, intent, flags)
    }
}
