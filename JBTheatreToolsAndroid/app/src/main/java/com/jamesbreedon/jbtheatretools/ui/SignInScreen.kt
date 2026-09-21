package com.jamesbreedon.jbtheatretools.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.jamesbreedon.jbtheatretools.BuildConfig
import com.jamesbreedon.jbtheatretools.core.AuthMode

/**
 * First run (§1a): the passphrase (or a fine-grained PAT) is pasted once and kept in the Android
 * Keystore. Nothing is written to preferences or to the log.
 *
 * A debug build adds a third option — a local feed — so the verify + install path can be exercised
 * without a passphrase. `BuildConfig.ALLOW_DEBUG_FEED` is false in every release build, so the option
 * does not exist there.
 */
@Composable
fun SignInScreen(vm: LauncherViewModel) {
    val c = House.colors
    var mode by remember { mutableStateOf(AuthMode.SERVER) }
    var secret by remember { mutableStateOf("") }
    var feedBase by remember { mutableStateOf(vm.repo.settings.debugFeedBase) }
    var feedKey by remember { mutableStateOf(vm.repo.settings.debugFeedMinisignKey) }

    val topInset = WindowInsets.statusBars.asPaddingValues().calculateTopPadding()
    val bottomInset = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()

    Column(
        Modifier
            .fillMaxSize()
            .background(c.ground)
            .padding(top = topInset, bottom = bottomInset)
            .verticalScroll(rememberScrollState())
            .padding(horizontal = Metrics.gutterCompact),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        VSpace(48.dp)
        IdentityTile(64.dp)
        VSpace(14.dp)
        TitleText("JB Theatre Tools")
        VSpace(6.dp)
        BodyText("Enter the suite passphrase to see and install the tools.", maxLines = 3)
        VSpace(24.dp)

        val options = buildList {
            add("Passphrase")
            add("Access token")
            if (BuildConfig.ALLOW_DEBUG_FEED) add("Local feed")
        }
        val index = when (mode) {
            AuthMode.SERVER -> 0
            AuthMode.TOKEN -> 1
            AuthMode.DEBUG_FEED -> 2
        }
        Segmented(options, index, { i ->
            mode = when (i) {
                0 -> AuthMode.SERVER
                1 -> AuthMode.TOKEN
                else -> AuthMode.DEBUG_FEED
            }
        }, Modifier.fillMaxWidth())
        VSpace(16.dp)

        when (mode) {
            AuthMode.SERVER -> {
                HouseTextField(secret, { secret = it }, "Suite passphrase", Modifier.fillMaxWidth(), mask = true)
                VSpace(8.dp)
                SmallText(
                    "Spacing, capitals and punctuation don’t matter.",
                    color = c.text3, weight = FontWeight.Normal,
                )
            }

            AuthMode.TOKEN -> {
                HouseTextField(
                    secret, { secret = it }, "Fine-grained access token",
                    Modifier.fillMaxWidth(), mask = true,
                )
                VSpace(8.dp)
                SmallText(
                    "Read-only access to the tool repositories is enough.",
                    color = c.text3, weight = FontWeight.Normal,
                )
            }

            AuthMode.DEBUG_FEED -> {
                HouseTextField(feedBase, { feedBase = it }, "http://10.0.2.2:8787/ghapi", Modifier.fillMaxWidth())
                VSpace(8.dp)
                HouseTextField(
                    feedKey, { feedKey = it }, "minisign public key (throwaway)", Modifier.fillMaxWidth(),
                )
                VSpace(8.dp)
                SmallText(
                    "Debug builds only — an unauthenticated local release feed, signed with a " +
                        "throwaway key.",
                    color = c.text3, weight = FontWeight.Normal,
                )
            }
        }

        VSpace(20.dp)
        Box(Modifier.fillMaxWidth()) {
            PrimaryButton(
                text = "Continue",
                modifier = Modifier.fillMaxWidth(),
                enabled = when (mode) {
                    AuthMode.DEBUG_FEED -> feedBase.isNotBlank()
                    else -> secret.isNotBlank()
                },
            ) { vm.signIn(secret, mode, feedBase, feedKey) }
        }
        VSpace(16.dp)
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            BodyText(
                "The passphrase is kept in this device’s keystore. It is never written to " +
                    "settings or to the log.",
                maxLines = 4,
            )
            BodyText(
                "Installing a tool asks Android once for permission to install apps — that is " +
                    "how a tool gets onto the device without a store.",
                maxLines = 4,
            )
        }
        VSpace(32.dp)
    }
}
