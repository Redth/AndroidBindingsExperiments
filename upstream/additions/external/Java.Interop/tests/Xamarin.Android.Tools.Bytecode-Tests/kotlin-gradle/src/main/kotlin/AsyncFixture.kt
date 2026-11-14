package net.dot.android.kotlinasync

import kotlinx.coroutines.*
import kotlinx.coroutines.flow.*
import java.util.concurrent.atomic.AtomicInteger
import kotlin.coroutines.CoroutineContext

interface ManagedEcho {
    suspend fun echo(value: String): String
}

interface DefaultService {
    fun value(prefix: String = "interface-default"): String
}

class DefaultServiceImpl : DefaultService {
    override fun value(prefix: String): String = prefix
}

class DefaultsConstructed(val value: String = "ctor-default", val number: Int = 4)

class MixedConstructed(optional: String = "ctor-prefix", val required: Int) {
    val value: String = optional + required
}

object StaticDefaults {
    @JvmStatic fun value(text: String = "static-default"): String = text
}

fun String.appendDefault(suffix: String = "!"): String = this + suffix

data class Payload(val text: String)

@JvmInline
value class Count(val value: Int) {
    init { require(value >= 0) { "negative-count" } }
}

@JvmInline
value class Label(val value: String)

@JvmInline
value class NullableLabel(val value: String?)

@JvmInline
value class GenericLabel<T>(val value: T)

class AsyncFixture {
    val calls = AtomicInteger()
    val cleanups = AtomicInteger()
    val collections = AtomicInteger()
    val emissions = AtomicInteger()
    val defaultEvaluations = AtomicInteger()

    suspend fun immediate(): String = "immediate"
    suspend fun delayed(): String { delay(20); return "delayed" }
    suspend fun number(): Int { delay(1); return 42 }
    suspend fun nullableText(): String? = null
    suspend fun nullableNumber(): Int? = null
    suspend fun nullableBoolean(): Boolean? = null
    suspend fun booleanValue(): Boolean = true
    suspend fun longValue(): Long = 9223372036854775806L
    suspend fun doubleValue(): Double = 123.5
    suspend fun floatValue(): Float = 12.5F
    suspend fun shortValue(): Short = -32000
    suspend fun byteValue(): Byte = -120
    suspend fun charValue(): Char = 'K'
    suspend fun echo(value: Any): Any { delay(1); return value }
    suspend fun dispatcherName(): String { delay(1); return Thread.currentThread().name }
    suspend fun callManaged(service: ManagedEcho, value: String): String = service.echo(value)
    suspend fun payload(): Payload? { delay(1); return Payload("typed-payload") }
    suspend fun suspendedCount(value: Count?): Count? { delay(1); return value }
    fun nullableCount(value: Count?): Count? = value
    fun countFlow(): Flow<Count> = flowOf(Count(3), Count(5))
    suspend fun nestedList(): List<List<String?>> = listOf(listOf("first", null), listOf("last"))
    suspend fun nullableList(): List<String>? = null
    suspend fun mapResult(): Map<String?, Int?> = linkedMapOf(null to null, "answer" to 42)
    fun setFlow(): Flow<Set<String>> = flowOf(linkedSetOf("first", "last"))
    fun defaults(prefix: String = "hello", times: Int = 2): String = prefix.repeat(times)
    suspend fun suspendDefaults(prefix: String = "async", times: Int = 2): String { delay(1); return prefix.repeat(times) }
    fun mixed(optional: String = "prefix", required: Int): String = optional + required
    fun nullableDefault(value: String? = "native-default"): String? = value
    private fun defaultNumber(): Int = defaultEvaluations.incrementAndGet()
    fun lazyDefault(value: Int = defaultNumber()): Int = value
    fun defaultService(): DefaultService = DefaultServiceImpl()
    fun wide(
        p0: Int = 0, p1: Int = 1, p2: Int = 2, p3: Int = 3, p4: Int = 4,
        p5: Int = 5, p6: Int = 6, p7: Int = 7, p8: Int = 8, p9: Int = 9,
        p10: Int = 10, p11: Int = 11, p12: Int = 12, p13: Int = 13, p14: Int = 14,
        p15: Int = 15, p16: Int = 16, p17: Int = 17, p18: Int = 18, p19: Int = 19,
        p20: Int = 20, p21: Int = 21, p22: Int = 22, p23: Int = 23, p24: Int = 24,
        p25: Int = 25, p26: Int = 26, p27: Int = 27, p28: Int = 28, p29: Int = 29,
        p30: Int = 30, p31: Int = 31, p32: Int = 32
    ): Int = p0 + p31 + p32
    fun defaultContext(): CoroutineContext = Dispatchers.Default
    fun mainContext(): CoroutineContext = Dispatchers.Main.immediate + Job()
    fun dispatcherFlow(): Flow<String> = flow {
        emit(Thread.currentThread().name)
        delay(1)
        emit(Thread.currentThread().name)
    }
    fun parentContext(): CoroutineContext = Job()
    fun cancelContext(context: CoroutineContext) { context[Job]?.cancel() }
    suspend fun unit() { delay(1) }
    suspend fun fail(): String { delay(1); error("native-suspend-failure") }
    suspend fun failImmediately(): String = error("native-immediate-failure")
    suspend fun cancelItself(): String = throw CancellationException("native-cancellation")
    suspend fun cancellable(): String {
        calls.incrementAndGet()
        try {
            awaitCancellation()
        } finally {
            delayCleanup()
        }
    }

    private suspend fun delayCleanup() = withContext(NonCancellable) {
        delay(10)
        cleanups.incrementAndGet()
    }

    fun finite(): Flow<String?> = flow {
        collections.incrementAndGet()
        try {
            emit("first")
            delay(1)
            emit(null)
            emit("last")
        } finally {
            delayCleanup()
        }

    }

    val propertyFlow: Flow<String?> get() = finite()
    fun nullableFlow(): Flow<String>? = null

    fun failing(): Flow<String> = flow {
        emit("before-error")
        error("native-flow-failure")
    }

    fun infinite(): Flow<Int> = flow {
        collections.incrementAndGet()
        try {
            while (true) {
                emissions.incrementAndGet()
                emit(1)
            }
        } finally {
            delayCleanup()
        }
    }

    fun waiting(): Flow<Int> = flow {
        try {
            awaitCancellation()
        } finally {
            delayCleanup()
        }
    }

    fun state(): MutableStateFlow<Int> = MutableStateFlow(7)
    fun echoFlow(value: Any): Flow<Any?> = flowOf(value, null, value)
    fun burst(count: Int): Flow<Int> = flow { repeat(count) { emit(it) } }
}
