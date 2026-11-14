using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Widget;
using Androidx.Activity;
using Androidx.Datastore.Preferences.Core;
using Java.Interop.Kotlin;
using Net.Dot.Android.Reference;
using Reference.Reader;
using Reference.Writer;

namespace BindingProjection
{
	[Activity (Label = "Binding reference", MainLauncher = true, Exported = true)]
	[Register ("net/dot/android/reference/ReferenceActivity")]
	public sealed class ReferenceActivity : ComponentActivity
	{
		static int instances;
		readonly TaskCompletionSource firstValue = new TaskCompletionSource (TaskCreationOptions.RunContinuationsAsynchronously);
		CancellationTokenSource? observationCancellation;
		Task observation = Task.CompletedTask;
		SettingsRepository? repository;
		TextView? status;
		string identity = "";
		int value;
		int expectedAfterRecreation = -1;
		bool started;
		bool testing;
		bool testStarted;

		protected override void OnCreate (Bundle? savedInstanceState)
		{
			base.OnCreate (savedInstanceState);
			Interlocked.Increment (ref instances);
			identity = Intent?.GetStringExtra ("runId") ?? Guid.NewGuid ().ToString ("N");
			testing = Intent?.GetBooleanExtra ("selfTest", false) == true;
			expectedAfterRecreation = savedInstanceState?.GetInt ("expectedCount", -1) ?? -1;
			repository = CounterReader.Open (this);
			var layout = new LinearLayout (this) { Orientation = Orientation.Vertical };
			status = new TextView (this) { Text = "Loading DataStore..." };
			var increment = new Button (this) { Text = "Increment persisted counter" };
			increment.Click += async (sender, args) => {
				try {
					await CounterWriter.IncrementAsync (Repository);
				} catch (Exception ex) {
					Fail (ex);
				}
			};
			layout.AddView (status);
			layout.AddView (increment);
			SetContentView (layout);
		}

		SettingsRepository Repository => repository ?? throw new InvalidOperationException ("The repository has not been opened.");

		protected override void OnStart ()
		{
			base.OnStart ();
			started = true;
			_ = StartObservationAsync ();
		}

		protected override void OnStop ()
		{
			started = false;
			observationCancellation?.Cancel ();
			base.OnStop ();
		}

		protected override void OnSaveInstanceState (Bundle outState)
		{
			outState.PutInt ("expectedCount", expectedAfterRecreation);
			base.OnSaveInstanceState (outState);
		}

		async Task StartObservationAsync ()
		{
			try {
				await StopObservationAsync ();
				if (!started)
					return;
				observationCancellation = new CancellationTokenSource ();
				observation = ObserveAsync (observationCancellation.Token);
				if (testing && !testStarted) {
					testStarted = true;
					await SelfTestAsync ();
				}
			} catch (Exception ex) {
				Fail (ex);
			}
		}

		async Task ObserveAsync (CancellationToken token)
		{
			try {
				await foreach (var next in CounterReader.Observe (Repository, this, token)) {
					value = next;
					if (status != null)
						status.Text = "Persisted counter: " + value;
					firstValue.TrySetResult ();
				}
			} catch (System.OperationCanceledException) when (token.IsCancellationRequested) {
			} catch (Exception ex) {
				Fail (ex);
				throw;
			}
		}

		async Task StopObservationAsync ()
		{
			observationCancellation?.Cancel ();
			await observation;
			observationCancellation?.Dispose ();
			observationCancellation = null;
		}

		async Task SelfTestAsync ()
		{
			await firstValue.Task.WaitAsync (TimeSpan.FromSeconds (30));
			if (expectedAfterRecreation < 0) {
				var expectedBefore = Intent?.GetIntExtra ("expectedBefore", -1) ?? -1;
				if (expectedBefore >= 0 && value != expectedBefore)
					throw new InvalidOperationException ("The DataStore value did not survive process restart.");
				Log ("START VALUE " + value);
				Check (await GenericProjectionChecks.RunAsync (Log) == GenericProjectionChecks.ExpectedCount,
					"typed generic and accessibility integration checks");
				Check (CounterReader.ViewType.FullName == CounterWriter.ViewType.FullName &&
					CounterReader.ViewType.Assembly != CounterWriter.ViewType.Assembly,
					"independently built libraries have distinct minimal views of one JVM type");
				Check (ReferenceEquals (Repository, SettingsRepository.Open (this)), "one canonical managed peer crosses both library boundaries");
				var store = Repository.DataStore ();
				using var key = PreferencesKeys.IntKey ("count");
				await using (var data = store.GetDataAsAsyncEnumerable ().GetAsyncEnumerator ()) {
					if (!await data.MoveNextAsync ())
						throw new InvalidOperationException ("The DataStore interface did not produce its stored preferences.");
					var preferences = data.Current?.JavaCast<Preferences> () ??
						throw new InvalidOperationException ("No generated Preferences peer was returned.");
					var rawValue = preferences.Get (key);
					Check ((rawValue == null ? 0 : KotlinCoroutines.GetInt32 (rawValue)) == value,
						"C# directly reads real DataStore and Preferences APIs through regenerated bindings");
				}
				using var listener = new ManagedChangeListener ();
				CounterWriter.Notify (Repository, listener, this, value);
				Check (listener.Calls == 1 && ReferenceEquals (listener.LastOwner, this),
					"Java dispatch reaches a managed callback in the other library with the original Activity peer");
				var before = await Repository.CurrentAsync ();
				var committed = await CounterWriter.IncrementAsync (Repository);
				await WaitUntil (() => value == committed);
				Check (committed == before + 1 && File.Exists (Path.Combine (FilesDir?.AbsolutePath ??
					throw new InvalidOperationException ("No app files directory."), "binding-reference.preferences_pb")),
					"real DataStore commit reaches the C# typed Flow and writes persistent storage");
				expectedAfterRecreation = committed;
				await StopObservationAsync ();
				Check (Repository.ActiveCollectors () == 0, "ending collection waits for native subscription cleanup");
				Recreate ();
			} else {
				Check (instances >= 2 && value == expectedAfterRecreation, "Activity recreation preserves the committed DataStore value");
				await WaitUntil (() => Repository.ActiveCollectors () == 1);
				Check (Repository.ActiveCollectors () == 1, "recreated Activity starts exactly one new lifecycle-bound collection");
				await StopObservationAsync ();
				Check (Repository.ActiveCollectors () == 0 && await Repository.CurrentAsync () == value,
					"recreated collection cleans up without disposing the shared repository");
				Log ("DONE 10 VALUE " + value);
			}
		}

		static async Task WaitUntil (Func<bool> predicate)
		{
			var timeout = DateTime.UtcNow.AddSeconds (15);
			while (!predicate ()) {
				if (DateTime.UtcNow >= timeout)
					throw new TimeoutException ("The expected native state was not observed.");
				await Task.Delay (20);
			}
		}

		void Check (bool condition, string name)
		{
			if (!condition)
				throw new InvalidOperationException (name);
			Log ("PASS " + name);
		}

		void Log (string message) => Android.Util.Log.Info ("BindingReference", identity + " " + message);

		void Fail (Exception error)
		{
			if (status != null)
				status.Text = error.ToString ();
			Log ("FAIL " + error);
			Android.Util.Log.Error ("BindingReference", error.ToString ());
		}
	}
}
