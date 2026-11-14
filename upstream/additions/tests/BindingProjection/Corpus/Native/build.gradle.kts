import groovy.json.JsonOutput
import groovy.json.JsonSlurper
import org.gradle.api.artifacts.component.ModuleComponentIdentifier
import org.gradle.api.artifacts.component.ModuleComponentSelector
import org.gradle.api.artifacts.result.ResolvedDependencyResult
import org.gradle.api.attributes.java.TargetJvmEnvironment
import org.jetbrains.kotlin.gradle.plugin.KotlinPlatformType
import java.security.MessageDigest
import javax.xml.XMLConstants
import javax.xml.parsers.DocumentBuilderFactory
import org.w3c.dom.Element

plugins {
    `java-library`
    kotlin("jvm") version "2.4.21"
}

val corpusConfig = file(providers.gradleProperty("bindingCorpusConfig").get())
val corpusOutput = file(providers.gradleProperty("bindingCorpusOutput").get())
val includeRoots = providers.gradleProperty("bindingCorpusIncludeRoots").orNull?.split(',')?.toSet()
val relocationsFile = providers.gradleProperty("bindingCorpusPublishedRelocations").orNull?.let(::file)
val aliasesFile = providers.gradleProperty("bindingCorpusModuleAliases").orNull?.let(::file)
val moduleAliases = aliasesFile?.let { input ->
    (JsonSlurper().parse(input) as Map<*, *>).entries.associate { (source, target) ->
        require(source is String && target is String) { "Module aliases must map coordinates to coordinates." }
        val sourceParts = source.split(':')
        val targetParts = target.split(':')
        require(sourceParts.size == 2 && targetParts.size == 3 && sourceParts[0] == targetParts[0] &&
            sourceParts[1].endsWith("-jvm") && targetParts[1] == sourceParts[1].removeSuffix("-jvm") + "-android" &&
            targetParts[2].isNotBlank()) { "Only explicit Android/JVM variant aliases are supported: $source -> $target" }
        source to target
    }
}.orEmpty()
val publishedRelocations = relocationsFile?.let { input ->
    (JsonSlurper().parse(input) as List<*>).map { it as Map<*, *> }.associate { record ->
        val source = text(record, "source")
        val target = text(record, "target")
        val pom = file(text(record, "pom"))
        require(sha256(pom) == text(record, "pomSha256") && publishedRelocation(pom, source) == target) {
            "The substitution is not the relocation declared by the recorded Maven POM: $source -> $target"
        }
        source to record
    }
}.orEmpty()
layout.buildDirectory.set(corpusOutput.resolve("gradle-build"))

fun text(record: Map<*, *>, key: String): String =
    (record[key] as? String)?.takeIf { it.isNotBlank() } ?: error("Corpus record has no $key: $record")

val allConfigurationRecords = (JsonSlurper().parse(corpusConfig) as List<*>).flatMap { solution ->
    ((solution as Map<*, *>)["artifacts"] as List<*>).map { it as Map<*, *> }
}.onEach { record -> listOf("groupId", "artifactId", "version").forEach { text(record, it) } }
val configurationRecords = allConfigurationRecords.filter {
    includeRoots == null || "${text(it, "groupId")}:${text(it, "artifactId")}" in includeRoots
}
require(configurationRecords.isNotEmpty()) { "No corpus roots were selected." }

val nativeClosure = configurations.create("nativeClosure") {
    isCanBeConsumed = false
    attributes {
        attribute(Usage.USAGE_ATTRIBUTE, objects.named(Usage.JAVA_RUNTIME))
        attribute(Category.CATEGORY_ATTRIBUTE, objects.named(Category.LIBRARY))
        attribute(TargetJvmEnvironment.TARGET_JVM_ENVIRONMENT_ATTRIBUTE, objects.named(TargetJvmEnvironment.ANDROID))
        attribute(KotlinPlatformType.attribute, KotlinPlatformType.androidJvm)
    }
    resolutionStrategy.dependencySubstitution {
        publishedRelocations.forEach { (source, record) ->
            substitute(module(source)).using(module(text(record, "target")))
                .because("Relocation declared by the published Maven POM").withoutArtifactSelectors()
        }
        all {
            val module = requested as? ModuleComponentSelector
            module?.let { moduleAliases["${it.group}:${it.module}"] }?.let { target ->
                useTarget(target, "Select the Android variant of the same Kotlin Multiplatform module")
            }
        }
    }
}
dependencies {
    configurationRecords.forEach {
        add(nativeClosure.name, "${text(it, "groupId")}:${text(it, "artifactId")}:${text(it, "version")}")
    }
}
dependencyLocking {
    lockAllConfigurations()
    lockFile.set(corpusOutput.resolve("gradle.lockfile"))
}

