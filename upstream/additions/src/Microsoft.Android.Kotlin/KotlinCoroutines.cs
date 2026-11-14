#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Native = Net.Dot.Android.Kotlin;
using JObject = Java.Lang.Object;

namespace Java.Interop.Kotlin
{
	public static partial class KotlinCoroutines
	{
		public static async Task<T> RunAsync<T> (Func<JObject, JObject?> invoke, Func<JObject?, T> convert, CancellationToken cancellationToken = default, IJavaPeerable? coroutineContext = null)
		{
			ArgumentNullException.ThrowIfNull (invoke);
			ArgumentNullException.ThrowIfNull (convert);
			cancellationToken.ThrowIfCancellationRequested ();
			using var callback = new SuspendCallback (invoke, cancellationToken);
			using var operation = NativeRuntime.Create (callback, callback, coroutineContext);
			using var registration = cancellationToken.Register (operation.Cancel);
			operation.Start ();
			return convert (await callback.Task.ConfigureAwait (false));
		}

		public static Task RunUnitAsync (Func<JObject, JObject?> invoke, CancellationToken cancellationToken = default, IJavaPeerable? coroutineContext = null) =>
			RunAsync (invoke, value => Native.CoroutineBridge.IsUnit (value) ? true :
				throw new InvalidOperationException ("Kotlin returned a non-Unit result for a Unit operation."), cancellationToken, coroutineContext);

		public static T GetPeer<T> (JObject? value) where T : class, IJavaObject => Require (value).JavaCast<T> ();
		public static T? GetNullablePeer<T> (JObject? value) where T : class, IJavaObject => value == null ? null : value.JavaCast<T> ();

		public static IAsyncEnumerable<JObject?> AsAsyncEnumerable (this IJavaPeerable flow, CancellationToken cancellationToken = default, IJavaPeerable? coroutineContext = null) =>
			AsAsyncEnumerable (flow, value => value, cancellationToken, coroutineContext);

		public static IAsyncEnumerable<T> AsAsyncEnumerable<T> (this IJavaPeerable flow, Func<JObject?, T> convert, CancellationToken cancellationToken = default, IJavaPeerable? coroutineContext = null)
		{
			ArgumentNullException.ThrowIfNull (flow);
			ArgumentNullException.ThrowIfNull (convert);
			return new FlowEnumerable<T> (flow, convert, cancellationToken, coroutineContext);
		}

		internal static JObject Require (JObject? value) =>
			value ?? throw new InvalidOperationException ("Kotlin returned null for a non-null result.");

		internal static Exception GetException (Java.Lang.Throwable failure)
		{
			var reference = failure.PeerReference;
			return JniEnvironment.Runtime.GetExceptionForThrowable (ref reference, JniObjectReferenceOptions.Copy)
				?? throw new InvalidOperationException ("Kotlin supplied an invalid exception reference.");
		}

		public static string GetString (JObject? value) => Require (value).JavaCast<Java.Lang.String> ().ToString ();
		public static string? GetNullableString (JObject? value) => value == null ? null : GetString (value);
		public static int GetInt32 (JObject? value) => Require (value).JavaCast<Java.Lang.Integer> ().IntValue ();
		public static int? GetNullableInt32 (JObject? value) => value == null ? null : GetInt32 (value);
		public static bool GetBoolean (JObject? value) => Require (value).JavaCast<Java.Lang.Boolean> ().BooleanValue ();
		public static bool? GetNullableBoolean (JObject? value) => value == null ? null : GetBoolean (value);
		public static long GetInt64 (JObject? value) => Require (value).JavaCast<Java.Lang.Long> ().LongValue ();
		public static long? GetNullableInt64 (JObject? value) => value == null ? null : GetInt64 (value);
		public static double GetDouble (JObject? value) => Require (value).JavaCast<Java.Lang.Double> ().DoubleValue ();
		public static double? GetNullableDouble (JObject? value) => value == null ? null : GetDouble (value);
		public static float GetSingle (JObject? value) => Require (value).JavaCast<Java.Lang.Float> ().FloatValue ();
		public static float? GetNullableSingle (JObject? value) => value == null ? null : GetSingle (value);
		public static short GetInt16 (JObject? value) => Require (value).JavaCast<Java.Lang.Short> ().ShortValue ();
		public static short? GetNullableInt16 (JObject? value) => value == null ? null : GetInt16 (value);
		public static sbyte GetSByte (JObject? value) => Require (value).JavaCast<Java.Lang.Byte> ().ByteValue ();
		public static sbyte? GetNullableSByte (JObject? value) => value == null ? null : GetSByte (value);
		public static char GetChar (JObject? value) => Require (value).JavaCast<Java.Lang.Character> ().CharValue ();
		public static char? GetNullableChar (JObject? value) => value == null ? null : GetChar (value);

