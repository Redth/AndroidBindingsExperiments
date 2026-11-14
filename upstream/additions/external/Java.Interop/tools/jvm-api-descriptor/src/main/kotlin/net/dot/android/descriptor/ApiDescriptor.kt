package net.dot.android.descriptor

import com.google.gson.GsonBuilder
import com.google.gson.JsonArray
import com.google.gson.JsonElement
import com.google.gson.JsonNull
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import com.google.gson.JsonPrimitive
import com.google.gson.Strictness
import java.io.ByteArrayInputStream
import java.io.File
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.security.MessageDigest
import java.util.zip.ZipFile
import java.util.zip.ZipInputStream
import kotlin.metadata.*
import kotlin.metadata.jvm.*
import org.objectweb.asm.ClassReader
import org.objectweb.asm.ClassVisitor
import org.objectweb.asm.MethodVisitor
import org.objectweb.asm.Opcodes
import org.objectweb.asm.Type
import org.objectweb.asm.tree.AnnotationNode
import org.objectweb.asm.tree.ClassNode
import org.objectweb.asm.tree.FieldNode
import org.objectweb.asm.tree.MethodNode
import org.objectweb.asm.tree.TypeAnnotationNode

private const val metadataReaderVersion = "2.4.21"

private fun json(value: Any?): JsonElement = when (value) {
    null -> JsonNull.INSTANCE
    is JsonElement -> value
    is String -> if (hasUnpairedSurrogate(value)) obj(
        "kind" to "utf16-string", "codeUnits" to value.map { it.code }
    ) else JsonPrimitive(value)
    is Double -> if (value.isFinite()) JsonPrimitive(value) else obj(
        "kind" to "floating-point", "type" to "double", "bits" to value.toRawBits().toULong().toString(16), "value" to value.toString())
    is Float -> if (value.isFinite()) JsonPrimitive(value) else obj(
        "kind" to "floating-point", "type" to "float", "bits" to value.toRawBits().toUInt().toString(16), "value" to value.toString())
    is Number -> JsonPrimitive(value)
    is Boolean -> JsonPrimitive(value)
    is Char -> json(value.toString())
    is Enum<*> -> JsonPrimitive(value.name)
    is Type -> JsonPrimitive(value.descriptor)
    is AnnotationNode -> annotation(value)
    is Map<*, *> -> JsonObject().apply {
        value.forEach { (key, item) -> require(key is String); add(key, json(item)) }
    }
    is Iterable<*> -> JsonArray().apply { value.forEach { add(json(it)) } }
    is Array<*> -> JsonArray().apply { value.forEach { add(json(it)) } }
    is IntArray -> json(value.toList())
    is ByteArray -> json(value.toList())
    is LongArray -> json(value.toList())
    is ShortArray -> json(value.toList())
    is CharArray -> json(value.map { it.toString() })
    is FloatArray -> json(value.toList())
    is DoubleArray -> json(value.toList())
    is BooleanArray -> json(value.toList())
    else -> error("Unsupported descriptor value: ${value.javaClass.name}")
}

private fun hasUnpairedSurrogate(value: String): Boolean {
    var index = 0
    while (index < value.length) {
        val current = value[index++]
        if (current.isHighSurrogate()) {
            if (index == value.length || !value[index].isLowSurrogate()) return true
            index++
        } else if (current.isLowSurrogate()) {
            return true
        }
    }
    return false
}

private fun obj(vararg values: Pair<String, Any?>) = json(linkedMapOf(*values)).asJsonObject

private fun annotationValues(node: AnnotationNode): Map<String, Any?> {
    val values = node.values ?: return emptyMap()
    require(values.size % 2 == 0) { "Invalid annotation value count: ${node.desc}" }
    return (values.indices step 2).associate { index ->
        val name = values[index]
        require(name is String) { "Invalid annotation member name: ${node.desc}" }
        name to values[index + 1]
    }
}

private fun annotation(node: AnnotationNode): JsonObject = obj(
    "descriptor" to node.desc,
    "values" to if (node.desc == "Lkotlin/Metadata;") emptyMap<String, Any?>() else annotationValues(node)
)

private fun annotations(visible: List<AnnotationNode>?, invisible: List<AnnotationNode>?) =
    (visible.orEmpty() + invisible.orEmpty()).map(::annotation)

