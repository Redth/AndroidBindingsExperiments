#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Android.Runtime;
using JObject = Java.Lang.Object;

namespace Java.Interop.Kotlin
{
	public sealed class JvmArray<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T> : IReadOnlyList<T?>
	{
		readonly JvmType<T> elementType;
		public JObject Peer { get; }

		public JvmArray (JObject peer, JvmType<T>? elementType = null)
		{
			ArgumentNullException.ThrowIfNull (peer);
			this.elementType = elementType ?? JvmType<T>.Default;
			using var expected = new JniType ("[" + this.elementType.Descriptor);
			if (!JniEnvironment.Types.IsInstanceOf (peer.PeerReference, expected.PeerReference))
				throw new ArgumentException ("Expected a compatible JVM reference array.", nameof (peer));
			Peer = peer;
		}

		public int Count => JniEnvironment.Arrays.GetArrayLength (Peer.PeerReference);

		public T? this [int index] {
			get {
				if ((uint) index >= (uint) Count)
					throw new ArgumentOutOfRangeException (nameof (index));
				var value = JniEnvironment.Arrays.GetObjectArrayElement (Peer.PeerReference, index);
				return elementType.Read (ref value, JniObjectReferenceOptions.CopyAndDispose);
			}
		}

		public static JvmType<JvmArray<T>> GetJvmType (JvmType<T>? elementType = null)
		{
			var actual = elementType ?? JvmType<T>.Default;
			return new JvmType<JvmArray<T>> ("[" + actual.Descriptor,
				peer => peer == null ? null : new JvmArray<T> (peer, actual), value => value?.Peer);
		}

		public static JvmArray<T> Create (IReadOnlyList<T?> values, JvmType<T>? elementType = null)
		{
			ArgumentNullException.ThrowIfNull (values);
			var actual = elementType ?? JvmType<T>.Default;
			using var elementClass = new JniType (actual.NativeName);
			var reference = JniEnvironment.Arrays.NewObjectArray (values.Count, elementClass.PeerReference, default);
			try {
				for (var i = 0; i < values.Count; i++) {
					using var argument = actual.CreateArgument (values [i]);
					JniEnvironment.Arrays.SetObjectArrayElement (reference, i, argument.Reference);
				}
				var peer = JObject.GetObject<JObject> (reference.Handle, JniHandleOwnership.DoNotTransfer) ??
					throw new InvalidOperationException ("The JVM did not return the created array.");
				return new JvmArray<T> (peer, actual);
			} finally {
				JniObjectReference.Dispose (ref reference);
			}
		}

		public IEnumerator<T?> GetEnumerator ()
		{
			for (var i = 0; i < Count; i++)
				yield return this [i];
		}

		IEnumerator IEnumerable.GetEnumerator () => GetEnumerator ();
	}
}
