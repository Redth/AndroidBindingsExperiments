using System;
using System.Collections.Generic;
using System.IO;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Java.Interop;
using Contracts = Androidx.Activity.Result.Contract.ActivityResultContracts;
using Contract = Androidx.Activity.Result.Contract.ActivityResultContract;
using JObject = Java.Lang.Object;

namespace BindingCompilerSpike
{
	[Activity (Label = "Binding compiler spike", MainLauncher = true, Exported = true)]
	[Register ("net/dot/android/bindingcompiler/ProbeActivity")]
	public class ProbeActivity : Activity
	{
		readonly List<string> results = [];
		string resultFile = "";

		protected override void OnCreate (Bundle? savedInstanceState)
		{
			base.OnCreate (savedInstanceState);
			resultFile = Path.Combine (Require (FilesDir).AbsolutePath, "binding-compiler-results.txt");
			results.Add (ProbeRunIdentity.Value);
			WriteResults ();
			try {
				using var input = new Java.Lang.String ("image/*");
				using var get_content = new Contracts.GetContent ();
				Run ("typed String JNI slot through erased C# base", () => {
					Contract contract = get_content;
					using var intent = contract.CreateIntent (this, input);
					Check (intent.Type == "image/*", "Wrong MIME type.");
				});
				Run ("Java generic bridge to typed String method", () => {
					using var intent = Probe.Create (get_content, this, input);
					Check (intent.Type == "image/*", "Wrong MIME type from Java bridge.");
				});

				using var uri = Require (Android.Net.Uri.Parse ("content://binding-compiler/item"));
				// Parsed results reuse this canonical peer; this scope owns its disposal.
				using var result_intent = new Intent ();
				result_intent.SetData (uri);
				Run ("covariant Uri result through erased managed return", () => {
					var result = Require (get_content.ParseResult ((int) Result.Ok, result_intent));
					Check (JavaObjectExtensions.JavaCast<Android.Net.Uri> (result).ToString () == uri.ToString (), "Wrong parsed URI.");
				});
				Run ("Java bridge with covariant Uri return", () => {
					var result = Probe.Parse (get_content, (int) Result.Ok, result_intent);
					Check (JavaObjectExtensions.JavaCast<Android.Net.Uri> (result).ToString () == uri.ToString (), "Wrong Java bridge URI.");
				});
				Run ("generic List result", () => {
					using var contract = new Contracts.GetMultipleContents ();
					using var result = Require (contract.ParseResult ((int) Result.Ok, result_intent));
					Check (JavaObjectExtensions.JavaCast<Java.Util.IList> (result).Size () == 1, "Wrong list result.");
				});
				Run ("generic Map result", () => {
					using var contract = new Contracts.RequestMultiplePermissions ();
					using var intent = new Intent ();
					intent.PutExtra (Contracts.RequestMultiplePermissions.ExtraPermissions, new [] { "android.permission.CAMERA" });
					intent.PutExtra (Contracts.RequestMultiplePermissions.ExtraPermissionGrantResults, new [] { 0 });
					using var result = Require (contract.ParseResult ((int) Result.Ok, intent));
					Check (JavaObjectExtensions.JavaCast<Java.Util.IMap> (result).Size () == 1, "Wrong permission map.");
				});

				using var array = Require (JObject.GetObject<JObject> (
					JNIEnv.NewArray (new [] { "image/png", "image/jpeg" }), JniHandleOwnership.TransferLocalRef));
				using var open_document = new Contracts.OpenDocument ();
				Run ("array specialization through typed JNI descriptor", () => {
					using var intent = open_document.CreateIntent (this, array);
					Check (intent.GetStringArrayExtra ("android.intent.extra.MIME_TYPES")?.Length == 2, "Wrong MIME array.");
				});
				Run ("Java erased bridge with array input", () => {
					using var intent = Probe.Create (open_document, this, array);
					Check (intent.GetStringArrayExtra ("android.intent.extra.MIME_TYPES")?.Length == 2, "Wrong Java bridge array.");
				});
				Run ("Java to managed scalar override and base dispatch", () => {
					using var contract = new ManagedGetContent ();
					using var intent = Probe.Create (contract, this, input);
					Check (contract.Calls == 1 && intent.Type == "image/*", "Managed callback missing or recursive.");
				});
				Run ("Java to managed array override and base dispatch", () => {
					using var contract = new ManagedOpenDocument ();
					using var intent = Probe.Create (contract, this, array);
					Check (contract.Calls == 1 && intent.GetStringArrayExtra ("android.intent.extra.MIME_TYPES")?.Length == 2, "Array callback missing or recursive.");
				});
				Run ("public companion singleton and nested type", () => {
					using var companion = Require (Contracts.RequestMultiplePermissions.CompanionInstance);
					Check (companion.Class.Name.EndsWith ("$Companion", StringComparison.Ordinal), "Wrong companion type.");
				});
				Run ("opaque private companion with exact field descriptor", () => {
					using var companion = Require (Contracts.GetMultipleContents.Companion);
					Check (companion.Class.Name.EndsWith ("$Companion", StringComparison.Ordinal), "Wrong private companion type.");
				});
				Run ("ComponentActivity construction and lifecycle", () => {
					using var activity = new Androidx.Activity.ComponentActivity ();
					Check (activity.Lifecycle != null, "Missing lifecycle.");
				});

				results.Add ("DONE " + (results.Count - 1));
				WriteResults ();
			} catch (Exception ex) {
				results.Add ("FAIL " + ex);
				WriteResults ();
				Android.Util.Log.Error ("BindingCompilerSpike", ex.ToString ());
				throw;
			}
		}

