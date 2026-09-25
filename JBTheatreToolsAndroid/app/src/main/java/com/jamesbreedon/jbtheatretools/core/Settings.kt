package com.jamesbreedon.jbtheatretools.core

import android.content.Context

/** Appearance, per §5 rule 3/16 — the word is "Appearance", never "Theme". */
enum class Appearance { LIGHT, DARK, SYSTEM }

/** Where releases are read from. */
enum class AuthMode {
    /** The built-in download server, reached with the suite passphrase (the default). */
    SERVER,

    /** Direct GitHub with a fine-grained personal access token. */
    TOKEN,

    /** Debug builds only: an unauthenticated local feed, for exercising the install path. */
    DEBUG_FEED,
}

/**
 * Non-secret preferences. Secrets live in [SecretStore]; nothing here is ever a credential.
 */
class Settings(context: Context) {
    private val prefs = context.getSharedPreferences("jbtt-settings", Context.MODE_PRIVATE)

    var appearance: Appearance
        get() = runCatching { Appearance.valueOf(prefs.getString(KEY_APPEARANCE, null) ?: "SYSTEM") }
            .getOrDefault(Appearance.SYSTEM)
        set(value) = prefs.edit().putString(KEY_APPEARANCE, value.name).apply()

    var authMode: AuthMode
        get() = runCatching { AuthMode.valueOf(prefs.getString(KEY_AUTH_MODE, null) ?: "SERVER") }
            .getOrDefault(AuthMode.SERVER)
        set(value) = prefs.edit().putString(KEY_AUTH_MODE, value.name).apply()

    /**
     * Debug-only base URL for the release feed (see BuildConfig.ALLOW_DEBUG_FEED and the README).
     * Ignored entirely in a release build, where [AuthMode.DEBUG_FEED] can never be selected.
     */
    var debugFeedBase: String
        get() = prefs.getString(KEY_DEBUG_FEED, "").orEmpty()
        set(value) = prefs.edit().putString(KEY_DEBUG_FEED, value.trim()).apply()

    /**
     * Debug-only minisign public key for the local feed. A local feed is signed with a THROWAWAY key,
     * not the suite key, so the verification chain can be exercised without the real secret existing
     * anywhere near this machine. Ignored entirely in a release build, where the pinned suite key is
     * the only key that verifies anything.
     */
    var debugFeedMinisignKey: String
        get() = prefs.getString(KEY_DEBUG_FEED_KEY, "").orEmpty()
        set(value) = prefs.edit().putString(KEY_DEBUG_FEED_KEY, value.trim()).apply()

    /** A local override of the relay base, clamped by [com.jamesbreedon.jbtheatretools.net.RelayPolicy]. */
    var relayOverride: String
        get() = prefs.getString(KEY_RELAY_OVERRIDE, "").orEmpty()
        set(value) = prefs.edit().putString(KEY_RELAY_OVERRIDE, value.trim()).apply()

    /**
     * Development builds (`vX.Y.Z-dev.N` pre-releases built on the maintainer's Mac): off unless switched on
     * on THIS device. The switch is shown only while this is on, or right after seven quick taps on the version
     * line (Android's developer-options gesture) — nothing about the reveal is stored.
     */
    var devChannel: Boolean
        get() = prefs.getBoolean(KEY_DEV_CHANNEL, false)
        set(value) = prefs.edit().putBoolean(KEY_DEV_CHANNEL, value).apply()


    /**
     * The release TAG each app was last installed from, by catalog id. A dev build's own versionName is the
     * plain X.Y.Z (`0.1.0-dev.2` ships as versionName `0.1.0`), so without this the stable 0.1.0 would look
     * "already installed" next to a dev build. Trusted only while the package still reports that X.Y.Z.
     */
    fun installedTag(catalogId: String): String? = prefs.getString(KEY_TAG_PREFIX + catalogId, null)

    fun setInstalledTag(catalogId: String, tag: String?) =
        prefs.edit().apply { if (tag == null) remove(KEY_TAG_PREFIX + catalogId) else putString(KEY_TAG_PREFIX + catalogId, tag) }.apply()

    var firstRunDone: Boolean
        get() = prefs.getBoolean(KEY_FIRST_RUN_DONE, false)
        set(value) = prefs.edit().putBoolean(KEY_FIRST_RUN_DONE, value).apply()

    private companion object {
        const val KEY_APPEARANCE = "appearance"
        const val KEY_AUTH_MODE = "auth-mode"
        const val KEY_DEBUG_FEED = "debug-feed-base"
        const val KEY_DEBUG_FEED_KEY = "debug-feed-minisign-key"
        const val KEY_RELAY_OVERRIDE = "relay-override"
        const val KEY_FIRST_RUN_DONE = "first-run-done"
        const val KEY_DEV_CHANNEL = "dev-channel"
        const val KEY_TAG_PREFIX = "installed-tag:"
    }
}
