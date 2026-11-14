package net.dot.android.descriptor

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.io.File
import java.util.zip.ZipEntry
import java.util.zip.ZipFile
import java.util.zip.ZipOutputStream
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class DescriptorTests {
    private fun extract(name: String): Pair<File, JsonObject> {
        val directory = File(System.getProperty("descriptorTestOutput"))
        directory.mkdirs()
        val output = directory.resolve("$name.json")
        main(arrayOf("--archive", System.getProperty("descriptorFixtures"), "--output", output.absolutePath))
        return output to JsonParser.parseString(output.readText()).asJsonObject
    }

    private fun type(root: JsonObject, name: String) = root.getAsJsonArray("classes")
        .single { it.asJsonObject.get("binaryName").asString == "net/dot/android/descriptor/fixtures/$name" }.asJsonObject

    @Test
    fun kotlinVisibilityIsIndependentOfPhysicalJvmVisibility() {
        val root = extract("visibility").second
        val node = type(root, "InternalApi")
        assertEquals("public", node.getAsJsonObject("jvm").get("visibility").asString)
        assertEquals("internal", node.getAsJsonObject("kotlin").getAsJsonObject("class").get("visibility").asString)
    }

    @Test
    fun genericsKeepVarianceBoundsOuterScopesAndNullableSuspendResults() {
        val root = extract("generics").second
        val sink = type(root, "Sink").getAsJsonObject("kotlin").getAsJsonObject("class")
        assertEquals("IN", sink.getAsJsonArray("typeParameters")[0].asJsonObject.get("variance").asString)
        val contract = type(root, "Contract").getAsJsonObject("kotlin").getAsJsonObject("class")
        val load = contract.getAsJsonArray("functions").map { it.asJsonObject }.single { it.get("name").asString == "load" }
        assertTrue(load.get("suspend").asBoolean)
        assertTrue(load.getAsJsonObject("signature").get("descriptor").asString.contains("Lkotlin/coroutines/Continuation;"))
        assertTrue(load.getAsJsonObject("returnType").getAsJsonArray("arguments")[0].asJsonObject.getAsJsonObject("type").get("nullable").asBoolean)
        assertEquals("kotlin/CharSequence", contract.getAsJsonArray("typeParameters")[0].asJsonObject
            .getAsJsonArray("upperBounds")[0].asJsonObject.getAsJsonObject("classifier").get("name").asString)
        val inner = type(root, "Outer\$Inner").getAsJsonObject("kotlin").getAsJsonObject("class")
        val self = inner.getAsJsonArray("functions").map { it.asJsonObject }.single { it.get("name").asString == "self" }
        assertFalse(self.getAsJsonObject("returnType").get("outerType").isJsonNull)
    }

    @Test
    fun arraysDefaultsAndValueClassesRetainTheirRealAbi() {
        val root = extract("abi").second
        val contract = type(root, "Contract")
        val methods = contract.getAsJsonObject("jvm").getAsJsonArray("methods").map { it.asJsonObject }
        assertEquals("([Ljava/lang/Integer;)[Ljava/lang/Integer;", methods.single { it.get("name").asString == "boxed" }.get("descriptor").asString)
        assertEquals("([I)[I", methods.single { it.get("name").asString == "primitives" }.get("descriptor").asString)
        assertTrue(methods.any { it.get("name").asString == "withDefault\$default" })
        val token = type(root, "Token")
        assertTrue(token.getAsJsonObject("kotlin").getAsJsonObject("class").get("valueClass").asBoolean)
        assertTrue(token.getAsJsonObject("jvm").getAsJsonArray("methods").any { it.asJsonObject.get("name").asString == "unbox-impl" })
    }

    @Test
    fun javaSignatureTreesDoNotEraseUpperAndLowerWildcards() {
        val fixture = type(extract("java").second, "JavaFixture").getAsJsonObject("jvm")
        val methods = fixture.getAsJsonArray("methods").map { it.asJsonObject }
        assertEquals("IN", methods.single { it.get("name").asString == "lower" }.getAsJsonObject("generic")
            .getAsJsonObject("returnType").getAsJsonArray("arguments")[0].asJsonObject.get("variance").asString)
        assertEquals("OUT", methods.single { it.get("name").asString == "upper" }.getAsJsonObject("generic")
            .getAsJsonObject("returnType").getAsJsonArray("arguments")[0].asJsonObject.get("variance").asString)
        assertEquals(2, fixture.getAsJsonObject("generic").getAsJsonArray("typeParameters")[0].asJsonObject.getAsJsonArray("upperBounds").size())
        val constants = fixture.getAsJsonArray("fields").map { it.asJsonObject }.filter { it.get("name").asString in setOf("NON_FINITE", "NOT_A_NUMBER") }
        assertEquals(2, constants.size)
        assertTrue(constants.all { it.getAsJsonObject("constant").get("kind").asString == "floating-point" })
        val malformed = fixture.getAsJsonArray("fields").single { it.asJsonObject.get("name").asString == "LONE_SURROGATE" }
            .asJsonObject.getAsJsonObject("constant")
        assertEquals("utf16-string", malformed.get("kind").asString)
        assertEquals(0xD800, malformed.getAsJsonArray("codeUnits").single().asInt)
    }

    @Test
    fun multifileFacadesUsePartVisibilityWithoutPrivateNameCollisions() {
        val metadata = type(extract("multifile").second, "MultifileApi").getAsJsonObject("kotlin")
        val declarations = metadata.getAsJsonObject("package")
        val functions = declarations.getAsJsonArray("functions").map { it.asJsonObject }
        assertEquals("internal", functions.single { it.get("name").asString == "internalFacadeValue" }.get("visibility").asString)
        assertEquals("public", functions.single { it.get("name").asString == "publicFacadeValue" }.get("visibility").asString)
        assertFalse(functions.any { it.get("visibility").asString == "private" })
        assertEquals("internal", declarations.getAsJsonArray("properties").single().asJsonObject.get("visibility").asString)
    }

    @Test
    fun bridgesRecordTheirActualTypedTargetRatherThanOtherOverloads() {
        val fixture = type(extract("bridges").second, "JavaFixture\$Child").getAsJsonObject("jvm")
        val bridge = fixture.getAsJsonArray("methods").single { it.asJsonObject.get("bridge").asBoolean }.asJsonObject
        val target = bridge.getAsJsonArray("bridgeTargets").single().asJsonObject
        assertEquals("echo", target.get("name").asString)
        assertEquals("(Ljava/lang/String;)Ljava/lang/String;", target.get("descriptor").asString)
        assertEquals("net/dot/android/descriptor/fixtures/JavaFixture\$Child", target.get("owner").asString)
    }

    @Test
    fun moduleDescriptorsAreNotApplicationClassDefinitions() {
        val directory = File(System.getProperty("descriptorTestOutput"))
        directory.mkdirs()
        val input = directory.resolve("module.jar")
        ZipOutputStream(input.outputStream()).use { output ->
            ZipFile(System.getProperty("descriptorFixtures")).use { fixtures ->
                fixtures.entries().asSequence().filter { !it.isDirectory }.forEach { entry ->
                    output.putNextEntry(ZipEntry(entry.name))
                    fixtures.getInputStream(entry).use { it.copyTo(output) }
                    output.closeEntry()
                }
            }
            output.putNextEntry(ZipEntry("module-info.class"))
            output.write(byteArrayOf(1, 2, 3))
            output.closeEntry()
        }
        val output = directory.resolve("module.json")
        main(arrayOf("--archive", input.absolutePath, "--output", output.absolutePath))
        val root = JsonParser.parseString(output.readText()).asJsonObject
        assertFalse(root.getAsJsonArray("classes").any { it.asJsonObject.get("binaryName").asString == "module-info" })
    }

    @Test
    fun corpusDescriptorsKeepConflictingArtifactDefinitionsSeparate() {
        val directory = File(System.getProperty("descriptorTestOutput")).resolve("corpus")
        directory.mkdirs()
        val fixture = File(System.getProperty("descriptorFixtures"))
        val manifest = directory.resolve("inputs.json")
        manifest.writeText(com.google.gson.Gson().toJson(listOf(
            mapOf("group" to "example", "artifact" to "first", "version" to "1", "file" to fixture.absolutePath),
            mapOf("group" to "example", "artifact" to "second", "version" to "1", "file" to fixture.absolutePath)
        )))
        main(arrayOf("--manifest", manifest.absolutePath, "--output-directory", directory.absolutePath))
        val index = directory.resolve("index.json")
        val records = JsonParser.parseString(index.readText()).asJsonObject.getAsJsonArray("artifacts")
        assertEquals(2, records.size())
        assertEquals(2, records.map { it.asJsonObject.get("descriptor").asString }.toSet().size)
        val origins = records.map {
            val descriptor = directory.resolve(it.asJsonObject.get("descriptor").asString)
            JsonParser.parseString(descriptor.readText()).asJsonObject.getAsJsonArray("classes")
                .first().asJsonObject.getAsJsonObject("origin").get("coordinate").asString
        }
        assertEquals(setOf("example:first:1", "example:second:1"), origins.toSet())
        manifest.writeText(com.google.gson.Gson().toJson(listOf(
            mapOf("group" to "example", "artifact" to "first", "version" to "1",
                "file" to fixture.absolutePath, "sha256" to "wrong")
        )))
        assertFailsWith<IllegalArgumentException> {
            main(arrayOf("--manifest", manifest.absolutePath, "--output-directory", directory.absolutePath))
        }
        assertFalse(index.exists())
    }

    @Test
    fun outputIsDeterministicAndFailedRunsRemoveStaleSuccess() {
        val first = extract("deterministic").first.readBytes()
        assertTrue(first.contentEquals(extract("deterministic").first.readBytes()))
        val directory = File(System.getProperty("descriptorTestOutput"))
        val duplicate = directory.resolve("duplicate.jar")
        File(System.getProperty("descriptorFixtures")).copyTo(duplicate, overwrite = true)
        val output = directory.resolve("deterministic.json")
        assertFailsWith<IllegalArgumentException> {
            main(arrayOf("--archive", System.getProperty("descriptorFixtures"), "--archive", duplicate.absolutePath, "--output", output.absolutePath))
        }
        assertFalse(output.exists())
    }
}