fun sha256(file: File): String {
    val digest = MessageDigest.getInstance("SHA-256")
    file.inputStream().use { input ->
        val buffer = ByteArray(65536)
        while (true) {
            val count = input.read(buffer)
            if (count < 0) break
            digest.update(buffer, 0, count)
        }
    }
    return digest.digest().joinToString("") { "%02x".format(it) }
}

fun child(element: Element, name: String): Element? =
    (0 until element.childNodes.length).mapNotNull { element.childNodes.item(it) as? Element }
        .firstOrNull { it.localName == name }

fun publishedRelocation(pom: File, source: String): String? {
    val parts = source.split(':')
    require(parts.size == 3 && parts.all { it.isNotBlank() }) { "Invalid relocation source: $source" }
    val factory = DocumentBuilderFactory.newInstance()
    factory.isNamespaceAware = true
    factory.setFeature("http://apache.org/xml/features/disallow-doctype-decl", true)
    factory.setAttribute(XMLConstants.ACCESS_EXTERNAL_DTD, "")
    factory.setAttribute(XMLConstants.ACCESS_EXTERNAL_SCHEMA, "")
    val root = factory.newDocumentBuilder().parse(pom).documentElement
    require(child(root, "artifactId")?.textContent?.trim() == parts[1]) { "POM artifact identity does not match $source" }
    val management = child(root, "distributionManagement") ?: return null
    val relocation = child(management, "relocation") ?: return null
    val parent = child(root, "parent")
    val declaredSource = listOf("groupId", "artifactId", "version").map { key ->
        child(root, key)?.textContent?.trim() ?: parent?.let { child(it, key)?.textContent?.trim() }
    }
    require(declaredSource == parts) { "Relocation POM coordinates do not match $source: $declaredSource" }
    val target = listOf("groupId", "artifactId", "version").mapIndexed { index, key ->
        child(relocation, key)?.textContent?.trim()?.takeIf { it.isNotEmpty() } ?: parts[index]
    }
    require(target.none { it.contains("\${") } && target != parts) { "Unsupported Maven relocation on $source: $target" }
    return target.joinToString(":")
}

val corpusInventory = tasks.register("corpusInventory") {
    inputs.file(corpusConfig)
    outputs.file(corpusOutput.resolve("configuration-inventory.json"))
    doLast {
        corpusOutput.mkdirs()
        val inventory = mapOf(
            "schemaVersion" to 1,
            "policy" to "Every artifact record in config.json; no Maven-group allowlist",
            "configurationSha256" to sha256(corpusConfig),
            "originalRecords" to allConfigurationRecords.size,
            "mavenGroups" to allConfigurationRecords.groupingBy { text(it, "groupId") }.eachCount().toSortedMap(),
            "records" to allConfigurationRecords,
        )
        corpusOutput.resolve("configuration-inventory.json").writeText(JsonOutput.prettyPrint(JsonOutput.toJson(inventory)) + "\n")
        println("Inventoried all ${allConfigurationRecords.size} configured artifact records.")
    }
}

tasks.register("publishedRelocations") {
    dependsOn(corpusInventory)
    val poms = configurationRecords.map { record ->
        val source = "${text(record, "groupId")}:${text(record, "artifactId")}:${text(record, "version")}"
        source to configurations.detachedConfiguration(dependencies.create("$source@pom")).apply { isTransitive = false }
    }
    inputs.file(corpusConfig)
    inputs.files(poms.map { it.second })
    outputs.file(corpusOutput.resolve("published-relocations.json"))
    doLast {
        val records = poms.mapNotNull { (source, configuration) ->
            val pom = configuration.singleFile
            publishedRelocation(pom, source)?.let { target ->
                mapOf("source" to source, "target" to target, "pom" to pom.absolutePath, "pomSha256" to sha256(pom))
            }
        }
        corpusOutput.resolve("published-relocations.json").writeText(JsonOutput.prettyPrint(JsonOutput.toJson(records)) + "\n")
        println("Recorded ${records.size} publisher-declared Maven relocations.")
    }
}

