package com.jamesbreedon.jbtheatretools.net

import com.jamesbreedon.jbtheatretools.core.Passphrase
import com.jamesbreedon.jbtheatretools.core.WhatsNewNotes
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.net.HttpURLConnection
import java.net.URL
import java.util.Base64

@Serializable
data class ReleaseAsset(
    val id: Long = 0,
    val name: String,
    val size: Long = 0,
)

@Serializable
data class ReleaseInfo(
    @SerialName("tag_name") val tagName: String,
    val assets: List<ReleaseAsset> = emptyList(),
    val prerelease: Boolean = false,
    val draft: Boolean = false,
    /** The release's Markdown notes (shown in-app via ReleaseNotesText). */
    val body: String? = null,
    /** ISO 8601 publish time, kept as text so an odd value can never fail the whole release list. */
    @SerialName("published_at") val publishedAt: String? = null,
)

/** Thrown out of [GitHubClient.downloadAsset] when its `shouldCancel` check says stop. Not a failure. */
class DownloadCancelledException : IOException("Download cancelled")

class GitHubException(val kind: Kind, message: String) : IOException(message) {
    enum class Kind { NO_RELEASE, NOT_ACCESSIBLE, UNAUTHORIZED, HTTP, ASSET_NOT_FOUND, BAD_RESPONSE, BAD_URL }

    companion object {
        fun noRelease() = GitHubException(Kind.NO_RELEASE, "No published release found.")
        fun notAccessible() = GitHubException(Kind.NOT_ACCESSIBLE, "This token can’t access that repository.")
        fun unauthorized() = GitHubException(Kind.UNAUTHORIZED, "The passphrase or token was not accepted.")
        fun http(code: Int) = GitHubException(Kind.HTTP, "The server returned HTTP $code.")
        fun assetNotFound(name: String) = GitHubException(Kind.ASSET_NOT_FOUND, "Release has no asset named “$name”.")
        fun badResponse() = GitHubException(Kind.BAD_RESPONSE, "Unexpected response from the release feed.")
        fun badUrl() = GitHubException(Kind.BAD_URL, "The download-server address is invalid. Check it in Settings.")
    }
}

/**
 * Talks to the GitHub REST API — directly with a fine-grained PAT, or through the suite's download
 * relay with the passphrase. Both modes use the SAME endpoint shapes, because the relay forwards the
 * same paths to GitHub with its own server-side token; only the base URL and the `Authorization`
 * header differ. This mirrors the macOS `GitHubClient.swift` and the Windows `GitHubClient.cs`.
 *
 * Private release assets cannot be fetched from `browser_download_url`; the API asset endpoint is hit
 * with `Accept: application/octet-stream`, and the 302 to the signed S3 URL is followed MANUALLY with
 * the `Authorization` header stripped — S3 rejects a request carrying both a credential header and its
 * own signed query parameters, and sending the suite passphrase to a third-party host would be worse.
 */
