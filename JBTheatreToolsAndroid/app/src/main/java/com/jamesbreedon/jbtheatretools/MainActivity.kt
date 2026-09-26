package com.jamesbreedon.jbtheatretools

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.windowsizeclass.ExperimentalMaterial3WindowSizeClassApi
import androidx.compose.material3.windowsizeclass.calculateWindowSizeClass
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.core.splashscreen.SplashScreen.Companion.installSplashScreen
import androidx.core.view.WindowCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.lifecycleScope
import androidx.lifecycle.repeatOnLifecycle
import com.jamesbreedon.jbtheatretools.core.Appearance
import com.jamesbreedon.jbtheatretools.ui.House
import com.jamesbreedon.jbtheatretools.ui.HouseTheme
import com.jamesbreedon.jbtheatretools.ui.LauncherScreen
import com.jamesbreedon.jbtheatretools.ui.LauncherViewModel
import kotlinx.coroutines.launch

/**
 * The whole launcher is one activity: edge-to-edge, predictive back, and it survives configuration
 * changes (rotation, split-screen resize, unfolding) without losing state — `configChanges` in the
 * manifest keeps the window alive and Compose re-lays out on the new size class (§4b).
 */
class MainActivity : ComponentActivity() {

    private val vm: LauncherViewModel by viewModels()

    @OptIn(ExperimentalMaterial3WindowSizeClassApi::class)
    override fun onCreate(savedInstanceState: Bundle?) {
        installSplashScreen()
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)

        // DEBUG ONLY: point the launcher at a local release feed (and the throwaway minisign key it is
        // signed with) without typing them in. See README "Debug feed override". There is no such path
        // in a release build — BuildConfig.ALLOW_DEBUG_FEED is false there.
        if (BuildConfig.ALLOW_DEBUG_FEED) {
            val base = intent?.getStringExtra("jbtt_debug_feed")
            if (!base.isNullOrBlank()) {
                val key = intent?.getStringExtra("jbtt_debug_feed_key").orEmpty()
                vm.signIn("", com.jamesbreedon.jbtheatretools.core.AuthMode.DEBUG_FEED, base, key)
            }
            // Also debug-only: pick the Appearance up front, so a review screenshot doesn't have to
            // drive the system's night mode (which would restart the activity mid-capture).
            intent?.getStringExtra("jbtt_debug_appearance")?.uppercase()?.let { wanted ->
                runCatching { Appearance.valueOf(wanted) }.getOrNull()?.let { vm.setAppearance(it) }
            }
        }

        val activity = this
        setContent {
            val state by vm.state.collectAsState()

            // System bars are tinted to --surface and their icons flip with the theme (§2).
            LaunchedEffect(state.appearance) {
                val dark = when (state.appearance) {
                    Appearance.LIGHT -> false
                    Appearance.DARK -> true
                    Appearance.SYSTEM -> resources.configuration.uiMode and
                        android.content.res.Configuration.UI_MODE_NIGHT_MASK ==
                        android.content.res.Configuration.UI_MODE_NIGHT_YES
                }
                enableEdgeToEdge(
                    statusBarStyle = if (dark) {
                        SystemBarStyle.dark(android.graphics.Color.TRANSPARENT)
                    } else {
                        SystemBarStyle.light(android.graphics.Color.TRANSPARENT, android.graphics.Color.TRANSPARENT)
                    },
                    navigationBarStyle = if (dark) {
                        SystemBarStyle.dark(android.graphics.Color.TRANSPARENT)
                    } else {
                        SystemBarStyle.light(android.graphics.Color.TRANSPARENT, android.graphics.Color.TRANSPARENT)
                    },
                )
            }

            HouseTheme(state.appearance) {
                Box(Modifier.fillMaxSize().background(House.colors.ground)) {
                    LauncherScreen(vm, calculateWindowSizeClass(activity).widthSizeClass)
                }
            }
        }

        // Notifications are only posted while the launcher isn't on screen.
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.STARTED) {
                vm.setForeground(true)
                try { kotlinx.coroutines.awaitCancellation() } finally { vm.setForeground(false) }
            }
        }

        // A removal happens in the system's uninstall flow, so re-read what's installed on resume.
        lifecycleScope.launch {
            repeatOnLifecycle(Lifecycle.State.RESUMED) { vm.refreshInstalledOnly() }
        }
    }
}
