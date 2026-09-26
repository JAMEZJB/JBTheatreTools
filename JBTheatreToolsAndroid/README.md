# JB Theatre Tools for Android

The Android build of the JB Theatre Tools launcher — the same suite launcher as the macOS and Windows
apps in this repository, written in Kotlin and Jetpack Compose.

On Android the launcher is also **the installer**. The tools are not on Google Play: you enter the
suite passphrase once, and the launcher reads the tools' releases, verifies each download, and installs
it through Android's own `PackageInstaller`. Updates, "Update all" and "Remove" work the same way.

| | |
|---|---|
| Package | `com.jamesbreedon.jbtheatretools` |
| Min / target | Android 10 (API 29) / API 35 |
| ABI | `arm64-v8a` only |
| Toolchain | Gradle 8.13, Android Gradle plugin 8.9.2, Kotlin 2.0.21, JDK 17 |
| Asset | `JBTheatreTools-v<version>-android-arm64.apk` + a `.sha256` sidecar |

The version comes from [`launcher.properties`](launcher.properties) and is the same number as the macOS
`Info.plist` and the Windows `.csproj` — one version across all three platforms, from one tag.

## What it shows

- **Apps** — a three-column grid of the suite's app tiles, each with its version. Long-press (or tap) a
  tile for Install / Update / Open / Remove / Open release notes.
- **Updates** — what has an update, with per-app progress, and a pinned **Update all**.
- **About** — Appearance (Light / Dark / System), the download settings, show lock, automatic checks and
  update notifications, setup export / import, storage (with Clear download cache), recent activity,
  Share diagnostics, the log, and the credit line.

The Apps tab has a search field and an All / Installed / Updates / Not installed filter. A tile's actions
also offer its release notes (newest first, with dates), its details (version, install date, size,
latest release) and **Hold at this version**; a download in progress can be cancelled, and Update all
has a Stop. Up to four installed apps (most recently opened first) become shortcuts on the launcher's
icon, and after the launcher updates itself it offers its own release notes once. With update notifications on,
checks also run while the launcher is closed — a periodic JobScheduler job that needs a network connection and only
ever notifies (it never installs). The rules behind these
(filtering, notes cleanup, schedules, the setup file, the history, redaction) match the desktop
launchers', with the same unit-test cases on all three.

At tablet width the grid becomes the desktop launcher's grouped list, with the same 220 dp sidebar,
the same category sections and the same rows. Phone portrait, phone landscape, tablet portrait, tablet
landscape and a split-screen half are all supported; rotating or resizing never loses state.

## How a download is trusted

Every install goes through the same chain, and **any failure refuses the install and deletes the file**
— there is no "install anyway":

1. the release's `SHA256SUMS` must carry a valid **minisign signature** from the suite's signing key
   (pinned in the app), and the signature's trusted comment must name *that* release, so a manifest
   from another release cannot be replayed;
2. the APK's **SHA-256** must equal the line for that asset in the manifest;
3. the APK's **signing certificate** must be the suite certificate — its SHA-256 is pinned in
   [`core/SigningCert.kt`](app/src/main/java/com/jamesbreedon/jbtheatretools/core/SigningCert.kt),
   fail-closed. Changing the signing key means a launcher release that trusts both fingerprints for
   one cycle;
4. the APK's declared applicationId must be the one the catalog expects.

Only then is a `PackageInstaller` session committed. On Android 14 and later the launcher claims
**update ownership** of what it installs, so nothing else can hijack a later update; on Android 10–13
the system shows its own install confirmation once per install. Nothing is ever installed by handing a
file to another app.

The passphrase (or a fine-grained access token, if you prefer to read the releases directly) is kept in
the **Android Keystore** via `EncryptedSharedPreferences`. It is never written to settings, never
included in a backup, and never written to the log — the log names the asset, the release and the
verification result, and a redaction filter strips anything credential-shaped before a line is written.

## The catalog and the icons are shared, not copied

`catalog.json` and `icons/*.png` live at the **root of this repository** and are read by all three
launchers. The Gradle build copies them into the APK's assets (`syncCatalog`); there is deliberately no
second copy to keep in step.

## applicationId mapping

The launcher finds an installed tool by its applicationId, `com.jamesbreedon.<slug>`. The mapping from
catalog id to slug lives in ONE place —
[`core/PackageIds.kt`](app/src/main/java/com/jamesbreedon/jbtheatretools/core/PackageIds.kt) — and the
same list appears in `AndroidManifest.xml`'s `<queries>` block, which is what makes the lookup legal on
Android 11 and later.

