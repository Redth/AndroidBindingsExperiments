pluginManagement {
    apply(from = "$rootDir/../../../../eng/gradle/plugin-repositories.gradle", to = this)
    if (System.getenv("DEPENDABOT_JOB_ID") != null) {
        repositories { google() }
    }
}
dependencyResolutionManagement {
    apply(from = "$rootDir/../../../../eng/gradle/dependency-repositories.gradle", to = this)
    if (System.getenv("DEPENDABOT_JOB_ID") != null) {
        repositories { google() }
    }
}
rootProject.name = "jvm-api-descriptor"