private fun typeAnnotations(visible: List<TypeAnnotationNode>?, invisible: List<TypeAnnotationNode>?) =
    (visible.orEmpty() + invisible.orEmpty()).map {
        obj("typeReference" to it.typeRef, "typePath" to it.typePath?.toString(), "annotation" to annotation(it))
    }

private fun visibility(access: Int) = when {
    access and Opcodes.ACC_PUBLIC != 0 -> "public"
    access and Opcodes.ACC_PROTECTED != 0 -> "protected"
    access and Opcodes.ACC_PRIVATE != 0 -> "private"
    else -> "package"
}

private fun jvmMethod(method: MethodNode): JsonObject = obj(
    "name" to method.name, "descriptor" to method.desc, "signature" to method.signature,
    "generic" to methodSignature(method.signature, method.desc),
    "access" to method.access, "visibility" to visibility(method.access),
    "static" to (method.access and Opcodes.ACC_STATIC != 0),
    "abstract" to (method.access and Opcodes.ACC_ABSTRACT != 0),
    "final" to (method.access and Opcodes.ACC_FINAL != 0),
    "bridge" to (method.access and Opcodes.ACC_BRIDGE != 0),
    "synthetic" to (method.access and Opcodes.ACC_SYNTHETIC != 0),
    "exceptions" to method.exceptions,
    "parameters" to method.parameters?.map { obj("name" to it.name, "access" to it.access) },
    "annotations" to annotations(method.visibleAnnotations, method.invisibleAnnotations),
    "typeAnnotations" to typeAnnotations(method.visibleTypeAnnotations, method.invisibleTypeAnnotations),
    "parameterAnnotations" to (0 until Type.getArgumentTypes(method.desc).size).map { index ->
        annotations(method.visibleParameterAnnotations?.getOrNull(index), method.invisibleParameterAnnotations?.getOrNull(index))
    },
    "annotationDefault" to method.annotationDefault
)

private fun jvmField(field: FieldNode): JsonObject = obj(
    "name" to field.name, "descriptor" to field.desc, "signature" to field.signature,
    "generic" to fieldSignature(field.signature, field.desc),
    "access" to field.access, "visibility" to visibility(field.access),
    "static" to (field.access and Opcodes.ACC_STATIC != 0),
    "final" to (field.access and Opcodes.ACC_FINAL != 0),
    "synthetic" to (field.access and Opcodes.ACC_SYNTHETIC != 0),
    "constant" to field.value,
    "annotations" to annotations(field.visibleAnnotations, field.invisibleAnnotations),
    "typeAnnotations" to typeAnnotations(field.visibleTypeAnnotations, field.invisibleTypeAnnotations)
)

private fun kotlinType(type: KmType, depth: Int = 0): JsonObject {
    require(depth < 128) { "Kotlin type exceeds the descriptor nesting limit." }
    val classifier = when (val value = type.classifier) {
        is KmClassifier.Class -> obj("kind" to "class", "name" to value.name)
        is KmClassifier.TypeAlias -> obj("kind" to "alias", "name" to value.name)
        is KmClassifier.TypeParameter -> obj("kind" to "parameter", "id" to value.id)
    }
    return obj(
        "classifier" to classifier, "nullable" to type.isNullable,
        "definitelyNonNull" to type.isDefinitelyNonNull, "suspend" to type.isSuspend,
        "arguments" to type.arguments.map { argument ->
            obj("variance" to (argument.variance?.name ?: "STAR"), "type" to argument.type?.let { kotlinType(it, depth + 1) })
        },
        "outerType" to type.outerType?.let { kotlinType(it, depth + 1) },
        "abbreviatedType" to type.abbreviatedType?.let { kotlinType(it, depth + 1) },
        "flexibleUpperBound" to type.flexibleTypeUpperBound?.let {
            obj("id" to it.typeFlexibilityId, "type" to kotlinType(it.type, depth + 1))
        }
    )
}

private fun typeParameter(parameter: KmTypeParameter): JsonObject = obj(
    "id" to parameter.id, "name" to parameter.name, "variance" to parameter.variance,
    "reified" to parameter.isReified, "upperBounds" to parameter.upperBounds.map { kotlinType(it) }
)

