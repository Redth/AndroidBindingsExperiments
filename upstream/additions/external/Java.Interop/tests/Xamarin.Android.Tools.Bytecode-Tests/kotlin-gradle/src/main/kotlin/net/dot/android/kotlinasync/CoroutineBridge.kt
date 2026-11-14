package net.dot.android.kotlinasync

import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlin.coroutines.CoroutineContext

interface SuspendCall {
    suspend fun invoke(): Any?
}

interface Completion {
    fun completed(value: Any?, failure: Throwable?, cancelled: Boolean)
}

class Operation internal constructor(call: SuspendCall, completion: Completion, context: CoroutineContext) {
    private val scope = CoroutineScope(Dispatchers.Unconfined + context + SupervisorJob(context[Job]))
    private var value: Any? = null
    private val work = scope.async(start = CoroutineStart.LAZY) {
        value = call.invoke()
    }

    init {
        work.invokeOnCompletion { failure ->
            try {
                completion.completed(value, failure, failure is CancellationException)
            } finally {
                value = null
                scope.cancel()
            }
        }
    }

    fun start() { work.start() }
    fun cancel() { work.cancel() }
}

class FlowItem(val hasValue: Boolean, val value: Any?)

class FlowSession internal constructor(flow: Flow<*>, context: CoroutineContext) {
    private class Value(val value: Any?, val consumed: CompletableDeferred<Unit> = CompletableDeferred())
    private val scope = CoroutineScope(Dispatchers.Unconfined + context + SupervisorJob(context[Job]))
    private val values = Channel<Value>(Channel.RENDEZVOUS)
    private var pending: CompletableDeferred<Unit>? = null
    private val producer = scope.async(start = CoroutineStart.LAZY) {
        flow.collect {
            val item = Value(it)
            values.send(item)
            item.consumed.await()
        }
    }

    init {
        producer.invokeOnCompletion { failure -> values.close(failure) }
    }

    suspend fun next(): FlowItem {
        // Returning a value to .NET is not permission to produce another.
        // Acknowledge it only when the consumer requests its next value.
        pending?.complete(Unit)
        pending = null
        producer.start()
        val result = values.receiveCatching()
        if (result.isClosed) {
            result.exceptionOrNull()?.let { throw it }
            return FlowItem(false, null)
        }
        val item = result.getOrThrow()
        pending = item.consumed
        return FlowItem(true, item.value)
    }

    suspend fun close() {
        try {
            producer.cancelAndJoin()
        } finally {
            values.cancel()
            pending = null
            scope.cancel()
        }
    }
}

object CoroutineBridge {
    @JvmStatic fun create(call: SuspendCall, completion: Completion, context: CoroutineContext) = Operation(call, completion, context)
    @JvmStatic fun createFlow(flow: Flow<*>, context: CoroutineContext) = FlowSession(flow, context)
}
