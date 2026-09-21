package com.jamesbreedon.jbtheatretools.ui

import android.content.Context
import android.graphics.BitmapFactory
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.Image

// ── type helpers ────────────────────────────────────────────────────────────

@Composable
fun TitleText(text: String, modifier: Modifier = Modifier, color: Color = House.colors.text, maxLines: Int = 1) =
    Text(
        text, modifier = modifier, color = color, maxLines = maxLines, overflow = TextOverflow.Ellipsis,
        style = TextStyle(fontFamily = HouseType.inter, fontWeight = FontWeight.SemiBold, fontSize = HouseType.titleSize),
    )

@Composable
fun BodyText(text: String, modifier: Modifier = Modifier, color: Color = House.colors.text2, maxLines: Int = 2) =
    Text(
        text, modifier = modifier, color = color, maxLines = maxLines, overflow = TextOverflow.Ellipsis,
        style = TextStyle(fontFamily = HouseType.inter, fontWeight = FontWeight.Normal, fontSize = HouseType.bodySize),
    )

@Composable
fun SmallText(
    text: String,
    modifier: Modifier = Modifier,
    color: Color = House.colors.text2,
    weight: FontWeight = FontWeight.Medium,
    align: TextAlign = TextAlign.Start,
    maxLines: Int = 1,
) = Text(
    text, modifier = modifier, color = color, maxLines = maxLines, overflow = TextOverflow.Ellipsis,
    textAlign = align,
    style = TextStyle(fontFamily = HouseType.inter, fontWeight = weight, fontSize = HouseType.smallSize),
)

@Composable
fun MonoText(
    text: String,
    modifier: Modifier = Modifier,
    color: Color = House.colors.text2,
    size: androidx.compose.ui.unit.TextUnit = HouseType.smallSize,
) = Text(
    text, modifier = modifier, color = color, maxLines = 1, overflow = TextOverflow.Ellipsis,
    style = TextStyle(fontFamily = HouseType.mono, fontWeight = FontWeight.Medium, fontSize = size),
)

@Composable
fun LabelText(text: String, modifier: Modifier = Modifier, color: Color = House.colors.text3) = Text(
    text.uppercase(), modifier = modifier, color = color, maxLines = 1,
    style = TextStyle(
        fontFamily = HouseType.inter, fontWeight = FontWeight.SemiBold,
        fontSize = HouseType.labelSize, letterSpacing = 0.8.sp,
    ),
)

// ── buttons (rule 24: primary / secondary / ghost / danger, 48 high) ────────

@Composable
fun PrimaryButton(text: String, modifier: Modifier = Modifier, enabled: Boolean = true, onClick: () -> Unit) {
    val c = House.colors
    Box(
        modifier
            .height(48.dp)
            .clip(RoundedCornerShape(Radii.control))
            .background(if (enabled) c.accent else c.sunken)
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        SmallText(
            text, color = if (enabled) c.onAccent else c.text3,
            weight = FontWeight.SemiBold,
        )
    }
}

@Composable
fun SecondaryButton(text: String, modifier: Modifier = Modifier, enabled: Boolean = true, onClick: () -> Unit) {
    val c = House.colors
    Box(
        modifier
            .height(48.dp)
            .clip(RoundedCornerShape(Radii.control))
            .background(c.raised)
            .border(1.dp, c.lineStrong, RoundedCornerShape(Radii.control))
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        SmallText(text, color = if (enabled) c.text else c.text3, weight = FontWeight.SemiBold)
    }
}

@Composable
fun DangerButton(text: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    val c = House.colors
    Box(
        modifier
            .height(48.dp)
            .clip(RoundedCornerShape(Radii.control))
            .background(c.danger)
            .clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        SmallText(text, color = Color.White, weight = FontWeight.SemiBold)
    }
}

@Composable
fun GhostButton(text: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    Box(
        modifier.height(48.dp).clip(RoundedCornerShape(Radii.control)).clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        SmallText(text, color = House.colors.text2, weight = FontWeight.SemiBold)
    }
}

@Composable
fun Glyph(icon: ImageVector, size: Dp = 22.dp, tint: Color = House.colors.text2, description: String? = null) {
    Icon(
        imageVector = icon,
        contentDescription = description,
        tint = tint,
        modifier = Modifier.size(size),
    )
}

/** An icon-only control — allowed only for the app bar's gear / back / search (§3), so it carries a label. */
@Composable
fun IconAction(icon: ImageVector, description: String, tint: Color = House.colors.text2, onClick: () -> Unit) {
    Box(
        Modifier
            .size(Metrics.touchTarget)
            .clip(RoundedCornerShape(Radii.control))
            .clickable(onClick = onClick)
            .semantics { contentDescription = description },
        contentAlignment = Alignment.Center,
    ) {
        Glyph(icon, 22.dp, tint)
    }
}

// ── the suite identity tile + per-app tiles ─────────────────────────────────

/** The app bar's identity tile (§7): the suite glyph on the accent, 22.4% radius. */
@Composable
fun IdentityTile(size: Dp = Metrics.identityTile) {
    val c = House.colors
    Box(
        Modifier.size(size).clip(RoundedCornerShape(Radii.tile(size))).background(c.accent),
        contentAlignment = Alignment.Center,
    ) {
        Glyph(HouseIcons.Masks, size * 0.62f, c.onAccent, null)
    }
}

