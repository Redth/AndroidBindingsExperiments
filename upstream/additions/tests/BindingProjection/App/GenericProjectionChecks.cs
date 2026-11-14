using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Android.Runtime;
using Java.Interop.Kotlin;
using Net.Dot.Android.Reference;
using JObject = Java.Lang.Object;

namespace BindingProjection
{
	static class GenericProjectionChecks
	{
		public const int ExpectedCount = 26;

		public static async Task<int> RunAsync (Action<string> log)
		{
			var count = 0;
			void Check (bool condition, string name)
			{
				if (!condition)
					throw new InvalidOperationException ("Generic projection: " + name);
				count++;
				log ("GENERIC PASS " + name);
			}

			var text = GenericProbe_BoxView<string>.Create ("first");
			using var textPeer = text.Peer;
			text.Set ("second");
			Check (text.Get () == "second", "typed string parameter and result");

			var number = GenericProbe_BoxView<int>.Create (17);
			using var numberPeer = number.Peer;
			number.Set (29);
			Check (number.Get () == 29, "generic primitives use JVM boxes rather than primitive JNI slots");
			number.PublicValue = 41;
			Check (number.PublicValue == 41, "public generic field reads and writes preserve their type argument");

			var nullable = GenericProbe_BoxView<int?>.Create (null);
			using var nullablePeer = nullable.Peer;
			Check (nullable.Get () == null, "nullable generic value retains null rather than zero");
			Check (text.Echo (4.5) == 4.5, "method-scoped generic type inference");

			var specialized = GenericProbe_StringBoxView.Create ("inherited");
			using var specializedPeer = specialized.Peer;
			Check (specialized.Get () == "inherited", "non-generic subclass retains inherited concrete type argument");

			using var list = new Java.Util.ArrayList ();
			using var entry = new Java.Lang.String ("entry");
			list.Add (entry);
			list.Add (null);
			var typedList = new KotlinList<string?> (list, JvmType<string?>.Default.Read);
			var listBox = GenericProbe_ListBoxView<string?>.Create (typedList);
			using var listPeer = listBox.Peer;
			var values = listBox.Get ();
			Check (values != null && values.Count == 2 && values [0] == "entry" && values [1] == null,
				"inherited nested collection substitution preserves nullable elements");

			var array = JvmArray<int>.Create (new [] { 3, 5, 7 });
			using var arrayPeer = array.Peer;
			var returned = number.Array (array);
			Check (returned != null && returned.SequenceEqual (new [] { 3, 5, 7 }) && array.Peer.Class?.Name == "[Ljava.lang.Integer;",
				"generic integer array is Integer[] and not int[]");
			var primitive = number.PrimitiveArray (new [] { 11, 13 });
			Check (primitive != null && primitive.SequenceEqual (new [] { 11, 13 }), "explicit primitive array stays int[]");

			var nested = GenericProbe_BoxView<GenericProbe_BoxView<int>>.Create (number, GenericProbe_BoxView<int>.GetJvmType ());
			using var nestedPeer = nested.Peer;
			var nestedResult = nested.Get ();
			Check (nestedResult != null && ReferenceEquals (nestedResult.Peer, number.Peer) && nestedResult.Get () == 29,
				"nested generic views reuse the canonical JNI peer");
			var wrong = new GenericProbe_BoxView<string> (number.Peer);
			Check (Throws<InvalidCastException> (() => wrong.Get ()), "wrong erased type argument fails safely before JNI scalar conversion");

			var bounded = GenericProbe_BoundedBoxView<string>.Create ("bounded");
			using var boundedPeer = bounded.Peer;
			Check (bounded.Get () == "bounded", "multiple Java interface bounds accept a valid native type");
			Check (Throws<ArgumentException> (() => GenericProbe_BoundedBoxView<int>.Create (5)),
				"invalid generic bound is rejected before native invocation");

			var record = GenericProbe_TextRecordView.Create ("record");
			using var recordPeer = record.Peer;
			Check (ReferenceEquals (((IJavaProjection<GenericProbe.IRecord>) record).Peer, recordPeer) &&
				ReferenceEquals (((IJavaProjection<GenericProbe.IRecord>) recordPeer).Peer, recordPeer),
				"raw peers and typed views expose the same constrained projection");
			var rawResponse = GenericProbe_RecordResponseView<GenericProbe.TextRecord>.Create (recordPeer);
			using var rawResponsePeer = rawResponse.Peer;
			Check (ReferenceEquals (rawResponse.GetRecord (), recordPeer), "native record bound accepts a canonical raw peer");
			var response = GenericProbe_RecordResponseView<GenericProbe_TextRecordView>.Create (
				record, GenericProbe_TextRecordView.GetJvmType ());
			using var responsePeer = response.Peer;
			Check (ReferenceEquals (response.GetRecord ()?.Peer, recordPeer), "native record bound accepts a typed view without a second peer");
			var methodResponse = GenericProbeView.ReadRecord (record, GenericProbe_TextRecordView.GetJvmType ());
			using var methodResponsePeer = methodResponse?.Peer;
			Check (ReferenceEquals (methodResponse?.GetRecord ()?.Peer, recordPeer), "method generic bounds preserve constrained nested results");
			using var managedRecord = new ManagedRecord ();
			var callbackResponse = GenericProbe_RecordResponseView<GenericProbe.IRecord>.Create (managedRecord);
			using var callbackResponsePeer = callbackResponse.Peer;
			Check (ReferenceEquals (((IJavaProjection<GenericProbe.IRecord>) managedRecord).Peer, managedRecord) &&
				callbackResponse.GetRecord ()?.Name == "managed", "default interface projection supports managed callback peers");
			var recordText = new JvmType<string> ("net/dot/android/reference/GenericProbe$TextRecord",
				peer => peer == null ? null : KotlinCoroutines.GetPeer<GenericProbe.TextRecord> (peer).Name,
				value => value == "record" ? recordPeer : throw new ArgumentException ("Unexpected record text.", nameof (value)));
			var mappedResponse = GenericProbe_RecordResponseMappedView<string>.Create ("record", recordText);
			using var mappedResponsePeer = mappedResponse.Peer;
			Check (mappedResponse.GetRecord () == "record", "mapped view preserves custom representations of constrained native types");
			Check (Throws<ArgumentException> (() => GenericProbe_RecordResponseMappedView<string>.Create ("wrong")),
				"mapped views still reject converters that violate the native bound");

			using var source = new ManagedGenericSource ();
			var sourceView = new GenericProbe_ISourceView<string> (source);
			Check (GenericProbeView.Roundtrip (sourceView, "callback") == "callback" && source.Calls == 2,
				"typed generic call crosses a managed raw-interface callback");
			var lower = GenericProbeView.LowerBoundedValues ();
			Check (lower != null && lower.Count == 2 && lower [0]?.ToString () == "23" && lower [1]?.ToString () == "text",
				"lower wildcard reads do not cast every element to its lower bound");
			using var opaque = GenericProbeView.OpaqueResult ();
			Check (opaque?.ToString () == "opaque-result", "restricted return type retains an opaque public operation");
			var suspender = GenericSuspenderView<string>.Create ("stream");
			using var suspenderPeer = suspender.Peer;
			Check (await suspender.EchoAsync ("suspend") == "suspend", "generic suspend facade retains its real Task result type");
			var flow = suspender.Flow () ?? throw new InvalidOperationException ("The generic Flow was not returned.");
			var emitted = new List<string?> ();
			await foreach (var item in flow.AsAsyncEnumerable ())
				emitted.Add (item);
			Check (emitted.SequenceEqual (new [] { "stream" }), "generic Flow facade retains its element converter");

			var disposable = GenericProbe_BoxView<string>.Create ("dispose");
			disposable.Peer.Dispose ();
			Check (Throws<ObjectDisposedException> (() => disposable.Get ()), "view does not outlive a disposed canonical peer");
			return count;
		}

		static bool Throws<T> (Action action) where T : Exception
		{
			try {
				action ();
				return false;
			} catch (T) {
				return true;
			}
		}

		[Register ("net/dot/android/reference/ManagedGenericSource")]
		sealed class ManagedGenericSource : JObject, GenericProbe.ISource
		{
			string value = "";
			public int Calls { get; private set; }

			public JObject? Read ()
			{
				Calls++;
				return new Java.Lang.String (value);
			}

			public void Write (JObject? input)
			{
				Calls++;
				value = input?.ToString () ?? "";
			}
		}

		[Register ("net/dot/android/reference/ManagedRecord")]
		sealed class ManagedRecord : JObject, GenericProbe.IRecord
		{
			public string Name => "managed";
		}
	}
}
