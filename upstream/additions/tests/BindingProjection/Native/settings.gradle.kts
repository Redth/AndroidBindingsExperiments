// See: eng/gradle/plugin-repositories.gradle, eng/gradle/dependency-repositories.gradle
pluginManagement {
    apply(from = "$rootDir/../../../eng/gradle/plugin-repositories.gradle", to = this)
    plugins { kotlin("jvm") version "2.4.21" }
    if (System.getenv("DEPENDABOT_JOB_ID") != null) {
        repositories { google() }
    }
}
dependencyResolutionManagement {
    apply(from = "$rootDir/../../../eng/gradle/dependency-repositories.gradle", to = this)
    if (System.getenv("DEPENDABOT_JOB_ID") != null) {
        repositories { google() }
    }
}
rootProject.name = "binding-reference"
include(":dotnet-kotlin-runtime")
project(":dotnet-kotlin-runtime").projectDir = file("../../../src/Microsoft.Android.Kotlin/Native")
include(":jvm-api-descriptor")
project(":jvm-api-descriptor").projectDir = file("../../../external/Java.Interop/tools/jvm-api-descriptor")
