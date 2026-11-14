#nullable enable
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using Android.Util;

namespace ThirdPartyProbe
{
	public abstract class OfflineProbeActivity : Activity
	{
		readonly HashSet<string> passed = new HashSet<string> (StringComparer.Ordinal);
		string identity = "";

		protected abstract int ExpectedChecks { get; }
		protected abstract Task RunChecksAsync ();

		protected override async void OnCreate (Bundle? state)
		{
			base.OnCreate (state);
			identity = Intent?.GetStringExtra ("run") ?? throw new InvalidOperationException ("Missing probe run identity.");
			try {
				await RunChecksAsync ().WaitAsync (TimeSpan.FromSeconds (60));
				if (passed.Count != ExpectedChecks)
					throw new InvalidOperationException ($"Expected {ExpectedChecks} checks; got {passed.Count}.");
				Log.Info ("ThirdPartyProbe", identity + " DONE " + passed.Count);
			} catch (Exception error) {
				Log.Error ("ThirdPartyProbe", identity + " FAIL " + error);
			}
		}

		protected void Check (bool condition, string name)
		{
			if (!condition)
				throw new InvalidOperationException (name);
			if (!passed.Add (name))
				throw new InvalidOperationException ("Duplicate probe check: " + name);
			Log.Info ("ThirdPartyProbe", identity + " PASS " + name);
		}

		protected void Artifact (string name, byte[] content) =>
			Log.Info ("ThirdPartyProbe", identity + " ARTIFACT " + name + " " + Convert.ToBase64String (content));
	}
}
