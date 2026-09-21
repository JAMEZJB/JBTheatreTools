package com.jamesbreedon.jbtheatretools.ui

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.unit.dp

/**
 * The Tabler outline icons the launcher uses, transcribed from the kit's `icons.svg` so the Android
 * build draws the SAME marks as the desktop apps. Stroke 2 on a 24-unit canvas, round caps/joins —
 * the kit's icon grammar. Colour comes from the caller (`tint`), never from a `stroke` attribute.
 */
object HouseIcons {
    val Apps = stroked("apps", "M4 4h6v6h-6z M14 4h6v6h-6z M4 14h6v6h-6z M14 14h6v6h-6z")
    val Download = stroked(
        "download",
        "M4 17v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2 -2v-2 M7 11l5 5l5 -5 M12 4l0 12",
    )
    val Info = stroked(
        "info",
        "M3 12a9 9 0 1 0 18 0a9 9 0 1 0 -18 0 M12 9h.01 M11 12h1v4h1",
    )
    val Search = stroked("search", "M10 10m-7 0a7 7 0 1 0 14 0a7 7 0 1 0 -14 0 M21 21l-6 -6")
    val ArrowLeft = stroked("arrow-left", "M5 12l14 0 M5 12l6 6 M5 12l6 -6")
    val Refresh = stroked(
        "refresh",
        "M20 11a8.1 8.1 0 0 0 -15.5 -2m-.5 -4v4h4 M4 13a8.1 8.1 0 0 0 15.5 2m.5 4v-4h-4",
    )
    val Trash = stroked(
        "trash",
        "M4 7l16 0 M10 11l0 6 M14 11l0 6 M5 7l1 12a2 2 0 0 0 2 2h8a2 2 0 0 0 2 -2l1 -12 " +
            "M9 7v-3a1 1 0 0 1 1 -1h4a1 1 0 0 1 1 1v3",
    )
    val External = stroked(
        "external-link",
        "M12 6h-6a2 2 0 0 0 -2 2v10a2 2 0 0 0 2 2h10a2 2 0 0 0 2 -2v-6 M11 13l9 -9 M15 4h5v5",
    )
    val Check = stroked("check", "M5 12l5 5l10 -10")
    val Close = stroked("close", "M18 6l-12 12 M6 6l12 12")
    val Share = stroked(
        "share",
        "M6 12m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0 M18 6m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0 " +
            "M18 18m-3 0a3 3 0 1 0 6 0a3 3 0 1 0 -6 0 M8.7 10.7l6.6 -3.4 M8.7 13.3l6.6 3.4",
    )

    /** The suite glyph (masks-theater), for the app bar's identity tile. */
    val Masks = stroked(
        "masks",
        "M13.192 9h6.616a2 2 0 0 1 1.992 2.183l-.567 6.182a4 4 0 0 1 -3.983 3.635h-1.5a4 4 0 0 1 " +
            "-3.983 -3.635l-.567 -6.182a2 2 0 0 1 1.992 -2.183 M15 13h.01 M18 13h.01 " +
            "M15 16.5c1 .667 2 .667 3 0 M8.632 15.982a4.037 4.037 0 0 1 -.382 .018h-1.5a4 4 0 0 1 " +
            "-3.983 -3.635l-.567 -6.182a2 2 0 0 1 1.992 -2.183h6.616a2 2 0 0 1 2 2 M6 8h.01 M9 8h.01 " +
            "M6 12c.764 -.51 1.528 -.63 2.291 -.36",
    )

    private fun stroked(name: String, pathData: String): ImageVector =
        ImageVector.Builder(
            name = name,
            defaultWidth = 24.dp,
            defaultHeight = 24.dp,
            viewportWidth = 24f,
            viewportHeight = 24f,
        ).run {
            addPath(
                pathData = PathParser().parsePathString(pathData).toNodes(),
                fill = null,
                stroke = SolidColor(Color.Black),
                strokeLineWidth = 2f,
                strokeLineCap = StrokeCap.Round,
                strokeLineJoin = StrokeJoin.Round,
            )
            build()
        }
}
