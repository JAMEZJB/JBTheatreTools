// JB Theatre Tools for Android — root build file.
//
// Pinned toolchain, matching the rest of the suite's Android builds:
//   Android Gradle plugin 8.9.2 — compileSdk 35 / build-tools 35.0.0, needs JDK 17.
//   Kotlin              2.0.21 — the Compose compiler ships with the Kotlin plugin from 2.0.
//   Gradle wrapper        8.13 — AGP 8.9 needs >= 8.11.1.
plugins {
    id("com.android.application") version "8.9.2" apply false
    id("org.jetbrains.kotlin.android") version "2.0.21" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.0.21" apply false
    id("org.jetbrains.kotlin.plugin.serialization") version "2.0.21" apply false
}
