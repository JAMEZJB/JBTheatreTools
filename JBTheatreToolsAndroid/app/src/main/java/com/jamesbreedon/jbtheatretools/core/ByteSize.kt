package com.jamesbreedon.jbtheatretools.core

import java.math.BigDecimal
import java.math.RoundingMode

/**
 * Human byte counts for download sizes, storage use and the disk-space check — the same wording on macOS,
 * Windows and Android. Decimal units (1 KB = 1000 bytes); one decimal under 10 ("4.2 MB"), whole numbers from
 * 10 up ("450 MB"). A value that rounds up to 1000 moves up a unit ("1.0 MB", never "1000 KB").
 */
object ByteSize {
    private val units = listOf("KB", "MB", "GB", "TB")

    fun format(bytes: Long): String {
        val b = if (bytes < 0) 0L else bytes
        if (b < 1000) return if (b == 1L) "1 byte" else "$b bytes"
        var v = b / 1000.0
        var u = 0
        while (true) {
            if (v < 10) {
                val r = round(v, 1)
                if (r < 10) return "${BigDecimal(r.toString()).setScale(1, RoundingMode.HALF_UP).toPlainString()} ${units[u]}"
            }
            val n = round(v, 0)
            if (n < 1000 || u == units.size - 1) return "${n.toLong()} ${units[u]}"
            v /= 1000
            u++
        }
    }

    /** Half away from zero (the .NET MidpointRounding.AwayFromZero the Windows build uses), on the decimal value. */
    private fun round(v: Double, places: Int): Double =
        BigDecimal(v.toString()).setScale(places, RoundingMode.HALF_UP).toDouble()

    /** Sum that never overflows (saturates) and ignores negative "unknown" sizes. */
    fun sum(sizes: Iterable<Long>): Long {
        var total = 0L
        for (s in sizes) {
            if (s <= 0) continue
            total = if (s > Long.MAX_VALUE - total) Long.MAX_VALUE else total + s
        }
        return total
    }
}
