#nullable enable
using System;
using Android.App;
using Android.OS;
using Android.Runtime;

namespace BindingCompilerSpike
{
	[Activity (Label = "Regenerated support bindings", Exported = true)]
	[Register ("net/dot/android/bindingcompiler/ClosureProbeActivity")]
	public class SupportClosureProbeActivity : Activity
	{
		protected override void OnCreate (Bundle? savedInstanceState)
		{
			base.OnCreate (savedInstanceState);
			try {
				Run ("Okio writes and reads through its regenerated generic contracts", () => {
					using var buffer = new Okio.BufferType ();
					buffer.WriteUtf8 ("okio");
					Check (buffer.ReadUtf8 () == "okio");
				});
				Run ("OkHttp parses a native media type", () => {
					using var media = Okhttp3.MediaType.Parse ("text/plain; charset=utf-8");
					Check (media?.ToString () == "text/plain; charset=utf-8");
				});
				Run ("RxJava 2 preserves a canonical peer through a generic stream", () => {
					using var value = new Java.Lang.String ("rx2");
					using var stream = IO.Reactivex.Observable.Just (value) ?? throw new InvalidOperationException ("No RxJava 2 stream.");
					Check (ReferenceEquals (value, stream.BlockingFirst ()));
				});
				Run ("RxJava 3 preserves a canonical peer through a generic stream", () => {
					using var value = new Java.Lang.String ("rx3");
					using var stream = IO.Reactivex.Rxjava3.Core.Observable.Just (value) ?? throw new InvalidOperationException ("No RxJava 3 stream.");
					Check (ReferenceEquals (value, stream.BlockingFirst ()));
				});
				Run ("Gson parses a native JSON value", () => {
					using var element = Com.Google.Gson.JsonParser.ParseString ("\"gson\"");
					Check (element?.AsString == "gson");
				});
				Run ("Protobuf preserves UTF-8 data", () => {
					using var bytes = Com.Google.Protobuf.ByteString.CopyFromUtf8 ("protobuf");
					Check (bytes?.ToStringUtf8 () == "protobuf");
				});
				Log ("DONE 6");
			} catch (Exception ex) {
				Log ("FAIL " + ex);
				throw;
			}
		}

		static void Run (string name, Action action)
		{
			action ();
			Log ("PASS " + name);
		}

		static void Log (string message) => Android.Util.Log.Info ("BindingClosureProbe", ProbeRunIdentity.Value + " " + message);

		static void Check (bool condition)
		{
			if (!condition)
				throw new InvalidOperationException ("Regenerated support binding returned an incorrect result.");
		}
	}
}
