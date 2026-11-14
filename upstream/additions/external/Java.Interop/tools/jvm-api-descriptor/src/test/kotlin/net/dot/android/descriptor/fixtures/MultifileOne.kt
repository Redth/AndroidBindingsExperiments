@file:JvmName("MultifileApi")
@file:JvmMultifileClass

package net.dot.android.descriptor.fixtures

internal fun internalFacadeValue(): String = "internal"
@JvmName("publicFacadeValue")
private fun privateFacadeValue(): String = "private"
fun privateValueThroughFacade(): String = privateFacadeValue()
