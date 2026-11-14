import org.gradle.api.attributes.java.TargetJvmEnvironment
import org.gradle.api.artifacts.component.ModuleComponentIdentifier
import java.util.zip.ZipFile

plugins { kotlin("jvm") }

val nativeClosure = configurations.create("nativeClosure") {
    isCanBeConsumed = false
    attributes {
        attribute(Usage.USAGE_ATTRIBUTE, objects.named(Usage.JAVA_RUNTIME))
        attribute(Category.CATEGORY_ATTRIBUTE, objects.named(Category.LIBRARY))
        attribute(TargetJvmEnvironment.TARGET_JVM_ENVIRONMENT_ATTRIBUTE, objects.named(TargetJvmEnvironment.ANDROID))
    }
}
dependencies {
    add("nativeClosure", "androidx.activity:activity:1.13.0")
    add("nativeClosure", "androidx.lifecycle:lifecycle-runtime-android:2.11.0")
    add("nativeClosure", "androidx.datastore:datastore-preferences:${providers.gradleProperty("dataStoreVersion").get()}")
    add("nativeClosure", "org.jetbrains.kotlin:kotlin-stdlib:2.4.0")
    add("nativeClosure", "org.jetbrains.kotlinx:kotlinx-coroutines-android:1.9.0")
}
dependencyLocking { lockAllConfigurations() }
val prepareNativeClasspath = tasks.register("prepareNativeClasspath") {
    inputs.files(nativeClosure)
    outputs.dir(layout.buildDirectory.dir("native-classpath"))
    doLast {
        val directory = layout.buildDirectory.dir("native-classpath").get().asFile
        directory.mkdirs()
        directory.listFiles()?.forEach { it.delete() }
        nativeClosure.files.sortedBy { it.absolutePath }.filter { it.extension == "aar" }.forEachIndexed { index, archive ->
            ZipFile(archive).use { zip ->
                zip.getEntry("classes.jar")?.let { entry ->
                    zip.getInputStream(entry).use { input ->
                        directory.resolve("$index.jar").outputStream().use { input.copyTo(it) }
                    }
                }
            }
        }
    }
}
val androidHome = providers.environmentVariable("ANDROID_HOME").get()
dependencies {
    compileOnly(files(nativeClosure.filter { it.extension == "jar" }))
    compileOnly(fileTree(layout.buildDirectory.dir("native-classpath")) { include("*.jar") })
    compileOnly(files("$androidHome/platforms/android-35/android.jar"))
}
tasks.withType<org.jetbrains.kotlin.gradle.tasks.KotlinCompile>().configureEach {
    dependsOn(prepareNativeClasspath)
    compilerOptions.jvmTarget.set(org.jetbrains.kotlin.gradle.dsl.JvmTarget.JVM_1_8)
}
java {
    sourceCompatibility = JavaVersion.VERSION_1_8
    targetCompatibility = JavaVersion.VERSION_1_8
}
evaluationDependsOn(":dotnet-kotlin-runtime")
val runtimeJar = project(":dotnet-kotlin-runtime").tasks.named<Jar>("jar")
val nativeInputs = tasks.register("nativeInputs") {
    dependsOn(tasks.jar, runtimeJar)
    inputs.files(nativeClosure, tasks.jar.flatMap { it.archiveFile }, runtimeJar.flatMap { it.archiveFile })
    outputs.files(layout.buildDirectory.file("outputs/binding-items.txt"),
        layout.buildDirectory.file("outputs/runtime-items.txt"), layout.buildDirectory.file("outputs/native-inputs.json"))
    doLast {
        val directory = layout.buildDirectory.dir("outputs").get().asFile
        directory.mkdirs()
        val artifacts = nativeClosure.incoming.artifacts.artifacts.map { artifact ->
            val id = artifact.id.componentIdentifier as ModuleComponentIdentifier
            mapOf("group" to id.group, "artifact" to id.module, "version" to id.version, "file" to artifact.file.absolutePath)
        }.sortedBy { "${it["group"]}:${it["artifact"]}" }
        directory.resolve("binding-items.txt").writeText(artifacts.joinToString("\n") { it["file"].toString() } + "\n")
        directory.resolve("runtime-items.txt").writeText(runtimeJar.get().archiveFile.get().asFile.absolutePath + "\n")
        directory.resolve("native-inputs.json").writeText(groovy.json.JsonOutput.prettyPrint(groovy.json.JsonOutput.toJson(artifacts)))
    }
}
evaluationDependsOn(":jvm-api-descriptor")
val descriptorProject = project(":jvm-api-descriptor")
val nativeDescriptor = tasks.register<JavaExec>("nativeDescriptor") {
    dependsOn(nativeInputs, descriptorProject.tasks.named("classes"))
    classpath = descriptorProject.extensions.getByType<SourceSetContainer>()["main"].runtimeClasspath
    mainClass.set("net.dot.android.descriptor.ApiDescriptorKt")
    maxHeapSize = "2g"
    inputs.files(nativeClosure, tasks.jar.flatMap { it.archiveFile }, runtimeJar.flatMap { it.archiveFile })
    outputs.file(layout.buildDirectory.file("outputs/api-descriptor.json"))
    args("--manifest", layout.buildDirectory.file("outputs/native-inputs.json").get().asFile.absolutePath,
        "--archive", tasks.jar.get().archiveFile.get().asFile.absolutePath,
        "--archive", runtimeJar.get().archiveFile.get().asFile.absolutePath,
        "--output", layout.buildDirectory.file("outputs/api-descriptor.json").get().asFile.absolutePath)
}
tasks.register<Zip>("assembleRelease") {
    dependsOn(tasks.jar, nativeDescriptor)
    archiveFileName.set("Reference.Native.aar")
    destinationDirectory.set(layout.buildDirectory.dir("outputs/aar"))
    from(tasks.jar.flatMap { it.archiveFile }) { rename { "classes.jar" } }
    from("AndroidManifest.xml")
}
