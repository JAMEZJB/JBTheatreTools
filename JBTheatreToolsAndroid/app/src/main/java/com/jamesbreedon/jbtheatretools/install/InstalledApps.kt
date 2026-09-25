package com.jamesbreedon.jbtheatretools.install

import android.content.Context
import android.content.pm.PackageManager
import com.jamesbreedon.jbtheatretools.core.PackageIds

/**
 * What is actually on the device.
 *
 * Android already keeps the install manifest the desktop launchers have to write themselves: the
 * package database. So "installed version" is `PackageManager.getPackageInfo(<applicationId>)`, not a
 * JSON file — there is no state to drift. The applicationIds come from [PackageIds], which is also
 * what the manifest's `<queries>` block lists (package visibility, Android 11+).
 */
class InstalledApps(private val context: Context) {

    data class Installed(
        val packageName: String,
        val versionName: String,
        val versionCode: Long,
        /** When this version was installed (PackageInfo.lastUpdateTime, epoch ms). */
        val lastUpdateTime: Long = 0,
        /** The size of the installed APK(s) — base plus any splits. */
        val apkBytes: Long = 0,
    )

    /** The installed record for a catalog id, or null when that app isn't installed. */
    fun forCatalogId(catalogId: String): Installed? {
        val pkg = PackageIds.packageId(catalogId) ?: return null
        return forPackage(pkg)
    }

    fun forPackage(packageName: String): Installed? = try {
        val info = context.packageManager.getPackageInfo(packageName, 0)
        val appInfo = info.applicationInfo
        val apks = listOfNotNull(appInfo?.sourceDir) + (appInfo?.splitSourceDirs?.toList() ?: emptyList())
        Installed(
            packageName = packageName,
            versionName = info.versionName.orEmpty(),
            versionCode = androidx.core.content.pm.PackageInfoCompat.getLongVersionCode(info),
            lastUpdateTime = info.lastUpdateTime,
            apkBytes = apks.sumOf { runCatching { java.io.File(it).length() }.getOrDefault(0L) },
        )
    } catch (e: PackageManager.NameNotFoundException) {
        null
    }

    /** Every suite app currently installed, keyed by applicationId. */
    fun scan(): Map<String, Installed> {
        val out = LinkedHashMap<String, Installed>()
        for (pkg in PackageIds.all()) forPackage(pkg)?.let { out[pkg] = it }
        return out
    }
}
