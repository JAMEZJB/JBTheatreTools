# The release build does not minify (see build.gradle.kts); these rules exist so that if it ever
# does, the serialization models and the Ed25519 provider survive.
-keepclassmembers class kotlinx.serialization.json.** { *; }
-keep,includedescriptorclasses class com.jamesbreedon.jbtheatretools.**$$serializer { *; }
-keepclassmembers class com.jamesbreedon.jbtheatretools.** { *** Companion; }
-keep class net.i2p.crypto.eddsa.** { *; }
