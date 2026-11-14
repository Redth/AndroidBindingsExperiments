#nullable enable
using System;
using System.Diagnostics.CodeAnalysis;
using Android.Runtime;
using Native = Net.Dot.Android.Kotlin;
using JObject = Java.Lang.Object;

namespace Java.Interop.Kotlin
{
	internal static class NativeRuntime
	{
		static readonly JniPeerMembers members = new JniPeerMembers ("net/dot/android/kotlin/CoroutineBridge", typeof (Native.CoroutineBridge));

		[DynamicDependency (DynamicallyAccessedMemberTypes.PublicMethods, typeof (Native.CoroutineBridge))]
		static NativeRuntime ()
		{
		}

		public static unsafe Native.NativeOperation Create (Native.ISuspendCall call, Native.ICompletion completion, IJavaPeerable? context)
		{
			JniArgumentValue* args = stackalloc JniArgumentValue [3];
			args [0] = new JniArgumentValue (call.PeerReference);
			args [1] = new JniArgumentValue (completion.PeerReference);
			args [2] = new JniArgumentValue (context?.PeerReference ?? default);
			try {
				var result = members.StaticMethods.InvokeObjectMethod (
					"create.(Lnet/dot/android/kotlin/SuspendCall;Lnet/dot/android/kotlin/Completion;Ljava/lang/Object;)Lnet/dot/android/kotlin/NativeOperation;", args);
				return JObject.GetObject<Native.NativeOperation> (result.Handle, JniHandleOwnership.TransferLocalRef)
					?? throw new InvalidOperationException ("The Kotlin runtime returned no operation.");
			} finally {
				GC.KeepAlive (call);
				GC.KeepAlive (completion);
				GC.KeepAlive (context);
			}
		}

		public static unsafe Native.NativeFlow Subscribe (IJavaPeerable flow, Native.IFlowCallback callback, IJavaPeerable? context)
		{
			JniArgumentValue* args = stackalloc JniArgumentValue [3];
			args [0] = new JniArgumentValue (flow.PeerReference);
			args [1] = new JniArgumentValue (callback.PeerReference);
			args [2] = new JniArgumentValue (context?.PeerReference ?? default);
			try {
				var result = members.StaticMethods.InvokeObjectMethod (
					"subscribe.(Ljava/lang/Object;Lnet/dot/android/kotlin/FlowCallback;Ljava/lang/Object;)Lnet/dot/android/kotlin/NativeFlow;", args);
				return JObject.GetObject<Native.NativeFlow> (result.Handle, JniHandleOwnership.TransferLocalRef)
					?? throw new InvalidOperationException ("The Kotlin runtime returned no subscription.");
			} finally {
				GC.KeepAlive (flow);
				GC.KeepAlive (callback);
				GC.KeepAlive (context);
			}
		}

		public static unsafe JObject? FromManaged (Native.IManagedCall call, IJavaPeerable continuation)
		{
			JniArgumentValue* args = stackalloc JniArgumentValue [2];
			args [0] = new JniArgumentValue (call.PeerReference);
			args [1] = new JniArgumentValue (continuation.PeerReference);
			try {
				var result = members.StaticMethods.InvokeObjectMethod (
					"fromManaged.(Lnet/dot/android/kotlin/ManagedCall;Ljava/lang/Object;)Ljava/lang/Object;", args);
				return JObject.GetObject<JObject> (result.Handle, JniHandleOwnership.TransferLocalRef);
			} finally {
				GC.KeepAlive (call);
				GC.KeepAlive (continuation);
			}
		}
	}
}
