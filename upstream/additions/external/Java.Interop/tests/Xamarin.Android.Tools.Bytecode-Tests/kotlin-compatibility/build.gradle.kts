plugins { base }

val version = providers.gradleProperty("kotlinCompatibilityVersion").get()
val destination = providers.gradleProperty("kotlinCompatibilityOutput").get()
val compiler = configurations.create("compiler")
val libraries = configurations.create("libraries")
dependencies {
    compiler("org.jetbrains.kotlin:kotlin-compiler-embeddable:$version")
    libraries("org.jetbrains.kotlin:kotlin-stdlib:$version")
    libraries("org.jetbrains.kotlinx:kotlinx-coroutines-core-jvm:1.9.0")
}
val fixture = file("../kotlin-gradle/src/main/kotlin/AsyncFixture.kt")
val baseline = file("../kotlin-gradle/src/main/kotlin/net/dot/android/kotlinasync/CoroutineBridge.kt")
tasks.register<JavaExec>("compileCompatibility") {
    classpath = compiler
    mainClass.set("org.jetbrains.kotlin.cli.jvm.K2JVMCompiler")
    jvmArgs("-Xmx1g")
    inputs.files(fixture, baseline)
    inputs.property("compilerVersion", version)
    outputs.dir(destination)
    doFirst {
        args("-no-stdlib", "-no-reflect", "-jvm-target", "11",
             "-classpath", libraries.asPath, "-d", destination, fixture.absolutePath, baseline.absolutePath)
    }
}
