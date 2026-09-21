package com.jamesbreedon.jbtheatretools.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import com.jamesbreedon.jbtheatretools.core.Appearance

/**
 * The house theme. Material3 is the base — but every colour, radius and face comes from the kit
 * tokens, dynamic colour is never used, and the components the house style bans (FAB, drawer,
 * tonal/elevated buttons) simply don't appear.
 */
@Composable
fun HouseTheme(
    appearance: Appearance,
    content: @Composable () -> Unit,
) {
    val dark = when (appearance) {
        Appearance.LIGHT -> false
        Appearance.DARK -> true
        Appearance.SYSTEM -> isSystemInDarkTheme()
    }
    val colors = if (dark) DarkColors else LightColors

    // Material's scheme is mapped onto the house tokens so any stray Material surface still lands
    // on our palette. Nothing here is generated from a wallpaper.
    val scheme = if (dark) {
        darkColorScheme(
            primary = colors.accent, onPrimary = colors.onAccent,
            background = colors.ground, onBackground = colors.text,
            surface = colors.surface, onSurface = colors.text,
            surfaceVariant = colors.raised, onSurfaceVariant = colors.text2,
            outline = colors.line, error = colors.danger,
        )
    } else {
        lightColorScheme(
            primary = colors.accent, onPrimary = colors.onAccent,
            background = colors.ground, onBackground = colors.text,
            surface = colors.surface, onSurface = colors.text,
            surfaceVariant = colors.raised, onSurfaceVariant = colors.text2,
            outline = colors.line, error = colors.danger,
        )
    }

    val typography = Typography(
        displayLarge = TextStyle(
            fontFamily = HouseType.mono, fontWeight = FontWeight.Medium, fontSize = HouseType.heroSize
        ),
        titleMedium = TextStyle(
            fontFamily = HouseType.inter, fontWeight = FontWeight.SemiBold, fontSize = HouseType.titleSize
        ),
        bodyMedium = TextStyle(
            fontFamily = HouseType.inter, fontWeight = FontWeight.Normal, fontSize = HouseType.bodySize
        ),
        bodySmall = TextStyle(
            fontFamily = HouseType.inter, fontWeight = FontWeight.Normal, fontSize = HouseType.smallSize
        ),
        labelSmall = TextStyle(
            fontFamily = HouseType.inter, fontWeight = FontWeight.Medium, fontSize = HouseType.labelSize
        ),
    )

    CompositionLocalProvider(LocalHouseColors provides colors) {
        MaterialTheme(colorScheme = scheme, typography = typography, content = content)
    }
}
