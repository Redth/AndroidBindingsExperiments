#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;

namespace Java.Interop.Kotlin
{
	public readonly struct KotlinOptional<T>
	{
		readonly (bool Specified, T Value) state;

		public KotlinOptional (T value) => state = (true, value);

		public bool IsSpecified => state.Specified;

		public T Value => state.Specified ? state.Value : throw new InvalidOperationException ("The Kotlin argument was omitted.");

		[return: MaybeNull]
		public T GetValueOrDefault () => state.Value;

		public static implicit operator KotlinOptional<T> (T value) => new KotlinOptional<T> (value);
	}
}