private fun valueParameter(parameter: KmValueParameter): JsonObject = obj(
    "name" to parameter.name, "type" to kotlinType(parameter.type),
    "varargElementType" to parameter.varargElementType?.let { kotlinType(it) },
    "defaultValue" to parameter.declaresDefaultValue,
    "crossinline" to parameter.isCrossinline, "noinline" to parameter.isNoinline
)

private fun function(function: KmFunction): JsonObject = obj(
    "name" to function.name, "visibility" to function.visibility.name.lowercase(),
    "modality" to function.modality, "kind" to function.kind,
    "signature" to function.signature?.let { obj("name" to it.name, "descriptor" to it.descriptor) },
    "suspend" to function.isSuspend, "inline" to function.isInline,
    "typeParameters" to function.typeParameters.map(::typeParameter),
    "receiverType" to function.receiverParameterType?.let { kotlinType(it) },
    "returnType" to kotlinType(function.returnType),
    "parameters" to function.valueParameters.map(::valueParameter)
)

private fun property(property: KmProperty): JsonObject = obj(
    "name" to property.name, "visibility" to property.visibility.name.lowercase(),
    "mutable" to property.isVar, "modality" to property.modality,
    "typeParameters" to property.typeParameters.map(::typeParameter),
    "receiverType" to property.receiverParameterType?.let { kotlinType(it) },
    "returnType" to kotlinType(property.returnType),
    "field" to property.fieldSignature?.let { obj("name" to it.name, "descriptor" to it.descriptor) },
    "getter" to property.getterSignature?.let {
        obj("name" to it.name, "descriptor" to it.descriptor, "visibility" to property.getter.visibility.name.lowercase())
    },
    "setter" to property.setterSignature?.let {
        obj("name" to it.name, "descriptor" to it.descriptor, "visibility" to property.setter?.visibility?.name?.lowercase())
    },
    "annotationsMethod" to property.syntheticMethodForAnnotations?.let { obj("name" to it.name, "descriptor" to it.descriptor) }
)

private fun declarationContainer(container: KmDeclarationContainer) = obj(
    "functions" to container.functions.map(::function),
    "properties" to container.properties.map(::property),
    "typeAliases" to container.typeAliases.map {
        obj("name" to it.name, "visibility" to it.visibility.name.lowercase(),
            "typeParameters" to it.typeParameters.map(::typeParameter),
            "underlyingType" to kotlinType(it.underlyingType), "expandedType" to kotlinType(it.expandedType))
    }
)

private fun classMetadata(klass: KmClass): JsonObject = declarationContainer(klass).apply {
    add("name", json(klass.name))
    add("visibility", json(klass.visibility.name.lowercase()))
    add("modality", json(klass.modality))
    add("kind", json(klass.kind))
    add("valueClass", json(klass.isValue))
    add("typeParameters", json(klass.typeParameters.map(::typeParameter)))
    add("supertypes", json(klass.supertypes.map { kotlinType(it) }))
    add("constructors", json(klass.constructors.map {
        obj("visibility" to it.visibility.name.lowercase(),
            "signature" to it.signature?.let { signature -> obj("name" to signature.name, "descriptor" to signature.descriptor) },
            "parameters" to it.valueParameters.map(::valueParameter))
    }))
    add("nestedClasses", json(klass.nestedClasses))
    add("companionObject", json(klass.companionObject))
    add("sealedSubclasses", json(klass.sealedSubclasses))
    add("underlyingValueType", json(klass.inlineClassUnderlyingType?.let { kotlinType(it) }))
}

private fun intArray(value: Any?): IntArray = when (value) {
    null -> intArrayOf()
    is IntArray -> value
    is List<*> -> value.map { require(it is Int); it }.toIntArray()
    else -> error("Expected an integer array in kotlin.Metadata.")
}

private fun stringArray(value: Any?): Array<String> = when (value) {
    null -> emptyArray()
    is List<*> -> value.map { require(it is String); it }.toTypedArray()
    is Array<*> -> value.map { require(it is String); it }.toTypedArray()
    else -> error("Expected a string array in kotlin.Metadata.")
}

