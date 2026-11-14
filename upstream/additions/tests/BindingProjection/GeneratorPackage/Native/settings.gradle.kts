// See: eng/gradle/plugin-repositories.gradle, eng/gradle/dependency-repositories.gradle
pluginManagement {
    val config = providers.gradleProperty("dotnetAndroidGradleConfig").get()
    apply(from = "$config/plugin-repositories.gradle", to = this)
    plugins { kotlin("jvm") version "2.4.21" }
    if (System.getenv("DEPENDABOT_JOB_ID") != null) {
        repositories { google() }
    }
}
dependencyResolutionManagement {
    val config = providers.gradleProperty("dotnetAndroidGradleConfig").get()
    apply(from = "$config/dependency-repositories.gradle", to = this)
    if (System.getenv("DEPENDABOT_JOB_ID") != null) {
        repositories { google() }
    }
}
rootProject.name = "binding-package-native"
include(":dotnet-kotlin-runtime")
project(":dotnet-kotlin-runtime").projectDir =
    file(providers.gradleProperty("dotnetKotlinRuntimeSource").get())
