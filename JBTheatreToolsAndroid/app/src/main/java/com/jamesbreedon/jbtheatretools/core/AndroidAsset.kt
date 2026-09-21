package com.jamesbreedon.jbtheatretools.core

/**
 * How an app's Android release asset is named.
 *
 * Each app publishes `<AssetPrefix>-v<ver>-android-arm64.apk` (plus a `.sha256` sidecar and a line in
 * the release's `SHA256SUMS`). The prefix is the app's existing macOS asset name with its platform
 * suffix removed — `HeloControl-macOS.zip` -> `HeloControl` -> `HeloControl-v2.1.1-android-arm64.apk`
 * — so one catalog entry describes all three platforms and nothing new has to be maintained per app.
 *
 * Two shapes exist in today's catalog:
 *   `HeloControl-macOS.zip`                  -> `HeloControl`
 *   `Cisco.Switch.Tools.macOS.universal2.zip` -> `Cisco.Switch.Tools`
 *
 * A catalog entry may also carry an explicit `android-arm64` asset name, which always wins; that is
 * the escape hatch for any app whose APK is named differently from its macOS bundle.
 */
object AndroidAsset {
    const val PLATFORM_KEY = "android-arm64"

    /** The asset prefix for an app (variant-aware), or null when the catalog has no macOS asset. */
    fun prefix(app: CatalogApp, variantId: String? = null): String? {
        val assets = app.assets(variantId)
        val mac = assets["macos"] ?: assets["macos-arm64"] ?: assets["macos-x64"] ?: return null
        return prefixFromMacAsset(mac)
    }

    /** Strips `.zip` then a trailing macOS platform marker, case-insensitively. */
    fun prefixFromMacAsset(macAssetName: String): String {
        var s = macAssetName
        for (ext in listOf(".zip", ".tar.gz", ".dmg")) {
            if (s.endsWith(ext, ignoreCase = true)) {
                s = s.dropLast(ext.length); break
            }
        }
        // "-macOS", ".macOS", ".macOS.universal2", "-macOS-arm64", "-Full-macOS-arm64" …
        val markers = listOf(
            ".macos.universal2", "-macos.universal2",
            ".macos-arm64", "-macos-arm64", ".macos-x64", "-macos-x64",
            ".macos", "-macos",
        )
        val lower = s.lowercase()
        for (m in markers) {
            val at = lower.lastIndexOf(m)
            if (at >= 0 && at + m.length == s.length) return s.substring(0, at)
        }
        return s
    }

    /**
     * The exact APK asset name for an app at a version. [version] is the release tag as published
     * (`v2.1.1` or `2.1.1`); the asset always carries exactly one leading `v`.
     */
    fun apkName(app: CatalogApp, version: String, variantId: String? = null): String? {
        app.assets(variantId)[PLATFORM_KEY]?.let { return it }
        val prefix = prefix(app, variantId) ?: return null
        return "$prefix-v${VersionCompare.norm(version)}-android-arm64.apk"
    }

    /** The sidecar published beside the APK. */
    fun sha256SidecarName(apkName: String): String = "$apkName.sha256"
}
