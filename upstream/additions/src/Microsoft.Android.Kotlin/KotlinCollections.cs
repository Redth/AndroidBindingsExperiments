#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Android.Runtime;
using JObject = Java.Lang.Object;

namespace Java.Interop.Kotlin
{
	public sealed class KotlinList<T> : IReadOnlyList<T>
	{
		readonly Func<JObject?, T> convert;
		public JObject Peer { get; }

		public KotlinList (JObject peer, Func<JObject?, T> convert)
		{
			ArgumentNullException.ThrowIfNull (convert);
			NativeCollections.Validate (peer, NativeCollections.List);
			Peer = peer;
			this.convert = convert;
		}

		public unsafe int Count => NativeCollections.List.InstanceMethods.InvokeAbstractInt32Method ("size.()I", Peer, null);

		public unsafe T this [int index] {
			get {
				var argument = new JniArgumentValue (index);
				var result = NativeCollections.List.InstanceMethods.InvokeAbstractObjectMethod ("get.(I)Ljava/lang/Object;", Peer, &argument);
				return convert (JObject.GetObject<JObject> (result.Handle, JniHandleOwnership.TransferLocalRef));
			}
		}

		public IEnumerator<T> GetEnumerator ()
		{
			foreach (var value in NativeCollections.Iterate (Peer, NativeCollections.List))
				yield return convert (value);
		}

		IEnumerator IEnumerable.GetEnumerator () => GetEnumerator ();
	}

	public sealed class KotlinSet<T> : IReadOnlyCollection<T>
	{
		readonly Func<JObject?, T> convert;
		public JObject Peer { get; }

		public KotlinSet (JObject peer, Func<JObject?, T> convert)
		{
			ArgumentNullException.ThrowIfNull (convert);
			NativeCollections.Validate (peer, NativeCollections.Set);
			Peer = peer;
			this.convert = convert;
		}

		public unsafe int Count => NativeCollections.Set.InstanceMethods.InvokeAbstractInt32Method ("size.()I", Peer, null);

		public IEnumerator<T> GetEnumerator ()
		{
			foreach (var value in NativeCollections.Iterate (Peer, NativeCollections.Set))
				yield return convert (value);
		}

		IEnumerator IEnumerable.GetEnumerator () => GetEnumerator ();
	}

	// Java maps allow null keys; IReadOnlyDictionary's non-null key contract
	// cannot represent every Kotlin Map. Preserve typed entries and the peer.
	public sealed class KotlinMap<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
	{
		readonly Func<JObject?, TKey> key;
		readonly Func<JObject?, TValue> value;
		public JObject Peer { get; }

		public KotlinMap (JObject peer, Func<JObject?, TKey> key, Func<JObject?, TValue> value)
		{
			ArgumentNullException.ThrowIfNull (key);
			ArgumentNullException.ThrowIfNull (value);
			NativeCollections.Validate (peer, NativeCollections.Map);
			Peer = peer;
			this.key = key;
			this.value = value;
		}

		public unsafe int Count => NativeCollections.Map.InstanceMethods.InvokeAbstractInt32Method ("size.()I", Peer, null);

		public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator ()
		{
			var entries = NativeCollections.GetObject (NativeCollections.Map, "entrySet.()Ljava/util/Set;", Peer);
			foreach (var entry in NativeCollections.Iterate (entries, NativeCollections.Set)) {
				var actual = KotlinCoroutines.Require (entry);
				yield return new KeyValuePair<TKey, TValue> (
					key (NativeCollections.GetNullableObject (NativeCollections.Entry, "getKey.()Ljava/lang/Object;", actual)),
					value (NativeCollections.GetNullableObject (NativeCollections.Entry, "getValue.()Ljava/lang/Object;", actual)));
			}
		}

		IEnumerator IEnumerable.GetEnumerator () => GetEnumerator ();
	}

