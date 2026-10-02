package com.jamesbreedon.jbtheatretools.core

import com.jamesbreedon.jbtheatretools.net.DownloadCancelledException
import com.jamesbreedon.jbtheatretools.net.GitHubException
import com.jamesbreedon.jbtheatretools.net.ReleaseInfo
import java.io.IOException
import java.time.Instant

/**
 * Errors worth ONE automatic retry in a batch: network and I/O hiccups (and an HTTP error from the server). Never a
 * verification failure (those are plain refusals), a cancel, show lock, or an access / "no such release" answer.
 */
object TransientError {
    fun isTransient(e: Throwable?): Boolean = when (e) {
        null -> false
        is DownloadCancelledException, is ShowLockedException -> false
        is GitHubException -> e.kind == GitHubException.Kind.HTTP
        is IOException -> true
        else -> false
    }
}

/**
 * One batch's work list (Update all, Install every app, a setup import). Each app runs at most once per batch: when
 * the list runs out it is topped up with whatever the batch would list NOW that it hasn't had yet (an update found
 * meanwhile, a hold released) — never a repeat, so a failure isn't retried forever.
 */
class BatchQueue<T>(initial: List<T>, private val keyOf: (T) -> String) {
    private val had = LinkedHashSet<String>()
    private val work = ArrayList<T>()
    private var next = 0

    init { add(initial) }

    /** Appends the items of [candidates] this batch hasn't had; returns the ones added. */
    fun add(candidates: List<T>): List<T> {
        val added = candidates.filter { had.add(keyOf(it)) }
        work.addAll(added)
        return added
    }

    /** The next item, topping up from [refill] when the list has run out; null when there's nothing left. */
    fun next(refill: (() -> List<T>)? = null): T? {
        if (next >= work.size && refill != null) add(refill())
        return if (next < work.size) work[next++] else null
    }

    val keys: Set<String> get() = had
    val size: Int get() = work.size
}

/** One app in a batch: [tag] null = the latest release; [installedAtList] what was installed when it was listed. */
data class BatchSlot(
    val app: CatalogApp,
    val tag: String? = null,
    val installedAtList: String? = null,
    /** The version the slot installs, when known (the latest, or the tag) — "already at it" skips it. */
    val target: String? = null,
    /** A setup import: an app installed meanwhile is left as it is, whatever its version. */
    val onlyIfMissing: Boolean = false,
)

object BatchRules {
    /**
     * Why a slot is skipped just before its turn (null = go ahead). The person can keep working while a batch runs,
     * so the batch never undoes or repeats what they did: held since (an update), removed since, already at its
     * target, or (an import) installed since.
     */
    fun skipReason(slot: BatchSlot, installedNow: String?, heldNow: Boolean): String? = when {
        slot.onlyIfMissing && installedNow != null -> "is already installed"
        slot.tag == null && installedNow != null && heldNow -> "is held"
        slot.installedAtList != null && installedNow == null -> "was removed meanwhile"
        installedNow != null && slot.target != null && VersionCompare.equal(installedNow, slot.target) ->
            "is already at ${VersionCompare.display(slot.target)}"
        else -> null
    }

    /** Update all's list: installed, not held, a newer release with an Android build. */
    fun updateWork(statuses: List<AppStatus>): List<AppStatus> = statuses.filter { it.updatePending && it.canInstall }

    /**
     * Install every app's list: every app with an Android build that isn't installed, plus the pending updates of
     * installed, non-held apps — the desktop "Install every app".
     */
    fun installAllWork(statuses: List<AppStatus>): List<AppStatus> =
        statuses.filter { it.canInstall && !it.hidden && (!it.isInstalled || it.updatePending) }

    fun slotOf(s: AppStatus): BatchSlot = BatchSlot(s.app, null, s.installedVersion, s.latestVersion)
}

/** The version an app had before its last install changed it — the one-click Roll back's target. */
object PreviousVersion {
    /**
     * What to record after an install moved an app from [before] to [now]: nothing for a first install, the old
     * [recorded] value for a reinstall of the same version, else the version it replaced.
     */
    fun after(before: String?, now: String, recorded: String?): String? = when {
        before == null -> null
        VersionCompare.equal(before, now) -> recorded
        else -> before
    }

    /**
     * The release Roll back goes to: the previous version, only ever BACK (after a roll back "previous" is the newer
     * one), and only a release the strict install can pass — an Android build, signed checksums, and never a
     * development build without Development builds switched on. Null when there's nothing to roll back to.
     */
    fun rollbackTag(app: CatalogApp, installed: String?, previous: String?, releases: List<ReleaseInfo>, devChannel: Boolean): String? {
        if (installed == null || previous == null || !VersionCompare.isNewer(installed, previous)) return null
        return releases.firstOrNull { r ->
            !r.draft && VersionCompare.equal(r.tagName, previous) && (devChannel || !VersionCompare.isDev(r.tagName)) &&
                hasSignedManifest(r) && AndroidAsset.resolve(app, r.tagName, r.assets.map { it.name }) != null
        }?.tagName
    }

    fun hasSignedManifest(r: ReleaseInfo): Boolean =
        r.assets.any { it.name == "SHA256SUMS" } && r.assets.any { it.name == "SHA256SUMS.minisig" }

    /** The releases "Install version" lists: newest first, each with an Android build. */
    fun installable(app: CatalogApp, releases: List<ReleaseInfo>): List<ReleaseInfo> =
        releases.filter { !it.draft && AndroidAsset.resolve(app, it.tagName, it.assets.map { a -> a.name }) != null }
            .sortedWith { a, b -> VersionCompare.compare(b.tagName, a.tagName) }

    /** Android can't install an older version over a newer one: going to [target] means removing the app first. */
    fun needsRemoval(installed: String?, target: String): Boolean =
        installed != null && VersionCompare.isNewer(installed, target)
}

/** Row wording shared by the list and the sheet. */
object RowText {
    /** "Latest: v1.2.0 (3 days ago) · 22 MB" — the desktop row's latest line. */
    fun latestLine(latest: String, publishedIso: String?, sizeBytes: Long, now: Instant): String {
        val age = RelativeAge.parseIso(publishedIso)?.let { " (${RelativeAge.describe(it, now)})" } ?: ""
        val size = if (sizeBytes > 0) " · ${ByteSize.format(sizeBytes)}" else ""
        return "Latest: ${VersionCompare.display(latest)}$age$size"
    }

    /** A batch button: "Update all (3) · 45 MB" / "Install every app (12) · 300 MB". */
    fun batchLabel(title: String, count: Int, bytes: Long): String =
        "$title ($count)" + if (bytes > 0) " · ${ByteSize.format(bytes)}" else ""
}
