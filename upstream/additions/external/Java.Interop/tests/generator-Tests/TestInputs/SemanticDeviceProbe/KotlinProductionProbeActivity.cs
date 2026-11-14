#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Runtime;
using Java.Interop.Kotlin;
using Kotlin.Coroutines;
using Net.Dot.Android.Kotlinasync;

namespace BindingCompilerSpike
{
	[Activity (Label = "Kotlin production runtime", Exported = true)]
	[Register ("net/dot/android/bindingcompiler/KotlinProductionProbeActivity")]
	public class KotlinProductionProbeActivity : KotlinAsyncProbeActivity
	{
		protected override async Task AdditionalChecks (AsyncFixture fixture)
		{
			await Run ("suspend reference results preserve typed nullable peers", async () => {
				Task<Payload?> task = fixture.PayloadAsync ();
				using var payload = await task ?? throw new InvalidOperationException ("Expected typed payload.");
				Check (payload.Text == "typed-payload");
			});
			await Run ("generated Flow property facade preserves typed null items", async () => {
				var values = new System.Collections.Generic.List<string?> ();
				await foreach (string? value in fixture.GetPropertyFlowAsAsyncEnumerable ())
					values.Add (value);
				Check (values.Count == 3 && values [0] == "first" && values [1] == null && values [2] == "last");
			});
			await Run ("nullable Flow facade does not invent an empty stream", () => {
				Check (fixture.NullableFlowAsAsyncEnumerable () == null);
				return Task.CompletedTask;
			});
			await Run ("primitive value class boxes and unboxes through verified JVM methods", () => {
				var value = Count.Create (7);
				using var boxed = value.Box ();
				Check (boxed.Unbox ().Value == 7);
				return Task.CompletedTask;
			});
			await Run ("value class creation preserves Kotlin constructor validation", () => {
				try {
					Count.Create (-1);
					throw new InvalidOperationException ("Expected Kotlin value-class validation.");
				} catch (Java.Lang.IllegalArgumentException ex) when (ex.Message?.Contains ("negative-count", StringComparison.Ordinal) == true) {
				}
				return Task.CompletedTask;
			});
			await Run ("reference and nullable reference backed value classes round trip", () => {
				using var text = Label.Create ("reference-label");
				Check (text.Unbox () == "reference-label");
				using var nullable = NullableLabel.Create (null);
				Check (nullable.Unbox () == null);
				return Task.CompletedTask;
			});
			await Run ("generic value class boxing preserves an existing peer", () => {
				using var value = new Java.Lang.String ("generic-label");
				using var boxed = GenericLabel.Create (value);
				Check (ReferenceEquals (value, boxed.Unbox ()) && value.Handle != IntPtr.Zero);
				return Task.CompletedTask;
			});
			await Run ("nullable suspend value classes preserve boxed peer identity", async () => {
				using var boxed = Count.Create (9).Box ();
				Check (fixture.NullableCount (null) == null);
				Check (ReferenceEquals (boxed, fixture.NullableCount (boxed)));
				Task<CountBoxed?> task = fixture.SuspendedCountAsync (boxed);
				Check (ReferenceEquals (boxed, await task));
			});
			await Run ("typed Flow value class elements can be unboxed", async () => {
				var sum = 0;
				await foreach (CountBoxed value in fixture.CountFlowAsAsyncEnumerable ())
					sum += value.Unbox ().Value;
				Check (sum == 8);
			});
			await Run ("default arguments execute native Kotlin defaults", () => {
				Check (fixture.DefaultsWithDefaults () == "hellohello");
				Check (fixture.DefaultsWithDefaults (prefix: "x", times: 3) == "xxx");
				return Task.CompletedTask;
			});
			await Run ("explicit zero is not an omitted default", () => {
				Check (fixture.DefaultsWithDefaults (times: 0) == "");
				return Task.CompletedTask;
			});
			await Run ("suspend default adapters are awaitable", async () =>
				Check (await fixture.SuspendDefaultsWithDefaultsAsync () == "asyncasync"));
			await Run ("nontrailing defaults preserve required argument names", () => {
				Check (fixture.MixedWithDefaults (required: 8) == "prefix8");
				Check (fixture.MixedWithDefaults (required: 9, optional: "set") == "set9");
				return Task.CompletedTask;
			});
			await Run ("explicit null differs from an omitted default", () => {
				Check (fixture.NullableDefaultWithDefaults () == "native-default");
				Check (fixture.NullableDefaultWithDefaults ((string?) null) == null);
				return Task.CompletedTask;
			});
			await Run ("default expressions are evaluated natively and only when omitted", () => {
				var before = fixture.DefaultEvaluations.Get ();
				Check (fixture.LazyDefaultWithDefaults () == before + 1);
				Check (fixture.LazyDefaultWithDefaults (value: 13) == 13);
				Check (fixture.DefaultEvaluations.Get () == before + 1);
				return Task.CompletedTask;
			});
			await Run ("default masks span signed bit 31 and multiple words", () => {
				Check (fixture.WideWithDefaults () == 63);
				Check (fixture.WideWithDefaults (p31: 40, p32: 80) == 120);
				Check (fixture.WideWithDefaults (p0: 7, p31: 11, p32: 13) == 31);
				return Task.CompletedTask;
			});
			await Run ("interface default helpers use their actual JVM owner", () => {
				using var service = fixture.DefaultService ();
				Check (service.ValueWithDefaults () == "interface-default");
				return Task.CompletedTask;
			});
			await Run ("static and extension default helpers retain receiver layout", () => {
				Check (StaticDefaults.ValueWithDefaults () == "static-default");
				Check (AsyncFixtureKt.AppendDefaultWithDefaults ("value") == "value!");
				return Task.CompletedTask;
			});
			await Run ("constructor defaults invoke the verified masked constructor", () => {
				using var value = DefaultsConstructed.CreateWithDefaults ();
				Check (value.Value == "ctor-default" && value.Number == 4);
				using var explicitValue = DefaultsConstructed.CreateWithDefaults (value: "explicit", number: 9);
				Check (explicitValue.Value == "explicit" && explicitValue.Number == 9);
				return Task.CompletedTask;
			});
			await Run ("constructor defaults can precede required parameters", () => {
				using var value = MixedConstructed.CreateWithDefaults (required: 8);
				Check (value.Value == "ctor-prefix8");
				return Task.CompletedTask;
			});
			await Run ("nested nullable Kotlin list results keep typed live views", async () => {
				Task<KotlinList<KotlinList<string?>>> task = fixture.NestedListAsync ();
				var list = await task;
				Check (list.Count == 2 && list [0].Count == 2 && list [0] [0] == "first" && list [0] [1] == null);
				Check (list [1] [0] == "last");
				Check (await fixture.NullableListAsync () == null);
			});
			await Run ("typed Kotlin maps preserve null keys and null values", async () => {
				Task<KotlinMap<string?, int?>> task = fixture.MapResultAsync ();
				var map = await task;
				Check (map.Count == 2);
				var nullEntry = false;
				var answerEntry = false;
				foreach (var pair in map) {
					nullEntry |= pair.Key == null && pair.Value == null;
					answerEntry |= pair.Key == "answer" && pair.Value == 42;
				}
				Check (nullEntry && answerEntry);
			});
			await Run ("typed Flow elements retain generic Kotlin sets", async () => {
				var seen = 0;
				await foreach (KotlinSet<string> set in fixture.SetFlowAsAsyncEnumerable ()) {
					Check (set.Count == 2);
					foreach (string value in set) {
						Check (value == "first" || value == "last");
						seen++;
					}
				}
				Check (seen == 2);
			});
			await Run ("native Main immediate dispatcher resumes on Android Main", async () => {
				using var context = fixture.MainContext ();
				try {
					var name = await Task.Run (() => fixture.DispatcherNameAsync (coroutineContext: context));
					Check (name == "main");
				} finally {
					fixture.CancelContext (context);
				}
			});
			await Run ("Flow collection can use native Main from a managed worker", async () => {
				using var context = fixture.MainContext ();
				try {
					await Task.Run (async () => {
						var seen = 0;
						await foreach (string value in fixture.DispatcherFlowAsAsyncEnumerable (coroutineContext: context)) {
							Check (value == "main");
							seen++;
						}
						Check (seen == 2);
					});
				} finally {
					fixture.CancelContext (context);
				}
			});
			await Run ("Kotlin awaits a managed Task implementation", async () => {
				using var service = new ManagedEchoService ();
				Check (await fixture.CallManagedAsync (service, "managed-value") == "managed-value");
				Check (service.Calls == 1 && service.Cleanups == 1);
			});
			await Run ("managed failure survives the Kotlin result boundary", async () => {
				using var service = new ManagedEchoService { Fail = true };
				try {
					await fixture.CallManagedAsync (service, "managed-value");
					throw new InvalidOperationException ("Expected managed failure.");
				} catch (FormatException ex) when (ex.Message == "managed-failure") {
					Check (service.Cleanups == 1);
				}
			});
			await Run ("native cancellation waits for managed finally", async () => {
				using var service = new ManagedEchoService { Wait = true };
				using var cancellation = new CancellationTokenSource ();
				var task = fixture.CallManagedAsync (service, "unused", cancellation.Token);
				cancellation.Cancel ();
				await ExpectCancellation (task);
				Check (service.Calls == 1 && service.Cleanups == 1);
			});
			await Run ("managed completion can be synchronous", async () => {
				using var service = new ManagedEchoService { Immediate = true };
				Check (await fixture.CallManagedAsync (service, "immediate-managed") == "immediate-managed");
			});
			await Run ("native parent cancellation joins managed implementation", async () => {
				using var service = new ManagedEchoService { Wait = true };
				using var context = fixture.ParentContext ();
				var task = fixture.CallManagedAsync (service, "unused", coroutineContext: context);
				fixture.CancelContext (context);
				await ExpectCancellation (task);
				Check (service.Cleanups == 1);
			});
			await Run ("demand-driven Flow meets the measured allocation reduction", () =>
				KotlinFlowBenchmarks.Run (fixture, Log), timeoutSeconds: 90);
		}
	}

	[Register ("net/dot/android/bindingcompiler/ManagedEchoService")]
	public class ManagedEchoService : Java.Lang.Object, IManagedEcho
	{
		public bool Fail { get; set; }
		public bool Wait { get; set; }
		public bool Immediate { get; set; }
		public int Calls { get; private set; }
		public int Cleanups { get; private set; }

		public Java.Lang.Object? Echo (string value, IContinuation continuation) =>
			KotlinCoroutines.FromTask (continuation, token => EchoAsync (value, token), result => new Java.Lang.String (result));

		Task<string> EchoAsync (string value, CancellationToken token)
		{
			Calls++;
			if (Immediate)
				return Task.FromResult (value);
			return Complete ();

			async Task<string> Complete ()
			{
				try {
					if (Wait)
						await Task.Delay (Timeout.Infinite, token);
					else
						await Task.Delay (2, token);
					if (Fail)
						throw new FormatException ("managed-failure");
					return value;
				} finally {
					await Task.Delay (10);
					Cleanups++;
				}
			}
		}
	}
}
