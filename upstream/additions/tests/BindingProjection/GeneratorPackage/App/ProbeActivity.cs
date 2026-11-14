using System;
using Android.App;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Java.Interop.Kotlin;
using PackagePreview;

namespace Preview.App;

[Activity (Name = "net.dot.android.packagetest.app.ProbeActivity", MainLauncher = true, Exported = true, Label = "Binding package probe")]
public sealed class ProbeActivity : Activity
{
	protected override void OnCreate (Bundle? savedInstanceState)
	{
		base.OnCreate (savedInstanceState);
		try {
			int checks = RunChecks ();
			Log.Info ("BindingPackageProbe", $"PASS {checks}/11");
		} catch (Exception error) {
			Log.Error ("BindingPackageProbe", "FAIL " + error);
		}
	}

	static int RunChecks ()
	{
		int checks = 0;
		void Check (bool success, string name)
		{
			if (!success)
				throw new InvalidOperationException (name);
			checks++;
		}

		using var raw = new PreviewBox ();
		Check (raw.RenamedEcho ("raw") == "raw", "Raw JNI method and metadata rename");
		Check (raw.HandwrittenEcho ("addition") == "addition", "Handwritten addition");
		var view = new PreviewBoxView<string> (raw);
		Check (view.GetValue () is null, "Native null read");
		view.SetValue ("typed-jni-value");
		Check (view.GetValue () == "typed-jni-value", "Typed generic JNI read/write");
		Check (view.RenamedEcho ("view") == "view", "Projected JNI method");
		Check (ReferenceEquals (view.Peer, raw), "View borrows the canonical managed peer");
		Check (ReferenceEquals (((IJavaProjection<PreviewBox>) raw).Peer, raw), "Raw projection capability");
		using var receiver = new ManagedListener ();
		Check (raw.Invoke (receiver, 21) == 21 && receiver.Value == 21 && receiver.Count == 1,
			"Raw Java-to-managed callback");
		Check (view.Invoke (receiver, 42) == 42 && receiver.Value == 42 && receiver.Count == 2,
			"Projected Java-to-managed callback");
		view.SetValue (null);
		Check (view.GetValue () is null, "Native null write");
		bool threw = false;
		try {
			raw.Fail ();
		} catch (Java.Lang.IllegalStateException error) {
			threw = error.Message == "package-preview-exception";
		}
		Check (threw, "Java exception translation");
		return checks;
	}
}

[Register ("net/dot/android/packagetest/ManagedListener")]
public sealed class ManagedListener : Java.Lang.Object, PreviewBox.IListener
{
	public int Value { get; private set; }
	public int Count { get; private set; }
	public void OnValue (int value)
	{
		Value = value;
		Count++;
	}
}