**Each tool's own Android build must use exactly this applicationId** (its `app.properties` `appId=`).
If a tool ships under a different one, the launcher will simply never see it as installed. A unit test
asserts that every app in `catalog.json` has a mapping and that every mapping is in the manifest.

| catalog id | applicationId |
|---|---|
| helocontrol | `com.jamesbreedon.helo` |
| dmxtools | `com.jamesbreedon.dmx` |
| psntools | `com.jamesbreedon.psn` |
| timecodetools | `com.jamesbreedon.timecode` |
| shownetscanner | `com.jamesbreedon.shownet` |
| nditools | `com.jamesbreedon.ndi` |
| showcontroltools | `com.jamesbreedon.showcontrol` |
| projectorcontrol | `com.jamesbreedon.projector` |
| powercalc | `com.jamesbreedon.power` |
| networkportmap | `com.jamesbreedon.portmap` |
| showdashboard | `com.jamesbreedon.dashboard` |
| showhandbook | `com.jamesbreedon.handbook` |
| deskconvert | `com.jamesbreedon.desk` |
| ciscobrotherlabels | `com.jamesbreedon.labels` |
| machineinventory | `com.jamesbreedon.inventory` |
| renametools | `com.jamesbreedon.rename` |
| surtitletools | `com.jamesbreedon.surtitle` |
| imagetools | `com.jamesbreedon.image` |
| ciscoswitchtools | `com.jamesbreedon.cisco` |
| pdftools | `com.jamesbreedon.pdf` |
| reporadar | `com.jamesbreedon.radar` |
| convert | `com.jamesbreedon.convert` |

Each tool's Android asset is named from its macOS asset: `HeloControl-macOS.zip` gives the prefix
`HeloControl`, so the APK is `HeloControl-v2.1.1-android-arm64.apk`. A catalog entry may also carry an
explicit `android-arm64` asset name, which always wins.

## Build

```sh
export JAVA_HOME=<a JDK 17>          # Android Studio's bundled JBR works
export ANDROID_HOME=<your Android SDK>
./gradlew :app:assembleDebug         # app/build/outputs/apk/debug/app-debug.apk
./gradlew :app:testDebugUnitTest     # the unit tests
```

Release builds are signed in CI only: `assembleRelease` reads the keystore from `SUITE_KEYSTORE_PATH`,
`SUITE_KEYSTORE_PASS` and `SUITE_KEY_ALIAS` in the environment. No key material is in this repository,
and a local release build without those variables deliberately produces an unsigned APK. The CI job is
[`ci/android.yml`](ci/android.yml).

### Tests

`app/src/test/` covers the parts that have to be right: the real `catalog.json` (apps, variants,
categories, the self entry), passphrase normalisation (the same cases the macOS and Windows tests
assert), `SHA256SUMS` parsing, BLAKE2b, minisign verification — against a real signed release manifest
*and* a throwaway keypair, including tampered manifests, a replayed manifest and a wrong key — the
certificate pin, asset-name derivation, installed-vs-latest comparison (including date-style versions)
and the release-feed override policy.

## Debug feed override (debug builds only)

To exercise the download → verify → install path without a passphrase, a **debug** build can be pointed
at a local release feed that speaks the same REST shapes. This exists only when
`BuildConfig.ALLOW_DEBUG_FEED` is true, which is never the case in a release build, and it is the only
way anything other than the pinned suite key is ever trusted.

Two ways in:

- **In the app** — on the first screen choose "Local feed", then enter the base URL and, if the feed's
  checksum manifest is signed with a throwaway key, that key's minisign public key.
- **By intent**, which is how the emulator run does it:

  ```sh
  adb shell am start -n com.jamesbreedon.jbtheatretools/.MainActivity \
      --es jbtt_debug_feed "http://10.0.2.2:8787/ghapi" \
      --es jbtt_debug_feed_key "<minisign public key line>" \
      --es jbtt_debug_appearance light        # light | dark | system, for screenshots
  ```

A debug build also permits cleartext HTTP to `10.0.2.2` / `localhost` and nowhere else; release builds
are HTTPS-only.

## Screenshots

`docs/screenshots/` holds the launcher at the four review sizes, light and dark: 390×844 (compact),
800×1280 (medium), 1280×800 (expanded) and 640×800 (a split-screen half).

## Credits

Inter and JetBrains Mono are bundled under the SIL Open Font License; the licences are in
`docs/licences/`. Icons are from the Tabler icon set (MIT). Convert is a repackage of the open-source
`p2r3/convert` under the GPL-2.0, and the launcher links to its source.