private fun kotlinMetadata(node: ClassNode): JsonObject? {
    val annotation = (node.visibleAnnotations.orEmpty() + node.invisibleAnnotations.orEmpty())
        .singleOrNull { it.desc == "Lkotlin/Metadata;" } ?: return null
    val values = annotationValues(annotation)
    val version = intArray(values["mv"])
    val metadata = Metadata(
        kind = values["k"] as? Int ?: 1,
        metadataVersion = version,
        data1 = stringArray(values["d1"]), data2 = stringArray(values["d2"]),
        extraString = values["xs"] as? String ?: "", packageName = values["pn"] as? String ?: "",
        extraInt = values["xi"] as? Int ?: 0
    )
    var status = "supported"
    var strictDiagnostic: String? = null
    val parsed = try {
        KotlinClassMetadata.readStrict(metadata)
    } catch (error: IllegalArgumentException) {
        status = "lenient"
        strictDiagnostic = error.message
        KotlinClassMetadata.readLenient(metadata)
    }
    val result = obj("status" to status, "version" to version.toList(),
        "readerVersion" to metadataReaderVersion, "strictDiagnostic" to strictDiagnostic)
    when (parsed) {
        is KotlinClassMetadata.Class -> result.add("class", classMetadata(parsed.kmClass))
        is KotlinClassMetadata.FileFacade -> result.add("package", declarationContainer(parsed.kmPackage))
        is KotlinClassMetadata.MultiFileClassPart -> {
            result.add("package", declarationContainer(parsed.kmPackage))
            result.add("facadeClassName", json(parsed.facadeClassName))
        }
        is KotlinClassMetadata.MultiFileClassFacade -> result.add("partClassNames", json(parsed.partClassNames))
        is KotlinClassMetadata.SyntheticClass -> parsed.kmLambda?.let { result.add("lambda", function(it.function)) }
        is KotlinClassMetadata.Unknown -> result.add("status", json("unknown-kind"))
    }
    return result
}

private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

private data class Input(val file: File, val coordinate: String?, val expectedHash: String? = null)

private fun bridgeTargets(reader: ClassReader): Map<String, List<JsonObject>> {
    val targets = mutableMapOf<String, MutableList<JsonObject>>()
    reader.accept(object : ClassVisitor(Opcodes.ASM9) {
        override fun visitMethod(access: Int, name: String, descriptor: String, signature: String?, exceptions: Array<out String>?): MethodVisitor? {
            if (access and Opcodes.ACC_BRIDGE == 0) return null
            val calls = mutableListOf<JsonObject>()
            targets[name + descriptor] = calls
            return object : MethodVisitor(Opcodes.ASM9) {
                override fun visitMethodInsn(opcode: Int, owner: String, name: String, descriptor: String, isInterface: Boolean) {
                    calls.add(obj("owner" to owner, "name" to name, "descriptor" to descriptor, "opcode" to opcode, "interface" to isInterface))
                }
            }
        }
    }, ClassReader.SKIP_DEBUG or ClassReader.SKIP_FRAMES)
    return targets
}

private fun describe(input: Input, bytes: ByteArray, entry: String, archiveHash: String): JsonObject {
    val node = ClassNode(Opcodes.ASM9)
    val reader = ClassReader(bytes)
    reader.accept(node, ClassReader.SKIP_CODE or ClassReader.SKIP_FRAMES)
    val targets = bridgeTargets(reader)
    return obj(
        "binaryName" to node.name, "descriptor" to "L${node.name};",
        "origin" to obj("coordinate" to input.coordinate, "archive" to input.file.name,
            "archiveSha256" to archiveHash, "entry" to entry, "classSha256" to sha256(bytes)),
        "jvm" to obj(
            "version" to node.version, "access" to node.access, "visibility" to visibility(node.access),
            "superclass" to node.superName, "interfaces" to node.interfaces, "signature" to node.signature,
            "generic" to classSignature(node.signature, node.superName, node.interfaces),
            "outerClass" to node.outerClass, "outerMethod" to node.outerMethod, "outerMethodDescriptor" to node.outerMethodDesc,
            "innerClasses" to node.innerClasses.map {
                obj("binaryName" to it.name, "outerName" to it.outerName, "innerName" to it.innerName, "access" to it.access)
            },
            "annotations" to annotations(node.visibleAnnotations, node.invisibleAnnotations),
            "typeAnnotations" to typeAnnotations(node.visibleTypeAnnotations, node.invisibleTypeAnnotations),
            "fields" to node.fields.sortedWith(compareBy({ it.name }, { it.desc })).map(::jvmField),
            "methods" to node.methods.sortedWith(compareBy({ it.name }, { it.desc })).map { method ->
                jvmMethod(method).apply {
                    targets[method.name + method.desc]?.let { add("bridgeTargets", json(it)) }
                }
            }
        ),
        "kotlin" to kotlinMetadata(node)
    )
}