val nativeInputs = tasks.register("nativeInputs") {
    dependsOn(corpusInventory)
    inputs.file(corpusConfig)
    if (aliasesFile != null) inputs.file(aliasesFile)
    if (relocationsFile != null) {
        inputs.file(relocationsFile)
        inputs.files(publishedRelocations.values.map { file(text(it, "pom")) })
    }
    inputs.files(nativeClosure)
    outputs.files(corpusOutput.resolve("native-inputs.json"), corpusOutput.resolve("selection.json"),
        corpusOutput.resolve("dependency-graph.json"))
    doLast {
        corpusOutput.mkdirs()
        val resolved = nativeClosure.incoming.resolutionResult
        val selected = resolved.allComponents.mapNotNull { component ->
            (component.id as? ModuleComponentIdentifier)?.let { id ->
                "${id.group}:${id.module}" to mapOf("group" to id.group, "artifact" to id.module,
                    "version" to id.version, "reasons" to component.selectionReason.descriptions.map { it.description })
            }
        }.toMap()
        val artifacts = nativeClosure.incoming.artifacts.artifacts.map { artifact ->
            val id = artifact.id.componentIdentifier as? ModuleComponentIdentifier
                ?: error("Unexpected native component ${artifact.id.componentIdentifier}")
            require(artifact.file.extension in setOf("jar", "aar")) { "Unsupported native input: ${artifact.file}" }
            mapOf("group" to id.group, "artifact" to id.module, "version" to id.version,
                "file" to artifact.file.absolutePath, "sha256" to sha256(artifact.file))
        }.distinctBy { "${it["group"]}:${it["artifact"]}:${it["version"]}:${it["sha256"]}" }
            .sortedBy { "${it["group"]}:${it["artifact"]}:${it["file"]}" }
        val records = configurationRecords.map { record ->
            val key = "${text(record, "groupId")}:${text(record, "artifactId")}"
            val relocation = publishedRelocations["$key:${text(record, "version")}"]
            val alias = moduleAliases[key]
            val selectedKey = relocation?.let { text(it, "target").substringBeforeLast(':') } ?: alias?.substringBeforeLast(':') ?: key
            val selection = selected[selectedKey] ?: error("Requested module was not resolved: $key")
            mapOf("requested" to record, "selected" to selection,
                "variantAlias" to alias,
                "publishedRelocation" to relocation,
                "superseded" to (key != selectedKey || text(record, "version") != selection["version"]))
        }
        val graph = resolved.allComponents.mapNotNull { component ->
            val id = component.id as? ModuleComponentIdentifier ?: return@mapNotNull null
            mapOf("coordinate" to "${id.group}:${id.module}:${id.version}", "dependencies" to
                component.dependencies.filterIsInstance<ResolvedDependencyResult>().map { dependency ->
                    val target = dependency.selected.id as? ModuleComponentIdentifier
                        ?: error("Unexpected selected native dependency ${dependency.selected.id}")
                    "${target.group}:${target.module}:${target.version}"
                }.distinct().sorted())
        }.sortedBy { it["coordinate"].toString() }
        fun write(name: String, value: Any) {
            corpusOutput.resolve(name).writeText(JsonOutput.prettyPrint(JsonOutput.toJson(value)) + "\n")
        }
        write("native-inputs.json", artifacts)
        write("selection.json", mapOf("schemaVersion" to 1, "policy" to "One Gradle-resolved version per module",
            "configurationSha256" to sha256(corpusConfig), "configurationRecords" to allConfigurationRecords.size,
            "includedRoots" to includeRoots?.sorted(), "records" to records, "modules" to selected.values))
        write("dependency-graph.json", graph)
        println("Resolved ${records.size} corpus records into ${selected.size} modules and ${artifacts.size} native archives.")
    }
}

evaluationDependsOn(":jvm-api-descriptor")
val descriptorProject = project(":jvm-api-descriptor")
val auditDirectory = corpusOutput.resolve("preflight")
val nativeOwnership = tasks.register<Exec>("nativeOwnership") {
    dependsOn(nativeInputs)
    val script = file("../prepare-inputs.py")
    inputs.file(script)
    inputs.file(corpusOutput.resolve("native-inputs.json"))
    inputs.files(nativeClosure)
    outputs.files(auditDirectory.resolve("input-audit.json"), auditDirectory.resolve("android-variant-aliases.json"),
        auditDirectory.resolve("conflict-summary.json"))
    commandLine("python3", script.absolutePath, "--native-inputs", corpusOutput.resolve("native-inputs.json").absolutePath,
        "--output", auditDirectory.absolutePath)
}
tasks.register<JavaExec>("nativeDescriptor") {
    dependsOn(nativeOwnership, descriptorProject.tasks.named("classes"))
    classpath = descriptorProject.extensions.getByType<SourceSetContainer>()["main"].runtimeClasspath
    mainClass.set("net.dot.android.descriptor.ApiDescriptorKt")
    maxHeapSize = "4g"
    inputs.files(nativeClosure)
    outputs.file(corpusOutput.resolve("api-descriptor.json"))
    args("--manifest", corpusOutput.resolve("native-inputs.json").absolutePath,
        "--output", corpusOutput.resolve("api-descriptor.json").absolutePath)
}