		sealed class FlowEnumerable<T> : IAsyncEnumerable<T>
		{
			readonly IJavaPeerable flow;
			readonly Func<JObject?, T> convert;
			readonly CancellationToken token;
			readonly IJavaPeerable? context;

			public FlowEnumerable (IJavaPeerable flow, Func<JObject?, T> convert, CancellationToken token, IJavaPeerable? context)
			{
				this.flow = flow;
				this.convert = convert;
				this.token = token;
				this.context = context;
			}

			public IAsyncEnumerator<T> GetAsyncEnumerator (CancellationToken cancellationToken = default)
			{
				CancellationTokenSource? linked = null;
				if (token.CanBeCanceled && cancellationToken.CanBeCanceled && token != cancellationToken)
					linked = CancellationTokenSource.CreateLinkedTokenSource (token, cancellationToken);
				var actual = linked?.Token ?? (cancellationToken.CanBeCanceled ? cancellationToken : token);
				try {
					return new DemandAsyncEnumerator<JObject?, T> (convert,
						(item, completed) => new FlowSubscription (flow, context, item, completed, linked), actual);
				} catch {
					linked?.Dispose ();
					throw;
				}
			}
		}

		sealed class FlowSubscription : IDemandSubscription
		{
			readonly FlowCallback callback;
			readonly Native.NativeFlow flow;
			readonly CancellationTokenSource? linked;

			public FlowSubscription (IJavaPeerable source, IJavaPeerable? context, Action<JObject?> item, Action<Exception?, bool> completed, CancellationTokenSource? linked)
			{
				callback = new FlowCallback (item, completed);
				try {
					flow = NativeRuntime.Subscribe (source, callback, context);
				} catch {
					callback.Dispose ();
					throw;
				}
				this.linked = linked;
			}

			public void Request () => flow.Request ();
			public void Cancel () => flow.Cancel ();

			public void Dispose ()
			{
				flow.Dispose ();
				callback.Dispose ();
				linked?.Dispose ();
			}
		}
	}

	[Register ("net/dot/android/kotlin/ManagedSuspendCallback")]
	internal sealed class SuspendCallback : JObject, Native.ISuspendCall, Native.ICompletion
	{
		readonly Func<JObject, JObject?> invoke;
		readonly CancellationToken cancellationToken;
		readonly TaskCompletionSource<JObject?> completion = new TaskCompletionSource<JObject?> (TaskCreationOptions.RunContinuationsAsynchronously);

		public SuspendCallback (Func<JObject, JObject?> invoke, CancellationToken cancellationToken)
		{
			this.invoke = invoke;
			this.cancellationToken = cancellationToken;
		}

		public Task<JObject?> Task => completion.Task;
		public JObject? Invoke (JObject continuation) => invoke (continuation);

		public void Completed (JObject? value, Java.Lang.Throwable? failure, bool cancelled)
		{
			if (cancelled)
				completion.TrySetCanceled (cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken (true));
			else if (failure != null)
				completion.TrySetException (KotlinCoroutines.GetException (failure));
			else
				completion.TrySetResult (value);
		}
	}

	[Register ("net/dot/android/kotlin/ManagedFlowCallback")]
	internal sealed class FlowCallback : JObject, Native.IFlowCallback
	{
		readonly Action<JObject?> item;
		readonly Action<Exception?, bool> completed;

		public FlowCallback (Action<JObject?> item, Action<Exception?, bool> completed)
		{
			this.item = item;
			this.completed = completed;
		}

		public void Item (JObject? value) => item (value);
		public void Completed (Java.Lang.Throwable? failure, bool cancelled) =>
			completed (failure == null ? null : KotlinCoroutines.GetException (failure), cancelled);
	}
}
