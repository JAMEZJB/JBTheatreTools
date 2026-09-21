package com.jamesbreedon.jbtheatretools.install

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow

/**
 * Where a `PackageInstaller` session reports back.
 *
 * A session can finish three ways: it succeeds, it fails, or (Android 10–13, and for any app the
 * launcher doesn't own the update of) it asks for the system's confirmation dialog first — which is
 * the one prompt the user sees per install. That intent is forwarded to the activity, never swallowed.
 */
class InstallResultReceiver : BroadcastReceiver() {

    override fun onReceive(context: Context, intent: Intent) {
        val status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)
        val sessionKey = intent.getStringExtra(EXTRA_SESSION_KEY).orEmpty()
        val packageName = intent.getStringExtra(PackageInstaller.EXTRA_PACKAGE_NAME)
        when (status) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)
                if (confirm != null) {
                    confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    context.startActivity(confirm)
                }
                emit(Event(sessionKey, State.NEEDS_CONFIRMATION, packageName, null))
            }

            PackageInstaller.STATUS_SUCCESS ->
                emit(Event(sessionKey, State.SUCCEEDED, packageName, null))

            else -> {
                val message = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE)
                    ?: "install failed (status $status)"
                emit(Event(sessionKey, State.FAILED, packageName, message))
            }
        }
    }

    enum class State { NEEDS_CONFIRMATION, SUCCEEDED, FAILED }

    data class Event(
        val sessionKey: String,
        val state: State,
        val packageName: String?,
        val message: String?,
    )

    companion object {
        const val EXTRA_SESSION_KEY = "com.jamesbreedon.jbtheatretools.SESSION_KEY"

        private val _events = MutableSharedFlow<Event>(replay = 0, extraBufferCapacity = 32)
        val events: SharedFlow<Event> = _events

        private fun emit(event: Event) {
            _events.tryEmit(event)
        }
    }
}