		void Run (string name, Action action)
		{
			action ();
			results.Add ("PASS " + name);
			WriteResults ();
			Android.Util.Log.Info ("BindingCompilerSpike", name);
		}

		void WriteResults () => File.WriteAllLines (resultFile, results);

		static void Check (bool condition, string message)
		{
			if (!condition)
				throw new InvalidOperationException (message);
		}

		static T Require<T> (T? value) where T : class =>
			value ?? throw new InvalidOperationException ("Unexpected null from the generated binding.");
	}

	[Register ("net/dot/android/bindingcompiler/ManagedGetContent")]
	public class ManagedGetContent : Contracts.GetContent
	{
		public int Calls { get; private set; }

		public override Intent CreateIntent (Context context, JObject? input)
		{
			Calls++;
			return base.CreateIntent (context, input);
		}
	}

	[Register ("net/dot/android/bindingcompiler/ManagedOpenDocument")]
	public class ManagedOpenDocument : Contracts.OpenDocument
	{
		public int Calls { get; private set; }

		public override Intent CreateIntent (Context context, JObject? input)
		{
			Calls++;
			return base.CreateIntent (context, input);
		}
	}

	static class Probe
	{
		static readonly JniPeerMembers members = new JniPeerMembers ("net/dot/android/bindingcompiler/BridgeProbe", typeof (JObject));

		public static unsafe Intent Create (Contract contract, Context context, JObject input)
		{
			JniArgumentValue* args = stackalloc JniArgumentValue [3];
			args [0] = new JniArgumentValue (contract.Handle);
			args [1] = new JniArgumentValue (context.Handle);
			args [2] = new JniArgumentValue (input.Handle);
			try {
				var result = members.StaticMethods.InvokeObjectMethod (
					"createViaBase.(Landroidx/activity/result/contract/ActivityResultContract;Landroid/content/Context;Ljava/lang/Object;)Landroid/content/Intent;", args);
				return JObject.GetObject<Intent> (result.Handle, JniHandleOwnership.TransferLocalRef)
					?? throw new InvalidOperationException ("Java bridge returned no intent.");
			} finally {
				GC.KeepAlive (contract);
				GC.KeepAlive (context);
				GC.KeepAlive (input);
			}
		}

		public static unsafe JObject Parse (Contract contract, int code, Intent intent)
		{
			JniArgumentValue* args = stackalloc JniArgumentValue [3];
			args [0] = new JniArgumentValue (contract.Handle);
			args [1] = new JniArgumentValue (code);
			args [2] = new JniArgumentValue (intent.Handle);
			try {
				var result = members.StaticMethods.InvokeObjectMethod (
					"parseViaBase.(Landroidx/activity/result/contract/ActivityResultContract;ILandroid/content/Intent;)Ljava/lang/Object;", args);
				return JObject.GetObject<JObject> (result.Handle, JniHandleOwnership.TransferLocalRef)
					?? throw new InvalidOperationException ("Java bridge returned no parsed result.");
			} finally {
				GC.KeepAlive (contract);
				GC.KeepAlive (intent);
			}
		}
	}
}