private fun classEntries(input: Input): Sequence<Pair<String, ByteArray>> = sequence {
    ZipFile(input.file).use { archive ->
        for (entry in archive.entries().asSequence().filter { !it.isDirectory }.sortedBy { it.name }) {
            if (entry.name.endsWith(".class") && entry.name != "module-info.class" && !entry.name.startsWith("META-INF/")) {
                yield(entry.name to archive.getInputStream(entry).use { it.readBytes() })
            } else if (input.file.extension == "aar" &&
                (entry.name == "classes.jar" || entry.name.startsWith("libs/") && entry.name.endsWith(".jar"))) {
                ZipInputStream(ByteArrayInputStream(archive.getInputStream(entry).use { it.readBytes() })).use { jar ->
                    var item = jar.nextEntry
                    while (item != null) {
                        if (!item.isDirectory && item.name.endsWith(".class") && item.name != "module-info.class" && !item.name.startsWith("META-INF/"))
                            yield("${entry.name}!/${item.name}" to jar.readBytes())
                        item = jar.nextEntry
                    }
                }
            }
        }
    }
}

private fun linkMultifileFacades(classes: Map<String, JsonObject>) {
    for ((name, node) in classes) {
        val metadata = node.get("kotlin").takeUnless { it.isJsonNull }?.asJsonObject ?: continue
        val partNames = metadata.getAsJsonArray("partClassNames") ?: continue
        if (metadata.get("status").asString != "supported") continue
        val parts = partNames.map { classes[it.asString]?.get("kotlin")?.takeUnless { value -> value.isJsonNull }?.asJsonObject }
        if (parts.any { it == null || it.get("status").asString != "supported" || it.get("facadeClassName")?.asString != name }) {
            metadata.addProperty("status", "incomplete-multifile")
            metadata.addProperty("strictDiagnostic", "Multifile facade has missing, unsupported, or mismatched part metadata.")
            continue
        }
        val jvm = node.getAsJsonObject("jvm")
        val slots = (jvm.getAsJsonArray("methods") + jvm.getAsJsonArray("fields"))
            .map { it.asJsonObject }.map { it.get("name").asString + it.get("descriptor").asString }.toSet()
        fun exported(declaration: JsonObject): Boolean =
            declaration.get("visibility").asString !in setOf("private", "private_to_this", "local")
        fun slot(declaration: JsonObject, key: String): String? =
            declaration.get(key)?.takeUnless { it.isJsonNull }?.asJsonObject?.let {
                it.get("name").asString + it.get("descriptor").asString
            }
        val packages = parts.map { requireNotNull(it).getAsJsonObject("package") }
        metadata.add("package", obj(
            "functions" to packages.flatMap { it.getAsJsonArray("functions").map { value -> value.asJsonObject } }
                .filter { exported(it) && slot(it, "signature") in slots },
            "properties" to packages.flatMap { it.getAsJsonArray("properties").map { value -> value.asJsonObject } }
                .filter { exported(it) && listOf("getter", "setter", "field").any { key -> slot(it, key) in slots } },
            "typeAliases" to packages.flatMap { it.getAsJsonArray("typeAliases").map { value -> value.asJsonObject } }.filter(::exported)
        ))
    }
}

