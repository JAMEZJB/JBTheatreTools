package com.jamesbreedon.jbtheatretools.core

import com.jamesbreedon.jbtheatretools.net.GitHubException
import java.io.IOException
import java.io.InterruptedIOException
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import javax.net.ssl.SSLException

/**
 * A failure as the user reads it — in a row, the history and the release-notes sheet. Raw exception text (host
 * names, socket details, stack-trace wording) never reaches the UI; the log keeps the detail.
 */
object UserMessage {
    fun of(e: Throwable?): String = when (e) {
        // The launcher's own refusals already read as one plain line.
        is GitHubException, is IllegalStateException, is SetupProfile.FormatError, is Minisign.VerifyException ->
            e.message?.takeIf { it.isNotBlank() } ?: GENERIC
        is UnknownHostException, is ConnectException, is NoRouteToHostException ->
            "Couldn't reach the download server — check the network connection and try again."
        is SocketTimeoutException, is InterruptedIOException ->
            "The download server stopped responding — try again."
        is SSLException -> "Couldn't make a secure connection to the download server — try again."
        is IOException -> "The connection was interrupted — try again."
        else -> GENERIC
    }

    const val GENERIC = "Something went wrong — the log has the details."
}
