package com.jamesbreedon.jbtheatretools.core

import android.app.job.JobInfo
import android.app.job.JobParameters
import android.app.job.JobScheduler
import android.app.job.JobService
import android.content.ComponentName
import android.content.Context
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.launch

/** Whether the launcher's window is on screen (set by MainActivity) — notifications are only posted when it isn't. */
object AppVisibility {
    @Volatile var foreground = false
}

/**
 * "Updates available" after a check — shared by the in-app scheduler and the background job, so both remember the
 * same "already announced" set. One notification per new version (never for held apps), and only when the launcher
 * isn't on screen.
 */
object UpdateAnnouncer {
    private const val LAUNCHER_ID = "jbtheatretools"

    /** [busy]: apps being installed (or queued in an Update all / import) right now — never announced. */
    fun announce(
        settings: Settings, notifier: Notifier, statuses: List<AppStatus>, launcherUpdate: String?,
        busy: Set<String> = ActiveInstalls.ids(),
    ) {
        val pending = statuses.filter { it.updatePending && it.latestVersion != null && it.app.id !in busy }
            .map { UpdatePolicy.Pending(it.app.id, it.app.name, it.latestVersion!!) } +
            listOfNotNull(launcherUpdate?.takeIf { LAUNCHER_ID !in busy }?.let { UpdatePolicy.Pending(LAUNCHER_ID, "JB Theatre Tools", it) })
        val already = settings.notifiedUpdates
        val (toNotify, notified) = UpdatePolicy.notify(pending, already)
        // An app whose check failed (no latest version) keeps what it was announced at; so does the launcher, whose
        // check reports "nothing" and "failed" alike (a stale launcher key only ever matches an installed version).
        // An app that's installing keeps its key too (if that install fails, the update isn't announced twice).
        val unchecked = statuses.filter { it.latestVersion == null }.map { it.app.id }.toSet() + busy +
            (if (launcherUpdate == null) setOf(LAUNCHER_ID) else emptySet())
        settings.notifiedUpdates = UpdatePolicy.remembered(notified, already, unchecked).toSet()
        // Show lock: nothing announces itself during a show.
        if (toNotify.isNotEmpty() && settings.notifyUpdates && !settings.showLock && !AppVisibility.foreground) {
            notifier.post(UpdatePolicy.notificationTitle(toNotify.size), UpdatePolicy.notificationBody(toNotify))
        }
    }
}

/**
 * With notifications on, checks keep running at the chosen interval after the launcher is closed — as a periodic
 * JobScheduler job that needs a network connection (Android decides the exact moment, batching it with other work
 * and deferring it in Doze). Not persisted across a reboot; opening the launcher schedules it again.
 */
object UpdateCheckScheduler {
    private const val JOB_ID = 7301

    /** [canRun]: signed in, and notifications may be posted. */
    fun sync(context: Context, settings: Settings, canRun: Boolean) {
        val js = context.getSystemService(JobScheduler::class.java) ?: return
        val interval = UpdatePolicy.interval(settings.autoCheckInterval)?.toMillis()
        if (!canRun || !settings.notifyUpdates || interval == null) {
            js.cancel(JOB_ID)
            return
        }
        if (js.getPendingJob(JOB_ID)?.intervalMillis == interval) return   // already scheduled at this interval
        val job = JobInfo.Builder(JOB_ID, ComponentName(context, UpdateCheckJob::class.java))
            .setRequiredNetworkType(JobInfo.NETWORK_TYPE_UNMETERED)   // a release check never spends mobile data
            .setRequiresBatteryNotLow(true)
            .setPeriodic(interval)
            .build()
        runCatching { js.schedule(job) }
    }
}

/** The background check behind [UpdateCheckScheduler]: refresh, then announce anything new. Never installs. */
class UpdateCheckJob : JobService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    override fun onStartJob(params: JobParameters): Boolean {
        if (AppVisibility.foreground) return false   // the open launcher runs its own checks
        scope.launch {
            try {
                val repo = LauncherRepository(applicationContext)
                val notifier = Notifier(applicationContext)
                if (repo.settings.notifyUpdates && !repo.settings.showLock && notifier.canPost() && repo.hasCredential()) {
                    val statuses = repo.refresh()
                    val launcherUpdate = repo.checkLauncherUpdate()
                    UpdateAnnouncer.announce(repo.settings, notifier, statuses, launcherUpdate)
                }
            } catch (_: Exception) {
                // Best effort: the next run tries again.
            } finally {
                jobFinished(params, false)
            }
        }
        return true
    }

    override fun onStopJob(params: JobParameters): Boolean {
        scope.coroutineContext.cancelChildren()
        return true
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }
}
