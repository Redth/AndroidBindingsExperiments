plugins {
    kotlin("jvm") version "2.4.21"
    application
}

if (project != rootProject) {
    layout.buildDirectory.set(rootProject.layout.buildDirectory.dir("jvm-api-descriptor"))
}

dependencies {
    implementation("org.jetbrains.kotlin:kotlin-metadata-jvm:2.4.21")
    implementation("org.ow2.asm:asm-tree:9.8")
    implementation("com.google.code.gson:gson:2.13.2")
    testImplementation(kotlin("test-junit"))
    testImplementation("androidx.annotation:annotation-jvm:1.9.1")
}

application { mainClass.set("net.dot.android.descriptor.ApiDescriptorKt") }
java {
    sourceCompatibility = JavaVersion.VERSION_11
    targetCompatibility = JavaVersion.VERSION_11
}
tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().configureEach {
    compilerOptions.jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_11)
}
val fixtureJar = tasks.register<Jar>("descriptorFixtureJar") {
    dependsOn(tasks.testClasses)
    archiveClassifier.set("fixtures")
    from(sourceSets.test.get().output)
    include("net/dot/android/descriptor/fixtures/**")
}
tasks.test {
    useJUnit()
    dependsOn(fixtureJar)
    systemProperty("descriptorFixtures", fixtureJar.get().archiveFile.get().asFile.absolutePath)
    systemProperty("descriptorTestOutput", layout.buildDirectory.dir("descriptor-tests").get().asFile.absolutePath)
}
