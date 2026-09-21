package com.jamesbreedon.jbtheatretools.ui

import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.jamesbreedon.jbtheatretools.R

/**
 * House Style v2 tokens, transcribed from `kit/tokens.css`. Same neutrals, same radii, same type —
 * the Android launcher is the desktop launcher in Compose, not a Material app with our colours on it.
 *
 * The launcher's accent is the suite purple (`kit/android-icons/manifest.json`, slug `theatre`).
 * There is NO Material You dynamic colour, and no yellow anywhere.
 */
@Immutable
data class HouseColors(
    val ground: Color,
    val surface: Color,
    val raised: Color,
    val sunken: Color,
    val line: Color,
    val lineStrong: Color,
    val text: Color,
    val text2: Color,
    val text3: Color,
    val selector: Color,
    val ok: Color,
    val warn: Color,
    val danger: Color,
    val info: Color,
    val accent: Color,
    val onAccent: Color,
    val isDark: Boolean,
)

val LightColors = HouseColors(
    ground = Color(0xFFF3F4F6),
    surface = Color(0xFFFFFFFF),
    raised = Color(0xFFF8F9FB),
    sunken = Color(0xFFECEEF1),
    line = Color(0xFFDCDFE4),
    lineStrong = Color(0xFFC6CAD2),
    text = Color(0xFF16181C),
    text2 = Color(0xFF5F6670),
    text3 = Color(0xFF8B929C),
    selector = Color(0xFF6E8299),
    ok = Color(0xFF1F9D4C),
    warn = Color(0xFFC2610B),      // orange warn, never yellow
    danger = Color(0xFFD3312B),
    info = Color(0xFF1F6FD6),
    accent = Color(0xFFAF52DE),    // the launcher accent (suite purple)
    onAccent = Color(0xFFFFFFFF),
    isDark = false,
)

val DarkColors = HouseColors(
    ground = Color(0xFF141619),
    surface = Color(0xFF1B1E23),
    raised = Color(0xFF22262C),
    sunken = Color(0xFF101215),
    line = Color(0xFF2C3138),
    lineStrong = Color(0xFF3A4048),
    text = Color(0xFFE8EAEE),
    text2 = Color(0xFF9AA1AB),
    text3 = Color(0xFF6C737D),
    selector = Color(0xFF6E8299),
    ok = Color(0xFF3FB950),
    warn = Color(0xFFF0883E),
    danger = Color(0xFFF85149),
    info = Color(0xFF58A6FF),
    accent = Color(0xFFC77BF0),    // retuned dark accent
    onAccent = Color(0xFF141619),
    isDark = true,
)

/** Radii — `--r-ctl` / `--r-panel` / `--r-win`, plus the 22.4% icon-tile radius (§7). */
object Radii {
    val control: Dp = 6.dp
    val panel: Dp = 9.dp
    val window: Dp = 12.dp
    /** 22.4% of the tile edge — the suite's icon-tile corner. */
    fun tile(size: Dp): Dp = size * 0.224f
}

/** The six-step type scale (§2). 1 CSS px = 1 dp = 1 sp here; body text still honours font scale. */
object HouseType {
    val inter = FontFamily(
        Font(R.font.inter_regular, FontWeight.Normal),
        Font(R.font.inter_medium, FontWeight.Medium),
        Font(R.font.inter_semibold, FontWeight.SemiBold),
    )
    val mono = FontFamily(
        Font(R.font.jetbrains_mono_regular, FontWeight.Normal),
        Font(R.font.jetbrains_mono_medium, FontWeight.Medium),
    )

    val heroSize = 40.sp
    val statusSize = 15.sp
    val titleSize = 15.sp
    val bodySize = 13.sp
    val smallSize = 12.sp
    val labelSize = 10.5.sp
}

/** Gutters and the chrome heights from §3. */
object Metrics {
    val appBarHeight = 52.dp
    val statusRowHeight = 36.dp
    val bottomNavHeight = 80.dp
    val gutterCompact = 20.dp
    val gutterExpanded = 24.dp
    val touchTarget = 44.dp
    val touchTargetPreferred = 48.dp
    val rowHeight = 56.dp
    val gridTile = 52.dp
    val identityTile = 28.dp
    val sidebarWidth = 220.dp
    val navRailWidth = 80.dp
}

val LocalHouseColors = staticCompositionLocalOf { LightColors }

/** Shorthand: `House.colors.accent`. */
object House {
    val colors: HouseColors
        @androidx.compose.runtime.Composable
        @androidx.compose.runtime.ReadOnlyComposable
        get() = LocalHouseColors.current
}
