package net.dot.android.descriptor.fixtures

internal class InternalApi {
    internal fun hidden(value: String) = value
}

class Outer<T> {
    inner class Inner<U> {
        fun <V : CharSequence> echo(value: V): V = value
        fun outer(value: T): T = value
        fun self(): Outer<T>.Inner<U> = this
    }
}

interface Sink<in T> {
    fun accept(value: T)
}

class Contract<T : CharSequence> {
    suspend fun load(value: T): List<T?> = listOf(value, null)
    fun withDefault(value: Int = 7): Int = value
    fun boxed(values: Array<Int?>): Array<Int?> = values
    fun primitives(values: IntArray): IntArray = values
    var title: String = ""
        private set
}

typealias Names = List<String?>

fun names(): Names = listOf(null)

@JvmInline
value class Token(val value: Int) {
    init {
        require(value >= 0)
    }
}
