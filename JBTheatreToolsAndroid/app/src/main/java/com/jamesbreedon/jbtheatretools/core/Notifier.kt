package com.jamesbreedon.jbtheatretools.core

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import com.jamesbreedon.jbtheatretools.MainActivity
import com.jamesbreedon.jbtheatretools.R

/**
 * "Updates available" notifications (one channel, "Updates"). Posts nothing without the POST_NOTIFICATIONS
 * permission on Android 13+ — the About screen asks for it when the user switches notifications on. Tapping the
 * notification opens the launcher.
 */
class Notifier(private val context: Context) {

    fun canPost(): Boolean {
        val permitted = Build.VERSION.SDK_INT < 33 ||
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
        val nm = context.getSystemService(NotificationManager::class.java)
        return permitted && (nm?.areNotificationsEnabled() ?: false)
    }

    /**
     * The "Updates" channel, created up front so it can be configured before the first notification. LOW importance:
     * it shows in the shade but never makes a sound — a phone on the prompt desk must not chime mid-show.
     */
    fun ensureChannel() {
        val nm = context.getSystemService(NotificationManager::class.java) ?: return
        runCatching {
            nm.createNotificationChannel(
                NotificationChannel(CHANNEL, "Updates", NotificationManager.IMPORTANCE_LOW).apply {
                    description = "New versions of the suite's apps"
                }
            )
        }
    }

    fun post(title: String, body: String) {
        if (!canPost()) return
        val nm = context.getSystemService(NotificationManager::class.java) ?: return
        ensureChannel()
        runCatching {
            val open = PendingIntent.getActivity(
                context, 0,
                Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP),
                PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
            )
            val n = Notification.Builder(context, CHANNEL)
                .setSmallIcon(R.drawable.ic_stat_updates)
                .setContentTitle(title)
                .setContentText(body)
                .setStyle(Notification.BigTextStyle().bigText(body))
                .setContentIntent(open)
                .setAutoCancel(true)
                .build()
            nm.notify(NOTIFICATION_ID, n)
        }
    }

    private companion object {
        const val CHANNEL = "updates"
        const val NOTIFICATION_ID = 1001
    }
}
