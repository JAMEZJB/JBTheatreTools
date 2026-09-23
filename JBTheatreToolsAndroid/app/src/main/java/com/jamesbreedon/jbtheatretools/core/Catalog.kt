package com.jamesbreedon.jbtheatretools.core

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/**
 * The shared app catalog — the repo-root `catalog.json`, the SAME file the macOS and Windows
 * launchers bundle. Gradle copies it into this APK's assets at build time; there is no second copy.
 *
 * Only the fields the Android launcher needs are modelled; unknown keys are ignored so a catalog
 * written for a newer launcher still parses.
 */
@Serializable
data class Catalog(
    val schemaVersion: Int = 1,
    val apps: List<CatalogApp> = emptyList(),
    @SerialName("self") val selfInfo: SelfInfo? = null,
    /** Built-in download-relay base URL for the default (passphrase) auth mode. */
    val downloadServer: String? = null,
    /** Section order for the grouped list; a category an app uses that isn't listed is appended. */
    val categories: List<String> = emptyList(),
) {
    companion object {
        val json = Json { ignoreUnknownKeys = true; isLenient = true }

        fun parse(text: String): Catalog = json.decodeFromString(serializer(), text)
    }

    /** Category order for the expanded-width grouped list: the declared order, then any strays A–Z. */
    fun orderedCategories(): List<String> {
        val used = apps.mapNotNull { it.category }.distinct()
        return categories.filter { it in used } + used.filterNot { it in categories }.sorted()
    }
}

/** One installable app in the catalog. */
@Serializable
data class CatalogApp(
    val id: String,
    val name: String,
    val blurb: String = "",
    val category: String? = null,
    val whatsNew: String? = null,
    val whatsNewVersion: String? = null,
    val owner: String,
    val repo: String,
    /**
     * Platform key -> exact release-asset name. The desktop keys are macos, windows-x64,
     * windows-arm64 (+ the per-arch macOS keys). An optional `android-arm64` key, when the catalog
     * grows one, overrides the name this launcher would otherwise derive (see [AndroidAsset]).
     */
    val assets: Map<String, String> = emptyMap(),
    /** Optional downloadable variants of the SAME app (e.g. NDI "Light" / "Full"). */
    val variants: List<AppVariant> = emptyList(),
) {
    val hasVariants: Boolean get() = variants.size > 1

    /** The asset map for a variant id (nil/unknown -> the default = first variant, else `assets`). */
    fun assets(variantId: String?): Map<String, String> {
        if (variants.isEmpty()) return assets
        return (variants.firstOrNull { it.id == variantId } ?: variants[0]).assets
    }

    fun isDefaultVariant(variantId: String?): Boolean {
        if (!hasVariants) return true
        val first = variants.firstOrNull()?.id ?: return true
        return variantId == null || variantId == first
    }

    /**
     * The install-manifest key for a variant. Each variant is its own slot, keyed by the app id for
     * the default variant and `<id>@<variant>` otherwise — identical to the desktop launchers.
     */
    fun installKey(variantId: String?): String =
        if (isDefaultVariant(variantId)) id else "$id@$variantId"
}

@Serializable
data class AppVariant(
    val id: String,
    val label: String,
    val assets: Map<String, String> = emptyMap(),
)

/** JB Theatre Tools' own release info (for the launcher's self-update check). */
@Serializable
data class SelfInfo(
    val owner: String,
    val repo: String,
    val assets: Map<String, String> = emptyMap(),
    val whatsNew: String? = null,
    val whatsNewVersion: String? = null,
) {
    /** A synthetic catalog entry so the self-update goes through the same verification path. */
    fun asApp(): CatalogApp = CatalogApp(
        id = "jbtheatretools", name = "JB Theatre Tools", blurb = "",
        owner = owner, repo = repo, assets = assets,
    )
}
