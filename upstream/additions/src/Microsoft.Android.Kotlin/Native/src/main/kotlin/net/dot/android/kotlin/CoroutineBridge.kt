package net.dot.android.kotlin

import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.intrinsics.startCoroutineUninterceptedOrReturn
import kotlin.coroutines.intrinsics.suspendCoroutineUninterceptedOrReturn

interface SuspendCall {
    fun invoke(continuation: Any): Any?
}

interface Completion {
    fun completed(value: Any?, failure: Throwable?, cancelled: Boolean)
}

interface FlowCallback {
    fun item(value: Any?)
    fun completed(failure: Throwable?, cancelled: Boolean)
}

private fun scope(context: Any?): CoroutineScope {
    require(context == null || context is CoroutineContext) { "Expected a Kotlin CoroutineContext." }
    val actual = context as CoroutineContext? ?: EmptyCoroutineContext
    return CoroutineScope(Dispatchers.Unconfined + actual + SupervisorJob(actual[Job]))
}

class NativeOperation internal constructor(call: SuspendCall, completion: Completion, context: Any?) {
    private val scope = scope(context)
    private var value: Any? = null
    private val work = scope.async(start = CoroutineStart.LAZY) {
        value = suspendCoroutineUninterceptedOrReturn { continuation -> call.invoke(continuation) }
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

class NativeFlow internal constructor(flow: Flow<*>, callback: FlowCallback, context: Any?) {
    private val scope = scope(context)
    private val demand = Channel<Unit>(Channel.CONFLATED)
    private val work = scope.async(start = CoroutineStart.LAZY) {
        demand.receive()
        flow.collect {
            callback.item(it)
            // Only the next MoveNextAsync acknowledges the current item.
            demand.receive()
        }
    }

    init {
        work.invokeOnCompletion { failure ->
            try {
                callback.completed(failure, failure is CancellationException)
            } finally {
                demand.cancel()
                scope.cancel()
            }
        }
    }

    fun request() {
        if (work.isCompleted) return
        val result = demand.trySend(Unit)
        check(result.isSuccess || work.isCompleted) { "Flow demand channel closed before completion." }
        work.start()
    }

    fun cancel() { work.cancel() }
}

interface ManagedCall {
    fun start(completion: ManagedCompletion)
    fun cancel()
}

class ManagedCompletion internal constructor(private val result: CompletableDeferred<Any?>) {
    fun succeed(value: Any?) { check(result.complete(value)) { "Managed operation completed twice." } }
    fun fail(failure: Throwable) { check(result.completeExceptionally(failure)) { "Managed operation completed twice." } }
    fun cancel() { result.cancel() }
}

object CoroutineBridge {
    @JvmStatic
    fun create(call: SuspendCall, completion: Completion, context: Any?): NativeOperation =
        NativeOperation(call, completion, context)

    @JvmStatic
    fun subscribe(flow: Any, callback: FlowCallback, context: Any?): NativeFlow {
        require(flow is Flow<*>) { "Expected a Kotlin Flow." }
        return NativeFlow(flow, callback, context)
    }

    @JvmStatic
    fun fromManaged(call: ManagedCall, continuation: Any): Any? {
        require(continuation is Continuation<*>) { "Expected a Kotlin Continuation." }
        val action: suspend () -> Any? = {
            val result = CompletableDeferred<Any?>()
            val completion = ManagedCompletion(result)
            try {
                suspendCancellableCoroutine<Unit> { ready ->
                    ready.invokeOnCancellation { call.cancel() }
                    result.invokeOnCompletion { ready.resumeWith(Result.success(Unit)) }
                    call.start(completion)
                }
                result.await()
            } catch (cancelled: CancellationException) {
                // Native cancellation must also wait for managed finally blocks.
                withContext(NonCancellable) {
                    try {
                        result.await()
                    } catch (_: CancellationException) {
                    } catch (failure: Throwable) {
                        cancelled.addSuppressed(failure)
                    }
                }
                throw cancelled
            }
        }
        @Suppress("UNCHECKED_CAST")
        return action.startCoroutineUninterceptedOrReturn(continuation as Continuation<Any?>)
    }

    @JvmStatic
    fun unitValue(): Any = Unit

    @JvmStatic
    fun isUnit(value: Any?): Boolean = value === Unit
}
