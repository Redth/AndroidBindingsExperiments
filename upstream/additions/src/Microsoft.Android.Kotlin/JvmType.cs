#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using Android.Runtime;
using JObject = Java.Lang.Object;

namespace Java.Interop.Kotlin
{
	public sealed class JvmType<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>
	{
		static readonly Lazy<JvmType<T>> defaultType = new Lazy<JvmType<T>> (() => new JvmType<T> ());
		readonly Lazy<JniType> nativeType;
		readonly Func<JObject?, T?>? read;
		readonly Func<T?, IJavaPeerable?>? write;

		public static JvmType<T> Default => defaultType.Value;
		public string NativeName { get; }
		public string Descriptor => NativeName [0] == '[' ? NativeName : "L" + NativeName + ";";

		JvmType ()
		{
			var type = Nullable.GetUnderlyingType (typeof (T)) ?? typeof (T);
			var signature = JniEnvironment.Runtime.TypeManager.GetTypeSignature (type);
			if (!signature.IsValid)
				throw new NotSupportedException ($"No default JNI type for '{typeof (T)}'. Supply an explicit JvmType converter.");
			NativeName = (signature.ArrayRank == 0 ? signature.GetPrimitivePeerTypeSignature () : signature).Name;
			nativeType = new Lazy<JniType> (() => new JniType (NativeName));
		}

		public JvmType (string nativeName, Func<JObject?, T?> read, Func<T?, IJavaPeerable?> write)
		{
			ArgumentException.ThrowIfNullOrEmpty (nativeName);
			ArgumentNullException.ThrowIfNull (read);
			ArgumentNullException.ThrowIfNull (write);
			if (nativeName.Contains ('.') || nativeName [0] == 'L' && nativeName.EndsWith (';'))
				throw new ArgumentException ("Expected a JNI class name or array descriptor.", nameof (nativeName));
			NativeName = nativeName;
			nativeType = new Lazy<JniType> (() => new JniType (NativeName));
			this.read = read;
			this.write = write;
		}

		public void RequireAssignableTo (string nativeBound)
		{
			using var bound = new JniType (nativeBound);
			if (!JniEnvironment.Types.IsAssignableFrom (nativeType.Value.PeerReference, bound.PeerReference))
				throw new ArgumentException ($"JNI type '{NativeName}' does not satisfy generic bound '{nativeBound}'.");
		}

		internal void Validate (JniObjectReference reference)
		{
			if (reference.IsValid && !JniEnvironment.Types.IsInstanceOf (reference, nativeType.Value.PeerReference))
				throw new InvalidCastException ($"The JNI object does not implement '{NativeName}' required by '{typeof (T)}'.");
		}

		public T? Read (JObject? peer)
		{
			if (peer != null && !peer.PeerReference.IsValid)
				throw new ObjectDisposedException (peer.GetType ().FullName);
			var reference = peer?.PeerReference ?? default;
			return Read (ref reference, JniObjectReferenceOptions.Copy);
		}

		public T ReadRequired (JObject? peer)
		{
			var value = Read (peer);
			if (value is null)
				throw new InvalidOperationException ("The JNI result violates its non-null contract.");
			return value;
		}

		public T? Read (ref JniObjectReference reference, JniObjectReferenceOptions ownership)
		{
			if (ownership != JniObjectReferenceOptions.Copy && ownership != JniObjectReferenceOptions.CopyAndDispose)
				throw new ArgumentOutOfRangeException (nameof (ownership));
			try {
				if (!reference.IsValid) {
					if (default (T) is not null)
						throw new InvalidCastException ($"A null JNI reference cannot be read as non-nullable '{typeof (T)}'.");
					return default;
				}
				Validate (reference);
				if (read == null)
					return JniEnvironment.Runtime.ValueManager.GetValue<T> (ref reference, JniObjectReferenceOptions.Copy);
				var peer = JObject.GetObject<JObject> (reference.Handle, JniHandleOwnership.DoNotTransfer);
				return read (peer);
			} finally {
				if (ownership == JniObjectReferenceOptions.CopyAndDispose)
					JniObjectReference.Dispose (ref reference);
			}
		}

		public JvmArgument<T> CreateArgument (T? value)
		{
			if (value is null)
				return new JvmArgument<T> ((IJavaPeerable?) null);
			if (value is IJavaPeerable disposed && !disposed.PeerReference.IsValid)
				throw new ObjectDisposedException (disposed.GetType ().FullName);
			if (write == null) {
				var reference = JniEnvironment.Runtime.ValueManager.CreateLocalObjectReferenceArgument (typeof (T), value);
				var transferred = false;
				try {
					Validate (reference);
					var argument = new JvmArgument<T> (reference);
					transferred = true;
					return argument;
				} finally {
					if (!transferred)
						JniObjectReference.Dispose (ref reference);
				}
			}
			var peer = write (value);
			if (peer != null && !peer.PeerReference.IsValid)
				throw new ObjectDisposedException (peer.GetType ().FullName);
			Validate (peer?.PeerReference ?? default);
			return new JvmArgument<T> (peer);
		}
	}

	public struct JvmArgument<T> : IDisposable
	{
		JniObjectReference reference;
		IJavaPeerable? peer;

		internal JvmArgument (JniObjectReference reference)
		{
			this.reference = reference;
			peer = null;
		}

		internal JvmArgument (IJavaPeerable? peer)
		{
			reference = default;
			this.peer = peer;
		}

		public JniObjectReference Reference => peer?.PeerReference ?? reference;
		public JniArgumentValue Value => new JniArgumentValue (Reference);

		public void Dispose ()
		{
			JniObjectReference.Dispose (ref reference);
			GC.KeepAlive (peer);
			peer = null;
		}
	}
}
