#nullable enable

using System;

using Java.Interop;

namespace SharedPeerViews
{
	public sealed partial class Widget
	{
		static class Methods
		{
			internal static readonly JniMethodInfo GetValue = JavaType.GetInstanceMethod ("getValue", "()I");
			internal static readonly JniMethodInfo Echo = JavaType.GetInstanceMethod (
				"echo", "(Lnet/dot/android/test/SharedPeerFixture;)Lnet/dot/android/test/SharedPeerFixture;");
			internal static readonly JniMethodInfo GetCallback = JavaType.GetInstanceMethod (
				"getCallback", "()Ljava/util/function/IntUnaryOperator;");
		}

		public unsafe int GetValue ()
		{
			var owner = Peer;
			try {
				// Views require Java virtual dispatch, independent of the canonical peer's managed type.
				return JniEnvironment.InstanceMethods.CallIntMethod (owner.PeerReference, Methods.GetValue);
			} finally {
				GC.KeepAlive (owner);
			}
		}

		public unsafe Java.Lang.Object? Echo (Java.Lang.Object? other)
		{
			var owner = Peer;
			if (other != null)
				Validate (other, JavaType);
			try {
				var argument = new JniArgumentValue (other?.PeerReference ?? default);
				var result = JniEnvironment.InstanceMethods.CallObjectMethod (owner.PeerReference, Methods.Echo, &argument);
				return ReadPeer (ref result);
			} finally {
				GC.KeepAlive (other);
				GC.KeepAlive (owner);
			}
		}

		public unsafe Java.Lang.Object? GetCallback ()
		{
			var owner = Peer;
			try {
				var result = JniEnvironment.InstanceMethods.CallObjectMethod (owner.PeerReference, Methods.GetCallback);
				return ReadPeer (ref result);
			} finally {
				GC.KeepAlive (owner);
			}
		}
	}
}
