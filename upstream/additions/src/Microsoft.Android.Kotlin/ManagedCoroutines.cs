#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using Native = Net.Dot.Android.Kotlin;
using JObject = Java.Lang.Object;

namespace Java.Interop.Kotlin
{
	public static partial class KotlinCoroutines
	{
		public static JObject? FromTask<T> (IJavaPeerable continuation, Func<CancellationToken, Task<T>> implementation, Func<T, JObject?> box)
		{
			ArgumentNullException.ThrowIfNull (implementation);
			ArgumentNullException.ThrowIfNull (box);
			return FromTask (continuation, async token => box (await implementation (token).ConfigureAwait (false)));
		}

		public static JObject? FromTask (IJavaPeerable continuation, Func<CancellationToken, Task> implementation)
		{
			ArgumentNullException.ThrowIfNull (implementation);
			return FromTask (continuation, async token => {
				await implementation (token).ConfigureAwait (false);
				return Native.CoroutineBridge.UnitValue ();
			});
		}

		public static JObject? FromTask (IJavaPeerable continuation, Func<CancellationToken, Task<JObject?>> implementation)
		{
			ArgumentNullException.ThrowIfNull (continuation);
			ArgumentNullException.ThrowIfNull (implementation);
			var callback = new ManagedCallCallback (implementation);
			try {
				return NativeRuntime.FromManaged (callback, continuation);
			} catch {
				callback.ReleaseWithoutStarting ();
				throw;
			}
		}
	}

	[Register ("net/dot/android/kotlin/ManagedCallCallback")]
	internal sealed class ManagedCallCallback : JObject, Native.IManagedCall
	{
		readonly object gate = new object ();
		readonly CancellationTokenSource cancellation = new CancellationTokenSource ();
		readonly Func<CancellationToken, Task<JObject?>> implementation;
		Task cancellationCallbacks = Task.CompletedTask;
		bool released;
		bool started;

		public ManagedCallCallback (Func<CancellationToken, Task<JObject?>> implementation) => this.implementation = implementation;

		public void Start (Native.ManagedCompletion completion)
		{
			lock (gate) {
				if (started || released)
					throw new InvalidOperationException ("A managed suspend operation can be started only once.");
				started = true;
			}
			_ = Observe (CompleteAsync (completion));
		}

		public void Cancel ()
		{
			lock (gate) {
				if (!released)
					cancellationCallbacks = cancellation.CancelAsync ();
			}
		}

		async Task CompleteAsync (Native.ManagedCompletion completion)
		{
			try {
				try {
					var value = await implementation (cancellation.Token).ConfigureAwait (false);
					completion.Succeed (value);
					GC.KeepAlive (value);
				} catch (OperationCanceledException) {
					completion.Cancel ();
				} catch (Exception ex) {
					completion.Fail (Java.Lang.Throwable.FromException (ex));
				}
			} finally {
				Task callbacks;
				lock (gate) {
					released = true;
					callbacks = cancellationCallbacks;
				}
				try {
					await callbacks.ConfigureAwait (false);
				} finally {
					completion.Dispose ();
					cancellation.Dispose ();
					Dispose ();
				}
			}
		}

		static async Task Observe (Task completion)
		{
			try {
				await completion.ConfigureAwait (false);
			} catch (Exception ex) {
				Android.Util.Log.Error ("AndroidKotlinRuntime", "Managed coroutine bridge completion failed: " + ex);
			}
		}

		public void ReleaseWithoutStarting ()
		{
			lock (gate) {
				if (started || released)
					return;
				released = true;
			}
			cancellation.Dispose ();
			Dispose ();
		}
	}
}
