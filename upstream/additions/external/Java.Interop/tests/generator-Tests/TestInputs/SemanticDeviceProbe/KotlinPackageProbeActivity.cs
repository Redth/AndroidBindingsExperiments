#nullable enable
using System;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using Android.Runtime;
using Java.Interop;
using Java.Interop.Kotlin;
using JObject = Java.Lang.Object;

namespace BindingCompilerSpike
{
	[Activity (Label = "Kotlin package round trip", MainLauncher = true, Exported = true)]
	[Register ("net/dot/android/bindingcompiler/KotlinPackageProbeActivity")]
	public class KotlinPackageProbeActivity : Activity
	{
		protected override async void OnCreate (Bundle? savedInstanceState)
		{
			base.OnCreate (savedInstanceState);
			try {
				using var fixture = new PackageFixture ();
				if (await KotlinCoroutines.RunAsync (fixture.Delayed, KotlinCoroutines.GetString) != "delayed")
					throw new InvalidOperationException ("Wrong package suspend result.");
				Log ("PASS source-built package coroutine");
				using var flow = fixture.Burst (3);
				var sum = 0;
				await foreach (var value in flow.AsAsyncEnumerable (KotlinCoroutines.GetInt32))
					sum += value;
				if (sum != 3)
					throw new InvalidOperationException ("Wrong package Flow result.");
				Log ("PASS source-built package Flow");
				foreach (var reference in typeof (KotlinCoroutines).Assembly.GetReferencedAssemblies ())
					if (reference.Name?.StartsWith ("Xamarin.Kotlin", StringComparison.Ordinal) == true)
						throw new InvalidOperationException ("The package depends on published Kotlin CLR ownership.");
				Log ("PASS no Kotlin CLR support package dependency");
				Log ("DONE 3");
			} catch (Exception ex) {
				Log ("FAIL " + ex);
			}
		}

		static void Log (string message) => Android.Util.Log.Info ("KotlinPackageProbe", ProbeRunIdentity.Value + " " + message);
	}

	[Register ("net/dot/android/kotlinasync/AsyncFixture", DoNotGenerateAcw = true)]
	public sealed class PackageFixture : JObject
	{
		static readonly JniPeerMembers members = new JniPeerMembers ("net/dot/android/kotlinasync/AsyncFixture", typeof (PackageFixture));
		public override JniPeerMembers JniPeerMembers => members;

		public unsafe PackageFixture () : base (IntPtr.Zero, JniHandleOwnership.DoNotTransfer)
		{
			var value = members.InstanceMethods.StartCreateInstance ("()V", GetType (), null);
			SetHandle (value.Handle, JniHandleOwnership.TransferLocalRef);
			members.InstanceMethods.FinishCreateInstance ("()V", this, null);
		}

		public PackageFixture (IntPtr handle, JniHandleOwnership ownership) : base (handle, ownership)
		{
		}

		public unsafe JObject? Delayed (JObject continuation)
		{
			var argument = new JniArgumentValue (continuation.PeerReference);
			try {
				var result = members.InstanceMethods.InvokeAbstractObjectMethod (
					"delayed.(Lkotlin/coroutines/Continuation;)Ljava/lang/Object;", this, &argument);
				return GetObject<JObject> (result.Handle, JniHandleOwnership.TransferLocalRef);
			} finally {
				GC.KeepAlive (continuation);
			}
		}

		public unsafe JObject Burst (int count)
		{
			var argument = new JniArgumentValue (count);
			var result = members.InstanceMethods.InvokeAbstractObjectMethod (
				"burst.(I)Lkotlinx/coroutines/flow/Flow;", this, &argument);
			return GetObject<JObject> (result.Handle, JniHandleOwnership.TransferLocalRef)
				?? throw new InvalidOperationException ("No native Flow.");
		}
	}
}
