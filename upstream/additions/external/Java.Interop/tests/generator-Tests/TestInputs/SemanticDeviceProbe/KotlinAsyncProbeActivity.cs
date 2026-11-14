#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using Android.Runtime;
using Java.Interop.Kotlin;
using Net.Dot.Android.Kotlinasync;
using OperationCanceledException = System.OperationCanceledException;

namespace BindingCompilerSpike
{
	[Activity (Label = "Kotlin async binding spike", MainLauncher = true, Exported = true)]
	[Register ("net/dot/android/bindingcompiler/KotlinAsyncProbeActivity")]
	public class KotlinAsyncProbeActivity : Activity
	{
		int passed;

		protected override async void OnCreate (Bundle? savedInstanceState)
		{
			base.OnCreate (savedInstanceState);
			try {
				using var fixture = new AsyncFixture ();
				await Run ("immediate suspend result", async () => Check (await fixture.ImmediateAsync () == "immediate"));
				await Run ("suspended result", async () => Check (await fixture.DelayedAsync () == "delayed"));
				await Run ("boxed primitive results", async () => Check (await fixture.NumberAsync () == 42 && await fixture.BooleanValueAsync ()));
				await Run ("remaining primitive results", async () => Check (
					await fixture.LongValueAsync () == 9223372036854775806L && await fixture.DoubleValueAsync () == 123.5 &&
					await fixture.FloatValueAsync () == 12.5F && await fixture.ShortValueAsync () == -32000 &&
					await fixture.ByteValueAsync () == -120 && await fixture.CharValueAsync () == 'K'));
				await Run ("nullable results", async () => Check (await fixture.NullableTextAsync () == null && await fixture.NullableNumberAsync () == null && await fixture.NullableBooleanAsync () == null));
				await Run ("Unit becomes Task", () => fixture.UnitAsync ());
				await Run ("immediate native failure", () => ExpectFailure (fixture.FailImmediatelyAsync (), "native-immediate-failure"));
				await Run ("suspended native failure", () => ExpectFailure (fixture.FailAsync (), "native-suspend-failure"));
				await Run ("native cancellation becomes a cancelled Task", () => ExpectCancellation (fixture.CancelItselfAsync ()));
				await Run ("pre-cancelled call never starts", async () => {
					var calls = fixture.Calls.Get ();
					using var cancellation = new CancellationTokenSource ();
					cancellation.Cancel ();
					await ExpectCancellation (fixture.CancellableAsync (cancellation.Token));
					Check (fixture.Calls.Get () == calls);
				});
				await Run ("cancellation awaits native finally", async () => {
					var cleanups = fixture.Cleanups.Get ();
					using var cancellation = new CancellationTokenSource ();
					var task = fixture.CancellableAsync (cancellation.Token);
					cancellation.Cancel ();
					await ExpectCancellation (task);
					Check (fixture.Cleanups.Get () == cleanups + 1);
				});
				await Run ("concurrent cross-thread cancellation", async () => {
					var cleanups = fixture.Cleanups.Get ();
					await Task.WhenAll (Enumerable.Range (0, 32).Select (async _ => {
						using var cancellation = new CancellationTokenSource ();
						var task = fixture.CancellableAsync (cancellation.Token);
						await Task.Run (cancellation.Cancel);
						await ExpectCancellation (task);
					}));
					Check (fixture.Cleanups.Get () == cleanups + 32);
				});
				await Run ("cancellation races with immediate completion", async () => {
					for (var iteration = 0; iteration < 64; iteration++) {
						using var cancellation = new CancellationTokenSource ();
						var cancel = Task.Run (cancellation.Cancel);
						var task = fixture.ImmediateAsync (cancellation.Token);
						try {
							Check (await task == "immediate");
						} catch (OperationCanceledException) {
							Check (task.IsCanceled);
						}
						await cancel;
					}
				});
				await Run ("suspend result retains the caller's canonical peer", async () => {
					using var value = new Java.Lang.String ("owned-peer");
					var task = fixture.EchoAsync (value);
					GC.Collect ();
					GC.WaitForPendingFinalizers ();
					var result = await task;
					Check (ReferenceEquals (value, result) && value.Handle != IntPtr.Zero);
				});
				await Run ("explicit Kotlin dispatcher survives suspension", async () => {
					using var context = fixture.DefaultContext ();
					var name = await fixture.DispatcherNameAsync (coroutineContext: context);
					Check (name.Contains ("DefaultDispatcher-worker", StringComparison.Ordinal));
				});
				await Run ("native parent Job cancellation awaits cleanup", async () => {
					var cleanups = fixture.Cleanups.Get ();
					using var context = fixture.ParentContext ();
					var task = fixture.CancellableAsync (coroutineContext: context);
					fixture.CancelContext (context);
					await ExpectCancellation (task);
					Check (fixture.Cleanups.Get () == cleanups + 1);
				});
				await Run ("Task failure does not cancel the caller's native Job", async () => {
					using var context = fixture.ParentContext ();
					try {
						await ExpectFailure (fixture.FailAsync (coroutineContext: context), "native-suspend-failure");
						Check (await fixture.ImmediateAsync (coroutineContext: context) == "immediate");
					} finally {
						fixture.CancelContext (context);
					}
				});
				await Run ("Flow is lazy repeatable ordered and preserves null", async () => {
					var collections = fixture.Collections.Get ();
					var cleanups = fixture.Cleanups.Get ();
					using var flow = fixture.Finite ();
					var stream = flow.AsAsyncEnumerable (KotlinCoroutines.GetNullableString);
					Check (fixture.Collections.Get () == collections);
					for (var iteration = 0; iteration < 2; iteration++) {
						var values = new List<string?> ();
						await foreach (var value in stream)
							values.Add (value);
						Check (values.SequenceEqual (new string? [] { "first", null, "last" }));
					}
					Check (fixture.Collections.Get () == collections + 2 && fixture.Cleanups.Get () == cleanups + 2);
				});
				await Run ("Flow backpressure and early break await cleanup", async () => {
					var emissions = fixture.Emissions.Get ();
					var cleanups = fixture.Cleanups.Get ();
					using var flow = fixture.Infinite ();
					await foreach (var value in flow.AsAsyncEnumerable (KotlinCoroutines.GetInt32)) {
						Check (value == 1);
						await Task.Delay (20);
						Check (fixture.Emissions.Get () - emissions == 1);
						break;
					}
					Check (fixture.Cleanups.Get () == cleanups + 1);
				});
				await Run ("Flow failure reaches consumer", async () => {
					using var flow = fixture.Failing ();
					var values = new List<string> ();
					try {
						await foreach (var value in flow.AsAsyncEnumerable (KotlinCoroutines.GetString))
							values.Add (value);
						throw new InvalidOperationException ("Expected a Flow failure.");
					} catch (Java.Lang.IllegalStateException ex) when (ex.Message?.Contains ("native-flow-failure", StringComparison.Ordinal) == true) {
						Check (values.SequenceEqual (new [] { "before-error" }));
					}
				});
				await Run ("Flow cancellation interrupts a waiting producer", async () => {
					var cleanups = fixture.Cleanups.Get ();
					using var flow = fixture.Waiting ();
					using var cancellation = new CancellationTokenSource ();
					cancellation.CancelAfter (20);
					var cancelled = false;
					try {
						await foreach (var value in flow.AsAsyncEnumerable (KotlinCoroutines.GetInt32, cancellation.Token))
							throw new InvalidOperationException ("Waiting Flow emitted a value.");
					} catch (OperationCanceledException) {
						cancelled = true;
					}
					Check (cancelled && fixture.Cleanups.Get () == cleanups + 1);
				});
				await Run ("Flow parent cancellation joins native cleanup", async () => {
					var cleanups = fixture.Cleanups.Get ();
					using var flow = fixture.Waiting ();
					using var context = fixture.ParentContext ();
					await using var enumerator = flow.AsAsyncEnumerable (KotlinCoroutines.GetInt32, coroutineContext: context).GetAsyncEnumerator ();
					var task = enumerator.MoveNextAsync ().AsTask ();
					fixture.CancelContext (context);
					await ExpectCancellation (task);
					Check (fixture.Cleanups.Get () == cleanups + 1);
				});
				await Run ("consumer conversion failure cancels native collection", async () => {
					var cleanups = fixture.Cleanups.Get ();
					using var flow = fixture.Infinite ();
					try {
						await foreach (var value in flow.AsAsyncEnumerable<int> (_ => throw new FormatException ("managed-conversion")))
							throw new InvalidOperationException ("Conversion should fail.");
						throw new InvalidOperationException ("Expected conversion failure.");
					} catch (FormatException ex) when (ex.Message == "managed-conversion") {
						Check (fixture.Cleanups.Get () == cleanups + 1);
					}
				});
				await Run ("StateFlow retains its current-value behavior", async () => {
					using var state = fixture.State ();
					await foreach (var value in state.AsAsyncEnumerable (KotlinCoroutines.GetInt32)) {
						Check (value == 7);
						break;
					}
				});
				await Run ("Flow values do not dispose existing peers", async () => {
					using var value = new Java.Lang.String ("owned-peer");
					using var flow = fixture.EchoFlow (value);
					var values = new List<Java.Lang.Object?> ();
					await foreach (var item in flow.AsAsyncEnumerable ())
						values.Add (item);
					Check (values.Count == 3 && ReferenceEquals (values [0], value) &&
						values [1] == null && ReferenceEquals (values [2], value) && value.Handle != IntPtr.Zero);
				});
				await AdditionalChecks (fixture);
				Log ("DONE " + passed);
			} catch (Exception ex) {
				Log ("FAIL " + ex);
			}
		}

		protected virtual Task AdditionalChecks (AsyncFixture fixture) => Task.CompletedTask;

		protected async Task Run (string name, Func<Task> check, int timeoutSeconds = 15)
		{
			await check ().WaitAsync (TimeSpan.FromSeconds (timeoutSeconds));
			passed++;
			Log ("PASS " + name);
		}

		protected static async Task ExpectFailure (Task task, string message)
		{
			try {
				await task;
			} catch (Java.Lang.IllegalStateException ex) when (ex.Message?.Contains (message, StringComparison.Ordinal) == true) {
				return;
			}
			throw new InvalidOperationException ("Expected Kotlin failure: " + message);
		}

		protected static async Task ExpectCancellation (Task task)
		{
			try {
				await task;
			} catch (OperationCanceledException) {
				Check (task.IsCanceled);
				return;
			}
			throw new InvalidOperationException ("Expected a cancelled Task.");
		}

		protected static void Check (bool condition)
		{
			if (!condition)
				throw new InvalidOperationException ("Kotlin async contract assertion failed.");
		}

		protected static void Log (string message) => Android.Util.Log.Info ("BindingCompilerSpike", ProbeRunIdentity.Value + " " + message);
	}
}
