#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Kotlin.Coroutines;
using Xamarin.KotlinX.Coroutines.Flow;
using Net.Dot.Android.Kotlinasync;
using JObject = Java.Lang.Object;

namespace Java.Interop.Kotlin
{
	public static class KotlinCoroutines
	{
		public static async Task<T> RunAsync<T> (Func<IContinuation, JObject?> invoke, Func<JObject?, T> convert, CancellationToken cancellationToken = default, ICoroutineContext? coroutineContext = null)
		{
			ArgumentNullException.ThrowIfNull (invoke);
			ArgumentNullException.ThrowIfNull (convert);
			cancellationToken.ThrowIfCancellationRequested ();
			using var callback = new SuspendCallback (invoke, cancellationToken);
			using var operation = CoroutineBridge.Create (callback, callback, coroutineContext ?? EmptyCoroutineContext.Instance);
			using var registration = cancellationToken.Register (operation.Cancel);
			operation.Start ();
			return convert (await callback.Task.ConfigureAwait (false));
		}

		public static Task RunUnitAsync (Func<IContinuation, JObject?> invoke, CancellationToken cancellationToken = default, ICoroutineContext? coroutineContext = null) =>
			RunAsync (invoke, value => Require (value).JavaCast<global::Kotlin.Unit> (), cancellationToken, coroutineContext);

		public static IAsyncEnumerable<JObject?> AsAsyncEnumerable (this IFlow flow, CancellationToken cancellationToken = default, ICoroutineContext? coroutineContext = null) =>
			AsAsyncEnumerable (flow, value => value, cancellationToken, coroutineContext);

		public static async IAsyncEnumerable<T> AsAsyncEnumerable<T> (this IFlow flow, Func<JObject?, T> convert, [EnumeratorCancellation] CancellationToken cancellationToken = default, ICoroutineContext? coroutineContext = null)
		{
			ArgumentNullException.ThrowIfNull (flow);
			ArgumentNullException.ThrowIfNull (convert);
			cancellationToken.ThrowIfCancellationRequested ();
			using var session = CoroutineBridge.CreateFlow (flow, coroutineContext ?? EmptyCoroutineContext.Instance);
			try {
				while (true) {
					using var item = await RunAsync (session.Next, value => Require (value).JavaCast<FlowItem> (), cancellationToken).ConfigureAwait (false);
					if (!item.HasValue)
						yield break;
					yield return convert (item.Value);
				}
			} finally {
				// DisposeAsync must wait for Kotlin finally blocks, even when the
				// caller's token is cancelled or the loop exits with break.
				await RunUnitAsync (session.Close, CancellationToken.None).ConfigureAwait (false);
			}
		}

		static JObject Require (JObject? value) =>
			value ?? throw new InvalidOperationException ("Kotlin returned null for a non-null result.");

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
	}

	[Register ("net/dot/android/kotlinasync/ManagedSuspendCallback")]
	public sealed class SuspendCallback : JObject, ISuspendCall, ICompletion
	{
		readonly Func<IContinuation, JObject?> invoke;
		readonly CancellationToken cancellationToken;
		readonly TaskCompletionSource<JObject?> completion = new TaskCompletionSource<JObject?> (TaskCreationOptions.RunContinuationsAsynchronously);

		public SuspendCallback (Func<IContinuation, JObject?> invoke, CancellationToken cancellationToken)
		{
			this.invoke = invoke;
			this.cancellationToken = cancellationToken;
		}

		public Task<JObject?> Task => completion.Task;

		public JObject? Invoke (IContinuation continuation) => invoke (continuation);

		public void Completed (JObject? value, Java.Lang.Throwable? failure, bool cancelled)
		{
			if (cancelled) {
				completion.TrySetCanceled (cancellationToken.IsCancellationRequested ? cancellationToken : new CancellationToken (true));
			} else if (failure != null) {
				completion.TrySetException (failure);
			} else {
				completion.TrySetResult (value);
			}
		}
	}
}
