package net.dot.android.reference

import android.content.Context
import androidx.datastore.preferences.core.PreferenceDataStoreFactory
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.intPreferencesKey
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.Preferences
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.lifecycleScope
import java.io.File
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.flowOf
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.onCompletion
import kotlinx.coroutines.flow.onStart
import kotlin.coroutines.CoroutineContext

interface ChangeListener {
    fun changed(value: Int, owner: LifecycleOwner)
}

class GenericSuspender<T>(private val initial: T) {
    suspend fun echo(value: T): T = value
    fun flow(): Flow<T> = flowOf(initial)
}

class SettingsRepository private constructor(context: Context) {
    private val key = intPreferencesKey("count")
    private val collectors = AtomicInteger()
    private val store = PreferenceDataStoreFactory.create(
        scope = CoroutineScope(SupervisorJob() + Dispatchers.IO),
        produceFile = { File(context.filesDir, "binding-reference.preferences_pb") }
    )

    fun snapshots(): Flow<Int> = store.data.map { it[key] ?: 0 }
        .onStart { collectors.incrementAndGet() }
        .onCompletion { collectors.decrementAndGet() }

    suspend fun increment(): Int {
        val updated = store.edit { it[key] = (it[key] ?: 0) + 1 }
        return updated[key] ?: error("DataStore did not commit the counter.")
    }

    suspend fun current(): Int = store.data.first()[key] ?: 0
    fun dataStore(): DataStore<Preferences> = store
    fun activeCollectors(): Int = collectors.get()
    fun collectionContext(owner: LifecycleOwner): CoroutineContext = owner.lifecycleScope.coroutineContext
    fun dispatch(listener: ChangeListener, owner: LifecycleOwner, value: Int) = listener.changed(value, owner)

    companion object {
        private var instance: SettingsRepository? = null
        @JvmStatic
        @Synchronized
        fun open(context: Context): SettingsRepository =
            instance ?: SettingsRepository(context.applicationContext).also { instance = it }
    }
}
