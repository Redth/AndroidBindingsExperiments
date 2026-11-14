plugins {
    kotlin("jvm")
}

if (project != rootProject) {
    layout.buildDirectory.set(rootProject.layout.buildDirectory.dir("dotnet-kotlin-runtime"))
}

dependencies {
    implementation("org.jetbrains.kotlin:kotlin-stdlib:2.0.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.9.0")
}

java {
    sourceCompatibility = JavaVersion.VERSION_1_8
    targetCompatibility = JavaVersion.VERSION_1_8
}

tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().configureEach {
    compilerOptions.jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_1_8)
}

val bindingReferences = tasks.register<Sync>("bindingReferences") {
    from(configurations.compileClasspath)
    into(layout.buildDirectory.dir("outputs/references"))
}

tasks.register<Zip>("assembleRelease") {
    dependsOn(tasks.jar, bindingReferences)
    archiveFileName.set("Microsoft.Android.Kotlin.aar")
    destinationDirectory.set(layout.buildDirectory.dir("outputs/aar"))
    from(tasks.jar.flatMap { it.archiveFile }) { rename { "classes.jar" } }
    from("AndroidManifest.xml")
}
