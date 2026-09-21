package com.jamesbreedon.jbtheatretools.install

import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import com.jamesbreedon.jbtheatretools.core.Sha256Sums
import com.jamesbreedon.jbtheatretools.core.SigningCert
import java.io.File

/**
 * What a downloaded APK has to prove before `PackageInstaller` is allowed anywhere near it.
 *
 * The chain, in order — the same one the desktop launchers use, plus the Android-only cert pin:
 *   1. the release's `SHA256SUMS` is signed by the suite minisign key, and its trusted comment names
 *      THIS release (`<repo> <tag>`)                                        — [com.jamesbreedon.jbtheatretools.core.Minisign]
 *   2. the APK's SHA-256 equals the line for this asset in that manifest    — [checkChecksum]
 *   3. the APK's signing certificate is the suite's                         — [checkSigningCert]
 *
 * Any failure deletes the file and refuses. There is no "install anyway".
 */
object ApkVerifier {

    sealed class Result {
        object Ok : Result()
        data class Failed(val reason: String) : Result()
    }

    /** Step 2: the APK's SHA-256 against the manifest line for [assetName]. */
    fun checkChecksum(apk: File, assetName: String, sumsText: String): Result {
        val expected = Sha256Sums.expected(assetName, sumsText)
            ?: return Result.Failed("“$assetName” isn’t listed in this release’s SHA256SUMS")
        val actual = Sha256Sums.sha256Hex(apk)
        return if (Sha256Sums.hexEquals(actual, expected)) Result.Ok
        else Result.Failed("checksum mismatch for $assetName — the download does not match the release’s SHA256SUMS")
    }

    /**
     * Step 3: the APK's signing certificate must be the suite certificate, fail-closed.
     *
     * The fingerprint is taken from the archive itself (`getPackageArchiveInfo`), so a swapped file is
     * caught before the session is committed rather than by the system installer afterwards.
     */
    fun checkSigningCert(context: Context, apk: File): Result {
        val fingerprint = fingerprintOf(context, apk)
            ?: return Result.Failed("the download isn’t a readable APK, or carries no signature")
        return if (SigningCert.isTrusted(fingerprint)) Result.Ok
        else Result.Failed(
            "the download is signed with certificate $fingerprint, which is not the suite signing certificate"
        )
    }

    /** The SHA-256 of the APK's signing certificate, lowercase hex, or null. */
    @Suppress("DEPRECATION")
    fun fingerprintOf(context: Context, apk: File): String? {
        val pm = context.packageManager
        val flags = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            PackageManager.GET_SIGNING_CERTIFICATES
        } else {
            PackageManager.GET_SIGNATURES
        }
        val info = pm.getPackageArchiveInfo(apk.absolutePath, flags) ?: return null
        val der: ByteArray = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            val signing = info.signingInfo ?: return null
            val signers = if (signing.hasMultipleSigners()) signing.apkContentsSigners
            else signing.signingCertificateHistory
            signers?.firstOrNull()?.toByteArray() ?: return null
        } else {
            info.signatures?.firstOrNull()?.toByteArray() ?: return null
        }
        return SigningCert.fingerprint(der)
    }

    /** The applicationId declared inside the archive — checked against what the catalog expects. */
    fun packageNameOf(context: Context, apk: File): String? =
        context.packageManager.getPackageArchiveInfo(apk.absolutePath, 0)?.packageName

    /** The versionName declared inside the archive. */
    fun versionNameOf(context: Context, apk: File): String? =
        context.packageManager.getPackageArchiveInfo(apk.absolutePath, 0)?.versionName
}
