#nullable enable
using System;
using Android.App;
using Android.OS;
using Android.Util;
using Androidx.Activity;

namespace BindingProjection
{
	[Activity (Name = "net.dot.android.bindings.GroupedSmokeActivity", MainLauncher = true, Exported = true)]
	public sealed class GroupedSmokeActivity : ComponentActivity
	{
		protected override async void OnCreate (Bundle? savedInstanceState)
		{
			base.OnCreate (savedInstanceState);
			var identity = Intent?.GetStringExtra ("run") ?? "missing-run";
			try {
				var count = await GenericProjectionChecks.RunAsync (line => Log.Info ("GroupedBindingSmoke", identity + " " + line));
				if (count != GenericProjectionChecks.ExpectedCount)
					throw new InvalidOperationException ($"Expected {GenericProjectionChecks.ExpectedCount} checks, got {count}.");
				Log.Info ("GroupedBindingSmoke", identity + " DONE " + count);
			} catch (Exception error) {
				Log.Error ("GroupedBindingSmoke", identity + " FAIL " + error);
			}
		}
	}
}
