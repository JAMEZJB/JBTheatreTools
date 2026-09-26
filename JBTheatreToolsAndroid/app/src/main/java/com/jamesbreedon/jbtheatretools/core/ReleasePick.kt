package com.jamesbreedon.jbtheatretools.core

import com.jamesbreedon.jbtheatretools.net.ReleaseInfo

/**
 * Which release counts as an app's "latest" for this device. Development pre-releases (`-dev.N`) are only
 * ever candidates when the device has the Dev channel switched on; with it off they're invisible — even for
 * an app whose only releases are dev builds. Otherwise: the highest stable release, falling back to the
 * highest (non-dev) pre-release when an app has no stable release yet.
 */
object ReleasePick {
    fun latest(releases: List<ReleaseInfo>, devChannel: Boolean): ReleaseInfo? {
        val nonDev = releases.filter { !VersionCompare.isDev(it.tagName) }
        val stable = nonDev.filter { !it.prerelease }
        val pool = when {
            devChannel -> (stable + releases.filter { VersionCompare.isDev(it.tagName) }).ifEmpty { nonDev }
            else -> stable.ifEmpty { nonDev }
        }
        return pool.maxWithOrNull { x, y -> VersionCompare.compare(x.tagName, y.tagName) }
    }

    /**
     * The release a setup file's held [version] names: matched by version equality ("1.2.0" finds the tag "v1.2.0"),
     * so the caller installs from the release's REAL tag. A development build is never a match unless [allowDev].
     */
    fun byVersion(releases: List<ReleaseInfo>, version: String, allowDev: Boolean): ReleaseInfo? =
        releases.firstOrNull { (allowDev || !VersionCompare.isDev(it.tagName)) && VersionCompare.equal(it.tagName, version) }
}
