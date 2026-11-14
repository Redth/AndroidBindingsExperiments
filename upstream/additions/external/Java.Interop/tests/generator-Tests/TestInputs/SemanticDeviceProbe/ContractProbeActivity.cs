using System;
using System.Collections.Generic;
using System.IO;
using Android.App;
using Android.OS;
using Android.Runtime;
using Probe = Net.Dot.Android.Bindingcompiler.ContractProbe;

namespace BindingCompilerSpike
{
	[Activity (Label = "Binding contract probe", MainLauncher = true, Exported = true)]
	[Register ("net/dot/android/bindingcompiler/ContractProbeActivity")]
	public class ContractProbeActivity : Activity
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
				Run ("inherited Java Iterator default through explicit CLR interface", () => {
					using var peer = new Probe.DefaultIterator ();
					Java.Util.IIterator iterator = peer;
					Check (iterator.HasNext, "Missing iterator state.");
					using var item = Require (iterator.Next ());
					Check (item.ToString () == "next", "Wrong iterator result.");
					try {
						iterator.Remove ();
						throw new InvalidOperationException ("The Java default should reject removal.");
					} catch (Java.Lang.UnsupportedOperationException) {
					}
				});
				Run ("generic native interface with typed implementation", () => {
					using var peer = new Probe.StringMapper ();
					using var input = new Java.Lang.String ("input");
					Probe.IMapper mapper = peer;
					using var result = Require (mapper.Map (input));
					Check (result.ToString () == "mapped:input", "Wrong generic interface result.");
				});
				Run ("Java to managed callback through inherited interface diamond", () => {
					using var peer = new ManagedCombined ();
					using var result = Require (Probe.ReadCombined (peer));
					Check (peer.Calls == 1 && result.ToString () == "callback", "The inherited interface callback did not dispatch exactly once.");
				});
				Run ("synthesized abstract property through concrete invoker", () => {
					using var peer = Require (Probe.CreatePropertyPeer ());
					Check (peer.GetType ().Name == "PropertyBaseInvoker", "The private native class did not use the generated invoker.");
					Check (peer.Value == "initial", "Wrong abstract property getter.");
					peer.Value = "updated";
					Check (peer.Value == "updated", "Wrong abstract property setter.");
				});
				Run ("interface-array specialization through erased CLR base", () => {
					using var peer = new Probe.ArrayPeer ();
					using var values = Require (Java.Lang.Object.GetObject<Java.Lang.Object> (
						JNIEnv.NewArray (new [] { "one", "two" }), JniHandleOwnership.TransferLocalRef));
					Probe.ArrayBase basis = peer;
					basis.Put (values);
					Check (peer.LastSize == 2, "Wrong array specialization.");
				});
				Run ("public API inherited from obfuscated-looking public base", () => {
					using var peer = new Probe.PublicPeer ();
					Check (peer.Answer == 42, "Public native base API was lost.");
				});
				Run ("formatted generic interface adapter with bounded parameter", () => {
					using var provider = new Probe.PublicSummary ();
					using var peer = new Probe.PublicPeer ();
					Probe.ISummary summary = provider;
					using var result = Require (summary.SummarizeFormatted (peer));
					Check (result.ToString () == "answer:42", "Wrong formatted interface result.");
				});
				Run ("colliding native static field and method both remain callable", () => {
					using var field = Require (Probe.InstanceValue);
					using var method = Require (Probe.Instance (1));
					Check (field.Answer == 42 && method.Answer == 42, "A native field or method was lost.");
				});
				Run ("distinct native fields with colliding managed names remain callable", () => {
					using var constant = Require (Probe.DetectionApi);
					using var other = Require (Probe.DetectionApiValue);
					Check (constant.Answer == 42 && other.Answer == 42, "A colliding native field was lost.");
					Check (!JNIEnv.IsSameObject (constant.Handle, other.Handle), "Distinct native fields resolved to the same object.");
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

	[Register ("net/dot/android/bindingcompiler/ManagedCombined")]
	public class ManagedCombined : Java.Lang.Object, Probe.ICombined
	{
		public int Calls { get; private set; }

		public Java.Lang.Object Read ()
		{
			Calls++;
			return new Java.Lang.String ("callback");
		}
	}
}