fun main(args: Array<String>) {
    val inputs = mutableListOf<Input>()
    var output: File? = null
    var outputDirectory: File? = null
    var index = 0
    while (index < args.size) {
        val option = args[index++]
        require(index < args.size) { "Missing value for $option" }
        val value = args[index++]
        when (option) {
            "--manifest" -> {
                val manifest = JsonParser.parseString(File(value).readText()).asJsonArray
                manifest.forEach { record ->
                    val artifact = record.asJsonObject
                    val coordinate = listOf("group", "artifact", "version").map { artifact[it]?.asString }
                        .takeIf { it.all { part -> part != null } }?.joinToString(":")
                    inputs.add(Input(File(artifact.get("file").asString), coordinate, artifact.get("sha256")?.asString))
                }
            }
            "--archive" -> inputs.add(Input(File(value), null))
            "--output" -> output = File(value)
            "--output-directory" -> outputDirectory = File(value)
            else -> error("Unknown descriptor option: $option")
        }
    }
    require(inputs.isNotEmpty()) { "At least one --manifest or --archive is required." }
    require((output == null) != (outputDirectory == null)) { "Specify exactly one of --output or --output-directory." }
    if (outputDirectory != null) {
        val directory = outputDirectory.absoluteFile
        directory.mkdirs()
        val indexFile = directory.resolve("index.json")
        Files.deleteIfExists(indexFile.toPath())
        val records = inputs.distinctBy { it.coordinate to it.file.canonicalPath }
            .sortedWith(compareBy({ it.coordinate ?: "" }, { it.file.canonicalPath })).map { input ->
                val hash = sha256(input.file.readBytes())
                require(input.expectedHash == null || input.expectedHash == hash) { "Archive changed after resolution: ${input.file}" }
                val descriptor = directory.resolve(sha256(((input.coordinate ?: "") + ":" + hash).toByteArray()) + ".json")
                writeDescriptor(listOf(input), descriptor)
                obj("coordinate" to input.coordinate, "archiveSha256" to hash, "descriptor" to descriptor.name,
                    "descriptorSha256" to sha256(descriptor.readBytes()))
            }
        writeJson(indexFile, obj("schemaVersion" to 1, "status" to "complete", "artifacts" to records))
        return
    }
    writeDescriptor(inputs, requireNotNull(output))
}

private fun writeDescriptor(inputs: List<Input>, destination: File) {
    require(inputs.none { it.file.canonicalFile == destination.canonicalFile }) { "The descriptor output cannot overwrite an input archive." }
    Files.deleteIfExists(destination.toPath())
    val classes = sortedMapOf<String, JsonObject>()
    val artifacts = mutableListOf<JsonObject>()
    inputs.distinctBy { it.file.canonicalPath }.sortedWith(compareBy({ it.coordinate ?: "" }, { it.file.name })).forEach { input ->
        require(input.file.isFile) { "Input archive does not exist: ${input.file}" }
        val hash = sha256(input.file.readBytes())
        require(input.expectedHash == null || input.expectedHash == hash) { "Archive changed after resolution: ${input.file}" }
        artifacts.add(obj("coordinate" to input.coordinate, "archive" to input.file.name, "sha256" to hash))
        for ((entry, bytes) in classEntries(input)) {
            val descriptor = describe(input, bytes, entry, hash)
            val name = descriptor.get("binaryName").asString
            require(classes.putIfAbsent(name, descriptor) == null) { "Duplicate JVM binary class: $name in ${input.file}" }
        }
    }
    linkMultifileFacades(classes)
    val result = obj("schemaVersion" to 1, "status" to "complete",
        "tool" to obj("name" to "jvm-api-descriptor", "kotlinMetadataReader" to metadataReaderVersion, "asm" to "9.8"),
        "inputs" to artifacts, "classes" to classes.values.toList())
    writeJson(destination, result)
    println("Described ${classes.size} JVM classes from ${artifacts.size} archives; official Kotlin metadata reader $metadataReaderVersion.")
}

private fun writeJson(destination: File, result: JsonObject) {
    destination.absoluteFile.parentFile.mkdirs()
    val temporary = Files.createTempFile(destination.absoluteFile.parentFile.toPath(), destination.name, ".writing")
    try {
        Files.newBufferedWriter(temporary).use { writer ->
            GsonBuilder().setStrictness(Strictness.STRICT).serializeNulls().disableHtmlEscaping().create().toJson(result, writer)
            writer.append('\n')
        }
        Files.move(temporary, destination.toPath(), StandardCopyOption.ATOMIC_MOVE, StandardCopyOption.REPLACE_EXISTING)
    } finally {
        Files.deleteIfExists(temporary)
    }
}
