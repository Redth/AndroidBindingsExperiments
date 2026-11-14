plugins { kotlin("jvm") apply false }

evaluationDependsOn(":dotnet-kotlin-runtime")
val runtimeProject = project(":dotnet-kotlin-runtime")
val runtimeJar = runtimeProject.tasks.named<Jar>("jar")
tasks.register<Sync>("prepareRuntime") {
    dependsOn(runtimeJar)
    from(runtimeJar.flatMap { it.archiveFile })
    from(runtimeProject.configurations.named("runtimeClasspath"))
    into(layout.buildDirectory.dir("package-runtime"))
}
