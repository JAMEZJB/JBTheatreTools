package com.jamesbreedon.jbtheatretools.core

/**
 * Catalog id -> Android applicationId, in ONE place.
 *
 * Every suite app's Android build uses `com.jamesbreedon.<slug>`, where the slug is the app's short
 * name. This table is what the launcher uses to ask `PackageManager` whether an app is installed and
 * at which version, so it MUST match each app's own `app.properties` (`appId=`). If an app ever ships
 * with a different applicationId the launcher will simply never see it as installed.
 *
 * The same list appears in `AndroidManifest.xml`'s `<queries>` block (package visibility on Android
 * 11+); add to both, or the lookup silently returns "not installed".
 */
object PackageIds {
    /** catalog id -> slug. */
    private val slugs = mapOf(
        "helocontrol" to "helo",
        "dmxtools" to "dmx",
        "psntools" to "psn",
        "timecodetools" to "timecode",
        "shownetscanner" to "shownet",
        "nditools" to "ndi",
        "showcontroltools" to "showcontrol",
        "projectorcontrol" to "projector",
        "powercalc" to "power",
        "networkportmap" to "portmap",
        "showdashboard" to "dashboard",
        "showhandbook" to "handbook",
        "deskconvert" to "desk",
        "ciscobrotherlabels" to "labels",
        "machineinventory" to "inventory",
        "renametools" to "rename",
        "surtitletools" to "surtitle",
        "imagetools" to "image",
        "ciscoswitchtools" to "cisco",
        "pdftools" to "pdf",
        "reporadar" to "radar",
        "convert" to "convert",
    )

    /** This launcher's own package — the self-update target. */
    const val LAUNCHER = "com.jamesbreedon.jbtheatretools"

    const val PREFIX = "com.jamesbreedon."

    fun slug(catalogId: String): String? = slugs[catalogId]

    /** The applicationId for a catalog id, or null when the catalog carries an app we have no slug for. */
    fun packageId(catalogId: String): String? {
        if (catalogId == "jbtheatretools") return LAUNCHER
        val slug = slugs[catalogId] ?: return null
        return PREFIX + slug
    }

    /** Every package the launcher manages — used by the installed-apps scan. */
    fun all(): List<String> = slugs.values.map { PREFIX + it }

    val catalogIds: Set<String> get() = slugs.keys
}
