#nullable enable

using System;

using Java.Interop;

namespace SharedPeerViews
{
	public sealed partial class Widget
	{
		static class Methods
		{
			internal static readonly JniType CallbackType = new JniType ("java/util/function/IntUnaryOperator");
			internal static readonly JniMethodInfo SetValue = JavaType.GetInstanceMethod ("setValue", "(I)V");
			internal static readonly JniMethodInfo SetCallback = JavaType.GetInstanceMethod (
				"setCallback", "(Ljava/util/function/IntUnaryOperator;)V");
			internal static readonly JniMethodInfo InvokeCallback = JavaType.GetInstanceMethod ("invokeCallback", "(I)I");
		}

		public unsafe void SetValue (int value)
		{
			var owner = Peer;
			try {
				var argument = new JniArgumentValue (value);
				JniEnvironment.InstanceMethods.CallVoidMethod (owner.PeerReference, Methods.SetValue, &argument);
			} finally {
				GC.KeepAlive (owner);
			}
		}

		public unsafe void SetCallback (Java.Lang.Object? callback)
		{
			var owner = Peer;
			if (callback != null)
				Validate (callback, Methods.CallbackType);
			try {
				var argument = new JniArgumentValue (callback?.PeerReference ?? default);
				JniEnvironment.InstanceMethods.CallVoidMethod (owner.PeerReference, Methods.SetCallback, &argument);
			} finally {
				GC.KeepAlive (callback);
				GC.KeepAlive (owner);
			}
		}

		public unsafe int InvokeCallback (int value)
		{
			var owner = Peer;
			try {
				var argument = new JniArgumentValue (value);
				return JniEnvironment.InstanceMethods.CallIntMethod (owner.PeerReference, Methods.InvokeCallback, &argument);
			} finally {
				GC.KeepAlive (owner);
			}
		}
	}
}
