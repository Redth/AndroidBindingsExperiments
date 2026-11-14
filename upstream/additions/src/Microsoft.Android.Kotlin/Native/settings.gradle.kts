// See: eng/gradle/plugin-repositories.gradle, eng/gradle/dependency-repositories.gradle
pluginManagement {
    plugins {
        kotlin("jvm") version "2.4.21"
    }
    val config = providers.gradleProperty("dotnetAndroidGradleConfig").getOrElse("$rootDir/gradle")
    apply(from = "$config/plugin-repositories.gradle", to = this)
    if (System.getenv("DEPENDABOT_JOB_ID") != null) {
        repositories { google() }
    }
}
dependencyResolutionManagement {
    val config = providers.gradleProperty("dotnetAndroidGradleConfig").getOrElse("$rootDir/gradle")
    apply(from = "$config/dependency-repositories.gradle", to = this)
    if (System.getenv("DEPENDABOT_JOB_ID") != null) {
        repositories { google() }
    }
}
rootProject.name = "dotnet-android-kotlin"
