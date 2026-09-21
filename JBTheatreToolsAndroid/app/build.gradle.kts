// JB Theatre Tools for Android — the one module (the suite launcher / installer).
import java.util.Properties

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("org.jetbrains.kotlin.plugin.serialization")
}

// ── the single source of the version + the shared repo assets ────────────────
val launcherProps = Properties().apply {
    rootProject.file("launcher.properties").inputStream().use { load(it) }
}
fun prop(name: String): String =
    launcherProps.getProperty(name) ?: error("launcher.properties is missing '$name'")

val appVersionName = prop("versionName")
val appVersionCode = prop("versionCode").toInt()

// The catalog and the row icons live at the REPO ROOT and are shared with the macOS and
// Windows launchers. They are copied into the APK's assets at build time — never forked,
// never a second copy in source control.
val repoRoot = rootProject.file("..")
val catalogFile = File(repoRoot, "catalog.json")
val iconsDir = File(repoRoot, "icons")

val generatedAssets = layout.buildDirectory.dir("generated/launcher/assets")

val syncCatalog by tasks.registering(Sync::class) {
    description = "Copy the repo-root catalog.json + icons/ into the APK's assets."
    group = "launcher"
    from(catalogFile)
    from(iconsDir) { into("icons") }
    into(generatedAssets)
    doFirst {
        require(catalogFile.isFile) { "catalog.json not found at ${catalogFile.absolutePath}" }
        require(iconsDir.isDirectory) { "icons/ not found at ${iconsDir.absolutePath}" }
    }
}

android {
    namespace = "com.jamesbreedon.jbtheatretools"
    compileSdk = 35
    buildToolsVersion = "35.0.0"

    defaultConfig {
        applicationId = "com.jamesbreedon.jbtheatretools"
        minSdk = 29                 // Android 10 — house style §1 / §10
        targetSdk = 35
        versionCode = appVersionCode
        versionName = appVersionName

        ndk { abiFilters += listOf("arm64-v8a") }   // §1: arm64 only

        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    signingConfigs {
        // Release signing is CI-ONLY, from the suite keystore in repo secrets. No key
        // material exists in this repo; a local `assembleRelease` without the env vars
        // produces an UNSIGNED apk on purpose.
        create("suite") {
            val ksPath = System.getenv("SUITE_KEYSTORE_PATH")
            if (!ksPath.isNullOrBlank() && File(ksPath).isFile) {
                storeFile = File(ksPath)
                storePassword = System.getenv("SUITE_KEYSTORE_PASS")
                keyAlias = System.getenv("SUITE_KEY_ALIAS")
                keyPassword = System.getenv("SUITE_KEYSTORE_PASS")   // key pw == store pw
            }
        }
    }

    buildTypes {
        debug {
            isMinifyEnabled = false
            // Debug-only source override for the release feed. Empty in every release build;
            // a debug build can point the launcher at a local HTTP server to exercise the
            // verify + install path without the passphrase. See README "Debug feed override".
            buildConfigField("boolean", "ALLOW_DEBUG_FEED", "true")
        }
        release {
            isMinifyEnabled = false
            isShrinkResources = false
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
            buildConfigField("boolean", "ALLOW_DEBUG_FEED", "false")
            if (System.getenv("SUITE_KEYSTORE_PATH") != null) {
                signingConfig = signingConfigs.getByName("suite")
            }
        }
    }

    sourceSets {
        getByName("main") { assets.srcDir(generatedAssets) }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    packaging {
        resources.excludes += setOf("META-INF/*.kotlin_module", "META-INF/LICENSE*", "META-INF/NOTICE*")
    }
}

dependencies {
    val composeBom = platform("androidx.compose:compose-bom:2024.10.01")
    implementation(composeBom)
    androidTestImplementation(composeBom)

    implementation("androidx.core:core-ktx:1.15.0")
    implementation("androidx.core:core-splashscreen:1.0.1")
    implementation("androidx.activity:activity-compose:1.9.3")
    implementation("androidx.lifecycle:lifecycle-runtime-ktx:2.8.7")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.8.7")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.ui:ui-tooling-preview")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material3:material3-window-size-class")
    debugImplementation("androidx.compose.ui:ui-tooling")

    implementation("androidx.security:security-crypto:1.1.0-alpha06")
    implementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
    // Ed25519 for minisign verification. java.security's "Ed25519" needs API 33; minSdk is 29,
    // so the verifier uses this small public-domain implementation on every API level.
    implementation("net.i2p.crypto:eddsa:0.3.0")

    testImplementation("junit:junit:4.13.2")
    testImplementation("org.jetbrains.kotlinx:kotlinx-serialization-json:1.7.3")
}

// The unit tests parse the REAL repo-root catalog.json — handed to them by path so there is
// still only one copy of the file.
tasks.withType<Test>().configureEach {
    systemProperty("jbtt.catalog", catalogFile.absolutePath)
    systemProperty("jbtt.icons", iconsDir.absolutePath)
}

tasks.withType<com.android.build.gradle.tasks.MergeSourceSetFolders>().configureEach {
    dependsOn(syncCatalog)
}
tasks.matching { it.name.contains("Lint", ignoreCase = true) && it.name != "syncCatalog" }
    .configureEach { dependsOn(syncCatalog) }
