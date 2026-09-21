package com.jamesbreedon.jbtheatretools.core

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * The suite passphrase / fine-grained PAT, kept in the **Android Keystore**.
 *
 * §5 rule 5: secrets go in `EncryptedSharedPreferences` (keys held by the Keystore, never extractable),
 * never in plain preferences, never in a JSON file, never in the log. The launcher's log names the
 * asset and the checksum result and nothing else — the same "never log the token" rule the macOS
 * Keychain store and the Windows DPAPI store carry.
 *
 * The values are read back only to build an `Authorization` header.
 */
class SecretStore(context: Context) {

    private val prefs: SharedPreferences by lazy {
        val key = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            FILE_NAME,
            key,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }

    /** Whether a passphrase is stored — does NOT return the secret. */
    fun hasPassphrase(): Boolean = !prefs.getString(KEY_PASSPHRASE, null).isNullOrEmpty()

    fun hasToken(): Boolean = !prefs.getString(KEY_TOKEN, null).isNullOrEmpty()

    fun passphrase(): String? = prefs.getString(KEY_PASSPHRASE, null)?.ifEmpty { null }

    fun token(): String? = prefs.getString(KEY_TOKEN, null)?.ifEmpty { null }

    fun savePassphrase(value: String) {
        prefs.edit().putString(KEY_PASSPHRASE, value).remove(KEY_TOKEN).apply()
    }

    fun saveToken(value: String) {
        prefs.edit().putString(KEY_TOKEN, value).remove(KEY_PASSPHRASE).apply()
    }

    fun clear() {
        prefs.edit().remove(KEY_PASSPHRASE).remove(KEY_TOKEN).apply()
    }

    companion object {
        /** Excluded from backup and device transfer in res/xml/data_extraction_rules.xml. */
        const val FILE_NAME = "jbtt-secrets"
        private const val KEY_PASSPHRASE = "server-pass"
        private const val KEY_TOKEN = "github-token"
    }
}