class GitHubClient private constructor(
    private val apiBase: String,
    private val authValue: String?,
) {
    companion object {
        private val json = Json { ignoreUnknownKeys = true; isLenient = true }
        const val GITHUB_API = "https://api.github.com"

        /** Direct GitHub access with a fine-grained PAT (or none, for public repos). */
        fun direct(token: String?): GitHubClient =
            GitHubClient(GITHUB_API, if (token.isNullOrEmpty()) null else "Bearer $token")

        /**
         * Download-relay access: the same API paths, sent to the relay with HTTP Basic auth (fixed
         * username "suite" + the NORMALISED suite passphrase — the relay applies the same reduction
         * rule, so entry is case- and punctuation-insensitive).
         */
        fun server(serverBase: String, passphrase: String): GitHubClient {
            val base = serverBase.trim().trimEnd('/')
            val pass = Passphrase.normalize(passphrase)
            val basic = Base64.getEncoder().encodeToString("suite:$pass".toByteArray(Charsets.UTF_8))
            return GitHubClient(base, "Basic $basic")
        }

        /**
         * A debug-only feed that speaks the same REST shapes with no credential at all — used by the
         * emulator install-path proof. Never reachable from a release build (see BuildConfig.ALLOW_DEBUG_FEED).
         */
        fun unauthenticated(base: String): GitHubClient = GitHubClient(base.trim().trimEnd('/'), null)
    }

    val isRelay: Boolean get() = apiBase != GITHUB_API

    private fun open(url: URL, accept: String): HttpURLConnection {
        val conn = url.openConnection() as HttpURLConnection
        conn.instanceFollowRedirects = false      // we strip the credential across the redirect ourselves
        conn.connectTimeout = 20_000
        conn.readTimeout = 60_000
        authValue?.let { conn.setRequestProperty("Authorization", it) }
        conn.setRequestProperty("Accept", accept)
        conn.setRequestProperty("X-GitHub-Api-Version", "2022-11-28")
        conn.setRequestProperty("User-Agent", "JBTheatreTools")
        return conn
    }

    private fun url(path: String): URL =
        try { URL("$apiBase$path") } catch (e: Exception) { throw GitHubException.badUrl() }

    private fun readBody(conn: HttpURLConnection): ByteArray {
        val stream = conn.inputStream ?: return ByteArray(0)
        return stream.use { it.readBytes() }
    }

    /** The latest (non-prerelease) release. Throws NO_RELEASE on 404. */
    fun latestRelease(owner: String, repo: String): ReleaseInfo {
        val conn = open(url("/repos/$owner/$repo/releases/latest"), "application/vnd.github+json")
        try {
            when (val code = conn.responseCode) {
                200 -> Unit
                401, 403 -> throw GitHubException.unauthorized()
                404 -> throw GitHubException.noRelease()
                else -> throw GitHubException.http(code)
            }
            val body = readBody(conn)
            return try {
                json.decodeFromString(ReleaseInfo.serializer(), String(body, Charsets.UTF_8))
            } catch (e: Exception) {
                throw GitHubException.badResponse()
            }
        } finally {
            conn.disconnect()
        }
    }

    /**
     * All (non-draft) releases, newest first. The list endpoint returns `200 []` for an accessible repo
     * with no releases and `404` only when the credential can't see the repo, so a 404 here means NO
     * ACCESS (not "no release") and a 401 means the credential itself is bad.
     */
    fun releases(owner: String, repo: String): List<ReleaseInfo> {
        val conn = open(url("/repos/$owner/$repo/releases?per_page=50"), "application/vnd.github+json")
        try {
            when (val code = conn.responseCode) {
                200 -> Unit
                401 -> throw GitHubException.unauthorized()
                404 -> throw GitHubException.notAccessible()
                else -> throw GitHubException.http(code)
            }
            val body = readBody(conn)
            return try {
                json.decodeFromString(
                    kotlinx.serialization.builtins.ListSerializer(ReleaseInfo.serializer()),
                    String(body, Charsets.UTF_8),
                ).filter { !it.draft }
            } catch (e: Exception) {
                throw GitHubException.badResponse()
            }
        } finally {
            conn.disconnect()
        }
    }

    /**
     * The relay's editable "New in" lines. Relay mode only — returns null on direct GitHub, when the
     * relay has no document (404), or on any error; callers then keep the bundled catalog lines.
     */
    fun whatsNewNotes(): Pair<WhatsNewNotes, ByteArray>? {
        if (!isRelay) return null
        val notesUrl = notesUrl(apiBase) ?: return null
        return try {
            val conn = open(URL(notesUrl), "application/json")
            conn.useCaches = false
            try {
                if (conn.responseCode != 200) return null
                val body = readBody(conn)
                WhatsNewNotes.parse(body) to body
            } finally {
                conn.disconnect()
            }
        } catch (e: Exception) {
            null
        }
    }

    /**
     * Downloads a release asset by id to [dest], reporting fractional progress (0…1). [shouldCancel] is polled
     * between chunks; when it returns true the transfer stops with [DownloadCancelledException] (the caller
     * removes the partial file).
     */
    fun downloadAsset(
        owner: String,
        repo: String,
        assetId: Long,
        dest: File,
        progress: ((Double) -> Unit)? = null,
        shouldCancel: (() -> Boolean)? = null,
    ) {
        var target = url("/repos/$owner/$repo/releases/assets/$assetId")
        var carryAuth = true
        var hops = 0
        while (true) {
            val conn = if (carryAuth) open(target, "application/octet-stream") else {
                (target.openConnection() as HttpURLConnection).apply {
                    instanceFollowRedirects = false
                    connectTimeout = 20_000
                    readTimeout = 60_000
                    setRequestProperty("Accept", "application/octet-stream")
                    setRequestProperty("User-Agent", "JBTheatreTools")
                }
            }
            try {
                val code = conn.responseCode
                if (code in 300..399) {
                    val location = conn.getHeaderField("Location") ?: throw GitHubException.badResponse()
                    if (++hops > 5) throw GitHubException.badResponse()
                    target = URL(target, location)
                    // Cross-host redirect to signed storage: NEVER carry the credential.
                    carryAuth = false
                    continue
                }
                when (code) {
                    200 -> Unit
                    401, 403 -> throw GitHubException.unauthorized()
                    404 -> throw GitHubException.assetNotFound("asset $assetId")
                    else -> throw GitHubException.http(code)
                }
                val total = conn.contentLengthLong
                dest.parentFile?.mkdirs()
                conn.inputStream.use { input -> copy(input, dest, total, progress, shouldCancel) }
                progress?.invoke(1.0)
                return
            } finally {
                conn.disconnect()
            }
        }
    }

    private fun copy(
        input: InputStream,
        dest: File,
        total: Long,
        progress: ((Double) -> Unit)?,
        shouldCancel: (() -> Boolean)?,
    ) {
        dest.outputStream().use { out ->
            val buf = ByteArray(1 shl 16)
            var written = 0L
            while (true) {
                if (shouldCancel?.invoke() == true) throw DownloadCancelledException()
                val n = input.read(buf)
                if (n <= 0) break
                out.write(buf, 0, n)
                written += n
                if (total > 0) progress?.invoke(written.toDouble() / total.toDouble())
            }
        }
    }
}

/**
 * `<origin of the relay base>/notes/whats-new.json` — the base is the `/ghapi` pass-through root, and
 * the notes live beside it, not under it.
 */
fun notesUrl(relayBase: String): String? = try {
    val base = URL(relayBase)
    val port = if (base.port == -1) "" else ":${base.port}"
    "${base.protocol}://${base.host}$port/notes/whats-new.json"
} catch (e: Exception) {
    null
}

/**
 * Guards the download-relay URL. The built-in relay URL lives in the catalog and is trusted. A local
 * override is honoured only when it parses, is `https`, and its host is `jamesbreedon.com` or a
 * subdomain — so nothing that can write this app's preferences can silently redirect every API call
 * (and with it the suite passphrase) to somewhere else. Mirrors the macOS `RelayPolicy`.
 */
object RelayPolicy {
    fun validatedOverride(raw: String?): String? {
        val s = raw?.trim().orEmpty()
        if (s.isEmpty()) return null
        return try {
            val u = URL(s)
            val host = u.host.lowercase()
            if (u.protocol.lowercase() != "https") return null
            if (host == "jamesbreedon.com" || host.endsWith(".jamesbreedon.com")) s else null
        } catch (e: Exception) {
            null
        }
    }
}