/** Loads `assets/icons/<id>.png` once per id; falls back to an initials tile when an app has no art. */
@Composable
fun AppIconTile(catalogId: String, appName: String, size: Dp) {
    val context = LocalContext.current
    val bitmap = remember(catalogId) { loadIcon(context, catalogId) }
    val c = House.colors
    val shape = RoundedCornerShape(Radii.tile(size))
    if (bitmap != null) {
        Image(
            bitmap = bitmap,
            contentDescription = null,
            contentScale = ContentScale.Fit,
            modifier = Modifier.size(size).clip(shape),
        )
    } else {
        Box(
            Modifier.size(size).clip(shape).background(c.raised).border(1.dp, c.line, shape),
            contentAlignment = Alignment.Center,
        ) {
            SmallText(initials(appName), color = c.text2, weight = FontWeight.SemiBold)
        }
    }
}

private val iconCache = HashMap<String, ImageBitmap?>()

private fun loadIcon(context: Context, catalogId: String): ImageBitmap? =
    iconCache.getOrPut(catalogId) {
        runCatching {
            context.assets.open("icons/$catalogId.png").use {
                BitmapFactory.decodeStream(it)?.asImageBitmap()
            }
        }.getOrNull()
    }

private fun initials(name: String): String =
    name.split(' ').filter { it.isNotBlank() }.take(2).map { it.first().uppercaseChar() }.joinToString("")

// ── panels, rows, progress ──────────────────────────────────────────────────

@Composable
fun Panel(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    val c = House.colors
    Box(
        modifier
            .clip(RoundedCornerShape(Radii.panel))
            .background(c.surface)
            .border(1.dp, c.line, RoundedCornerShape(Radii.panel))
            .padding(horizontal = 16.dp, vertical = 14.dp),
    ) { content() }
}

@Composable
fun HouseProgress(fraction: Float, modifier: Modifier = Modifier) {
    val c = House.colors
    LinearProgressIndicator(
        progress = { fraction.coerceIn(0f, 1f) },
        modifier = modifier.height(4.dp).clip(RoundedCornerShape(2.dp)),
        color = c.accent,
        trackColor = c.sunken,
        strokeCap = androidx.compose.ui.graphics.StrokeCap.Round,
        gapSize = 0.dp,
        drawStopIndicator = {},
    )
}

/** A status dot — the kit's `.dot`. */
@Composable
fun Dot(color: Color, size: Dp = 7.dp) {
    Box(Modifier.size(size).clip(RoundedCornerShape(size / 2)).background(color))
}

@Composable
fun HouseTextField(
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    modifier: Modifier = Modifier,
    mask: Boolean = false,
    singleLine: Boolean = true,
) {
    val c = House.colors
    Box(
        modifier
            .height(Metrics.touchTargetPreferred)
            .clip(RoundedCornerShape(Radii.control))
            .background(c.surface)
            .border(1.dp, c.lineStrong, RoundedCornerShape(Radii.control))
            .padding(horizontal = 12.dp),
        contentAlignment = Alignment.CenterStart,
    ) {
        if (value.isEmpty()) SmallText(placeholder, color = c.text3, weight = FontWeight.Normal)
        BasicTextField(
            value = value,
            onValueChange = onValueChange,
            singleLine = singleLine,
            textStyle = TextStyle(
                fontFamily = if (mask) HouseType.mono else HouseType.inter,
                fontSize = HouseType.bodySize,
                color = c.text,
            ),
            cursorBrush = SolidColor(c.accent),
            visualTransformation = if (mask) {
                androidx.compose.ui.text.input.PasswordVisualTransformation()
            } else {
                androidx.compose.ui.text.input.VisualTransformation.None
            },
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

/** A segmented control — the kit's `.seg` (used for Appearance). */
@Composable
fun Segmented(options: List<String>, selectedIndex: Int, onSelect: (Int) -> Unit, modifier: Modifier = Modifier) {
    val c = House.colors
    Row(
        modifier
            .height(Metrics.touchTarget)
            .clip(RoundedCornerShape(Radii.control))
            .background(c.sunken)
            .padding(3.dp),
        horizontalArrangement = Arrangement.spacedBy(3.dp),
    ) {
        options.forEachIndexed { i, label ->
            val active = i == selectedIndex
            Box(
                Modifier
                    .weight(1f)
                    .fillMaxSize()
                    .clip(RoundedCornerShape(Radii.control - 2.dp))
                    .background(if (active) c.surface else Color.Transparent)
                    .clickable { onSelect(i) },
                contentAlignment = Alignment.Center,
            ) {
                SmallText(
                    label,
                    color = if (active) c.text else c.text2,
                    weight = if (active) FontWeight.SemiBold else FontWeight.Medium,
                )
            }
        }
    }
}

@Composable
fun Divider(modifier: Modifier = Modifier) {
    Box(modifier.fillMaxWidth().height(1.dp).background(House.colors.line))
}

@Composable
fun VSpace(height: Dp) = Spacer(Modifier.height(height))

@Composable
fun HSpace(width: Dp) = Spacer(Modifier.width(width))

@Composable
fun SectionHeader(text: String, modifier: Modifier = Modifier) {
    Column(modifier.fillMaxWidth().padding(top = 16.dp, bottom = 8.dp)) {
        LabelText(text)
    }
}
