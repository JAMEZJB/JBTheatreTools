package com.jamesbreedon.jbtheatretools.core

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.assertEquals
import org.junit.Test

/** The installed-vs-latest comparison, including Convert's rolling date tags. */
class VersionCompareTest {

    @Test fun tolerantOfLeadingV() {
        assertTrue(VersionCompare.equal("v2.1.1", "2.1.1"))
        assertEquals("2.1.1", VersionCompare.norm("v2.1.1"))
    }

    @Test fun ordinarySemver() {
        assertTrue(VersionCompare.isNewer("2.1.1", "2.1.0"))
        assertTrue(VersionCompare.isNewer("v1.27.0", "v1.26.0"))
        assertFalse(VersionCompare.isNewer("1.26.0", "1.27.0"))
        assertFalse(VersionCompare.isNewer("2.0.0", "2.0.0"))
    }

    @Test fun differingComponentCounts() {
        assertTrue(VersionCompare.isNewer("2.1", "2.0.9"))
        assertFalse(VersionCompare.isNewer("2.0", "2.0.0"))
        assertTrue(VersionCompare.isNewer("2.0.1", "2.0"))
    }

    @Test fun dateTagsCompareAsNumbersAndNeverWrap() {
        // Convert ships build-YYYYMMDD; 20260912 overflows a 32-bit int, which is how an update could
        // silently look OLDER than the installed build.
        assertTrue(VersionCompare.isNewer("build-20260912", "build-20260901"))
        assertFalse(VersionCompare.isNewer("build-20260901", "build-20260912"))
        assertTrue(VersionCompare.isNewer("build-20260912", "2.1.1"))
    }

    @Test fun preReleaseSuffixesDoNotBreakTheParse() {
        assertFalse(VersionCompare.isNewer("2.0.0-rc1", "2.0.0"))
        assertTrue(VersionCompare.isNewer("2.0.1-rc1", "2.0.0"))
    }

    @Test fun absurdlyLongNumbersSaturateInsteadOfWrapping() {
        assertFalse(VersionCompare.isNewer("1.0.0", "99999999999999999999.0.0"))
    }
}