	internal static class NativeCollections
	{
		internal static readonly JniPeerMembers List = new JniPeerMembers ("java/util/List", typeof (JObject));
		internal static readonly JniPeerMembers Set = new JniPeerMembers ("java/util/Set", typeof (JObject));
		internal static readonly JniPeerMembers Map = new JniPeerMembers ("java/util/Map", typeof (JObject));
		internal static readonly JniPeerMembers Entry = new JniPeerMembers ("java/util/Map$Entry", typeof (JObject));
		static readonly JniPeerMembers Iterator = new JniPeerMembers ("java/util/Iterator", typeof (JObject));

		internal static void Validate (JObject peer, JniPeerMembers members)
		{
			ArgumentNullException.ThrowIfNull (peer);
			if (!JniEnvironment.Types.IsInstanceOf (peer.PeerReference, members.JniPeerType.PeerReference))
				throw new ArgumentException ("Expected " + members.JniPeerTypeName + ".", nameof (peer));
		}

		internal static unsafe JObject? GetNullableObject (JniPeerMembers members, string method, JObject peer)
		{
			var result = members.InstanceMethods.InvokeAbstractObjectMethod (method, peer, null);
			return JObject.GetObject<JObject> (result.Handle, JniHandleOwnership.TransferLocalRef);
		}

		internal static JObject GetObject (JniPeerMembers members, string method, JObject peer) =>
			KotlinCoroutines.Require (GetNullableObject (members, method, peer));

		internal static IEnumerable<JObject?> Iterate (JObject peer, JniPeerMembers members)
		{
			var iterator = GetObject (members, "iterator.()Ljava/util/Iterator;", peer);
			while (HasNext (iterator))
				yield return GetNullableObject (Iterator, "next.()Ljava/lang/Object;", iterator);
			GC.KeepAlive (peer);
		}

		static unsafe bool HasNext (JObject peer) => Iterator.InstanceMethods.InvokeAbstractBooleanMethod ("hasNext.()Z", peer, null);
	}

	public static partial class KotlinCoroutines
	{
		public static JvmType<KotlinList<T?>> GetListType<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T> (JvmType<T> element) =>
			new JvmType<KotlinList<T?>> ("java/util/List",
				peer => peer == null ? null : new KotlinList<T?> (peer, element.Read), value => value?.Peer);

		public static JvmType<KotlinSet<T?>> GetSetType<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T> (JvmType<T> element) =>
			new JvmType<KotlinSet<T?>> ("java/util/Set",
				peer => peer == null ? null : new KotlinSet<T?> (peer, element.Read), value => value?.Peer);

		public static JvmType<KotlinMap<TKey?, TValue?>> GetMapType<
			[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] TKey,
			[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] TValue> (JvmType<TKey> key, JvmType<TValue> value) =>
			new JvmType<KotlinMap<TKey?, TValue?>> ("java/util/Map",
				peer => peer == null ? null : new KotlinMap<TKey?, TValue?> (peer, key.Read, value.Read), map => map?.Peer);

		public static KotlinList<T> GetList<T> (JObject? peer, Func<JObject?, T> convert) => new KotlinList<T> (Require (peer), convert);
		public static KotlinList<T>? GetNullableList<T> (JObject? peer, Func<JObject?, T> convert) => peer == null ? null : GetList (peer, convert);
		public static KotlinSet<T> GetSet<T> (JObject? peer, Func<JObject?, T> convert) => new KotlinSet<T> (Require (peer), convert);
		public static KotlinSet<T>? GetNullableSet<T> (JObject? peer, Func<JObject?, T> convert) => peer == null ? null : GetSet (peer, convert);
		public static KotlinMap<TKey, TValue> GetMap<TKey, TValue> (JObject? peer, Func<JObject?, TKey> key, Func<JObject?, TValue> value) =>
			new KotlinMap<TKey, TValue> (Require (peer), key, value);
		public static KotlinMap<TKey, TValue>? GetNullableMap<TKey, TValue> (JObject? peer, Func<JObject?, TKey> key, Func<JObject?, TValue> value) =>
			peer == null ? null : GetMap (peer, key, value);
	}
}
