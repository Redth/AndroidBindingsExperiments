package net.dot.android.descriptor

import com.google.gson.JsonArray
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonPrimitive
import org.objectweb.asm.Opcodes
import org.objectweb.asm.Type
import org.objectweb.asm.signature.SignatureReader
import org.objectweb.asm.signature.SignatureVisitor

private fun model(kind: String, name: String? = null) = JsonObject().apply {
    addProperty("kind", kind)
    if (name != null) addProperty("name", name)
}

private fun descriptorType(type: Type): JsonObject = when (type.sort) {
    Type.ARRAY -> model("array").apply { add("element", descriptorType(Type.getType(type.descriptor.substring(1)))) }
    Type.OBJECT -> model("class", type.internalName).apply { add("arguments", JsonArray()) }
    else -> model("primitive", type.descriptor)
}

private class TypeVisitor(private val emit: (JsonObject) -> Unit) : SignatureVisitor(Opcodes.ASM9) {
    private var current: JsonObject? = null

    override fun visitBaseType(descriptor: Char) = emit(model("primitive", descriptor.toString()))
    override fun visitTypeVariable(name: String) = emit(model("parameter", name))
    override fun visitArrayType(): SignatureVisitor = TypeVisitor { element ->
        emit(model("array").apply { add("element", element) })
    }

    override fun visitClassType(name: String) {
        current = model("class", name).apply { add("arguments", JsonArray()) }
    }

    override fun visitInnerClassType(name: String) {
        val outer = requireNotNull(current) { "An inner JVM type has no outer type." }
        current = model("class", outer.get("name").asString + "$" + name).apply {
            add("outerType", outer)
            add("arguments", JsonArray())
        }
    }

    override fun visitTypeArgument() {
        requireNotNull(current).getAsJsonArray("arguments").add(model("argument").apply { addProperty("variance", "STAR") })
    }

    override fun visitTypeArgument(wildcard: Char): SignatureVisitor {
        val variance = when (wildcard) {
            EXTENDS -> "OUT"
            SUPER -> "IN"
            INSTANCEOF -> "INVARIANT"
            else -> error("Unknown JVM generic variance: $wildcard")
        }
        val arguments = requireNotNull(current).getAsJsonArray("arguments")
        return TypeVisitor { type ->
            arguments.add(model("argument").apply { addProperty("variance", variance); add("type", type) })
        }
    }

    override fun visitEnd() = emit(requireNotNull(current) { "A JVM class type was not initialized." })
}

private class DeclarationVisitor : SignatureVisitor(Opcodes.ASM9) {
    val parameters = JsonArray()
    val interfaces = JsonArray()
    val arguments = JsonArray()
    val exceptions = JsonArray()
    var superclass: JsonElement? = null
    var result: JsonElement? = null
    private var formal: JsonObject? = null

    override fun visitFormalTypeParameter(name: String) {
        formal = model("parameter", name).apply { add("upperBounds", JsonArray()) }
        parameters.add(formal)
    }

    override fun visitClassBound(): SignatureVisitor = TypeVisitor { bound ->
        requireNotNull(formal).getAsJsonArray("upperBounds").add(bound)
    }

    override fun visitInterfaceBound(): SignatureVisitor = visitClassBound()
    override fun visitSuperclass(): SignatureVisitor = TypeVisitor { superclass = it }
    override fun visitInterface(): SignatureVisitor = TypeVisitor { interfaces.add(it) }
    override fun visitParameterType(): SignatureVisitor = TypeVisitor { arguments.add(it) }
    override fun visitReturnType(): SignatureVisitor = TypeVisitor { result = it }
    override fun visitExceptionType(): SignatureVisitor = TypeVisitor { exceptions.add(it) }
}

internal fun classSignature(signature: String?, superclass: String?, interfaces: List<String>): JsonObject {
    val visitor = DeclarationVisitor()
    if (signature != null) SignatureReader(signature).accept(visitor)
    return JsonObject().apply {
        add("typeParameters", visitor.parameters)
        add("superclass", visitor.superclass ?: superclass?.let { descriptorType(Type.getObjectType(it)) })
        add("interfaces", if (signature == null) JsonArray().apply {
            interfaces.forEach { add(descriptorType(Type.getObjectType(it))) }
        } else visitor.interfaces)
    }
}

internal fun methodSignature(signature: String?, descriptor: String): JsonObject {
    val visitor = DeclarationVisitor()
    SignatureReader(signature ?: descriptor).accept(visitor)
    return JsonObject().apply {
        add("typeParameters", visitor.parameters)
        add("parameters", visitor.arguments)
        add("returnType", requireNotNull(visitor.result) { "A JVM method signature has no return type." })
        add("exceptions", visitor.exceptions)
    }
}

internal fun fieldSignature(signature: String?, descriptor: String): JsonObject {
    var result: JsonObject? = null
    SignatureReader(signature ?: descriptor).acceptType(TypeVisitor { result = it })
    return requireNotNull(result) { "A JVM field signature has no type." }
}
