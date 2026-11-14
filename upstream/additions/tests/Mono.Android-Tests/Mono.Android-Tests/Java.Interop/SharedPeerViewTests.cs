#nullable enable

extern alias ReaderProjection;
extern alias WriterProjection;

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using Android.Runtime;

using Java.Interop;

using NUnit.Framework;

using Reader = ReaderProjection::SharedPeerViews.Widget;
using Writer = WriterProjection::SharedPeerViews.Widget;

namespace Java.InteropTests
{
	[TestFixture]
	[NonParallelizable]
	[Category ("SharedPeerViews")]
	public class SharedPeerViewTests
	{
		const string FixtureName = "net/dot/android/test/SharedPeerFixture";

		[OneTimeSetUp]
		public void ReportEnvironment ()
		{
			Android.Util.Log.Info ("SharedPeerViews", $"Shared-peer experiment: {RuntimeInformation.FrameworkDescription}, " +
				$"{RuntimeInformation.ProcessArchitecture}, Android API {Android.OS.Build.VERSION.SdkInt}; " +
				$"value manager: {JniRuntime.CurrentRuntime.ValueManager.GetType ().FullName}.");
		}

		[Test]
		public void IndependentMinimalProjectionsShareOneCanonicalPeer ()
		{
			Assert.AreEqual (typeof (Reader).FullName, typeof (Writer).FullName);
			Assert.AreNotSame (typeof (Reader).Assembly, typeof (Writer).Assembly);
			Assert.IsFalse (typeof (Reader).IsAssignableFrom (typeof (Writer)));
			Assert.IsFalse (typeof (IJavaPeerable).IsAssignableFrom (typeof (Reader)));
			Assert.IsFalse (typeof (IJavaPeerable).IsAssignableFrom (typeof (Writer)));
			Assert.IsNull (typeof (Reader).GetMethod ("SetValue"));
			Assert.IsNull (typeof (Writer).GetMethod ("GetValue"));

			using var owner = CreateFixture (7);
			using var reader = new Reader (owner);
			using var writer = new Writer (reader.Peer);
			Assert.AreEqual (7, reader.GetValue ());

			writer.SetValue (42);

			Assert.AreEqual (42, reader.GetValue ());
			Assert.AreSame (owner, reader.Peer);
			Assert.AreSame (owner, writer.Peer);
			Assert.IsTrue (JniEnvironment.Types.IsSameObject (reader.Peer.PeerReference, writer.Peer.PeerReference));
			AssertRegistered (owner);
		}

		[Test]
		public void ArgumentsAndReturnsRoundTripWithoutCloningJavaObjects ()
		{
			using var firstOwner = CreateFixture (1);
			using var secondOwner = CreateFixture (2);
			using var reader = new Reader (firstOwner);
			using var writer = new Writer (secondOwner);

			var returned = reader.Echo (writer.Peer)
				?? throw new InvalidOperationException ("Echo returned null.");
			using var returnedReader = new Reader (returned);
			Assert.AreSame (secondOwner, returned);
			Assert.IsTrue (JniEnvironment.Types.IsSameObject (secondOwner.PeerReference, returned.PeerReference));
			Assert.IsFalse (JniEnvironment.Types.IsSameObject (firstOwner.PeerReference, returned.PeerReference));
			writer.SetValue (23);
			Assert.AreEqual (23, returnedReader.GetValue ());
			Assert.IsNull (reader.Echo (null));
			AssertRegistered (firstOwner);
			AssertRegistered (secondOwner);
		}

		[Test]
		public void ConversionChecksActualJavaAssignability ()
		{
			using var wrongType = new Java.Lang.String ("not a fixture or callback");
			using var owner = CreateFixture (1);
			using var reader = new Reader (owner);
			using var writer = new Writer (owner);

			Assert.Throws<InvalidCastException> (() => new Reader (wrongType));
			Assert.Throws<InvalidCastException> (() => new Writer (wrongType));
			Assert.Throws<InvalidCastException> (() => reader.Echo (wrongType));
			Assert.Throws<InvalidCastException> (() => writer.SetCallback (wrongType));
			Assert.IsNull (reader.GetCallback ());
			Assert.Throws<Java.Lang.IllegalStateException> (() => writer.InvokeCallback (1));
			AssertRegistered (owner);
		}

		[Test]
		public void OutboundCallsUseJavaVirtualDispatch ()
		{
			using var owner = CreateFixture (7, FixtureName + "$Derived");
			using var reader = new Reader (owner);
			using var writer = new Writer (owner);

			Assert.AreEqual (107, reader.GetValue ());
			writer.SetValue (42);
			Assert.AreEqual (142, reader.GetValue ());
		}

		[Test]
		public void ViewsOverManagedSubclassDispatchToTheOriginalCallbackOwner ()
		{
			using var owner = new SharedViewManagedFixture (7);
			owner.ManagedOffset = 100;
			using var reader = new Reader (owner);
			using var writer = new Writer (reader.Peer);

			Assert.AreEqual (107, reader.GetValue ());
			writer.SetValue (42);
			Assert.AreEqual (142, reader.GetValue ());
			Assert.AreEqual (2, owner.Invocations);
			Assert.AreSame (owner, reader.Echo (writer.Peer));
			AssertRegistered (owner);
		}

		[Test]
		public void CallbackReturnsToOriginalManagedOwnerAndOverride ()
		{
			using var owner = CreateFixture (1);
			using var reader = new Reader (owner);
			using var writer = new Writer (owner);
			using var callback = new SharedViewDerivedCallback (10);

			try {
				writer.SetCallback (callback);
				Assert.AreEqual (42, writer.InvokeCallback (11));
				Assert.AreEqual (1, callback.Invocations);
				Assert.AreEqual (11, callback.LastArgument);
				Assert.AreSame (callback, reader.GetCallback ());
				AssertRegistered (callback);

				for (int i = 0; i < 128; i++) {
					using var otherReader = new Reader (writer.Peer);
					using var otherWriter = new Writer (otherReader.Peer);
					Assert.AreEqual (42, otherWriter.InvokeCallback (11));
					Assert.AreSame (callback, otherReader.GetCallback ());
					AssertRegistered (owner);
					AssertRegistered (callback);
				}
				Assert.AreEqual (129, callback.Invocations);
			} finally {
				writer.SetCallback (null);
			}
			Assert.IsNull (reader.GetCallback ());
		}

		[Test]
		public void DisposingAViewDoesNotDisposeItsOwnerOrOtherViews ()
		{
			using var owner = CreateFixture (7);
			using var reader = new Reader (owner);
			using var writer = new Writer (owner);
			reader.Dispose ();
			reader.Dispose ();

			Assert.Throws<ObjectDisposedException> (() => reader.GetValue ());
			Assert.IsTrue (owner.PeerReference.IsValid);
			writer.SetValue (42);
			using var anotherReader = new Reader (writer.Peer);
			Assert.AreEqual (42, anotherReader.GetValue ());
			AssertRegistered (owner);
		}

		[Test]
		public void ExplicitOwnerDisposalInvalidatesAllViews ()
		{
			using var owner = CreateFixture (7);
			using var reader = new Reader (owner);
			using var writer = new Writer (owner);
			owner.Dispose ();

			Assert.Throws<ObjectDisposedException> (() => reader.GetValue ());
			Assert.Throws<ObjectDisposedException> (() => writer.SetValue (42));
			Assert.Throws<ObjectDisposedException> (() => new Reader (owner));
			Assert.Throws<ObjectDisposedException> (() => new Writer (owner));
		}

		[TestCase (false)]
		[TestCase (true)]
		[Repeat (10)]
		public void ViewsKeepThePeerAliveAndReleaseItAfterBothAreDisposed (bool managedSubclass)
		{
			CreateUnrootedViews (managedSubclass, out var reader, out var writer, out var weakPeer, out var weakJava);
			try {
				Collect ();
				Assert.IsTrue (weakPeer.IsAlive, "Views must retain the canonical managed peer.");
				Assert.IsFalse (JniEnvironment.Types.IsSameObject (weakJava, default));
				writer.SetValue (42);
				Assert.AreEqual (managedSubclass ? 142 : 42, reader.GetValue ());

				reader.Dispose ();
				Collect ();
				Assert.IsTrue (weakPeer.IsAlive, "The remaining writer must retain the canonical peer.");
				writer.SetValue (43);

				writer.Dispose ();
				WaitForCollection (weakPeer, weakJava);
			} finally {
				reader.Dispose ();
				writer.Dispose ();
				JniObjectReference.Dispose (ref weakJava);
			}
		}

		[Test]
		[Repeat (10)]
		public void JavaHeldCallbackSurvivesGcAndIsReleasedWhenDetached ()
		{
			using var owner = CreateFixture (7);
			using var reader = new Reader (owner);
			using var writer = new Writer (owner);
			AttachUnrootedCallback (writer, out var weakCallback, out var weakJava);
			try {
				Collect ();
				Assert.IsTrue (weakCallback.IsAlive, "The Java field must retain the original managed callback.");
				Assert.AreEqual (42, writer.InvokeCallback (11));
				AssertOriginalCallback (reader, weakCallback);
				writer.SetCallback (null);
				Assert.IsNull (reader.GetCallback ());
				WaitForCollection (weakCallback, weakJava);
				GC.KeepAlive (owner);
			} finally {
				writer.SetCallback (null);
				JniObjectReference.Dispose (ref weakJava);
			}
		}

		static Java.Lang.Object CreateFixture (int value, string javaName = FixtureName)
		{
			return new Java.Lang.Object (
				JNIEnv.CreateInstance (javaName, "(I)V", new JValue (value)),
				JniHandleOwnership.TransferLocalRef);
		}

		static void AssertRegistered (Java.Lang.Object owner)
		{
			Assert.AreSame (owner, JniRuntime.CurrentRuntime.ValueManager.PeekPeer (owner.PeerReference),
				"A view must not replace the authoritative peer in the registry.");
		}

		[MethodImpl (MethodImplOptions.NoInlining)]
		static void CreateUnrootedViews (bool managedSubclass, out Reader reader, out Writer writer, out WeakReference weakPeer,
			out JniObjectReference weakJava)
		{
			var owner = managedSubclass
				? new SharedViewManagedFixture (7) { ManagedOffset = 100 }
				: CreateFixture (7);
			reader = new Reader (owner);
			writer = new Writer (owner);
			weakPeer = new WeakReference (owner, trackResurrection: true);
			weakJava = owner.PeerReference.NewWeakGlobalRef ();
		}

		[MethodImpl (MethodImplOptions.NoInlining)]
		static void AttachUnrootedCallback (Writer writer, out WeakReference weakCallback, out JniObjectReference weakJava)
		{
			var callback = new SharedViewDerivedCallback (10);
			weakCallback = new WeakReference (callback, trackResurrection: true);
			weakJava = callback.PeerReference.NewWeakGlobalRef ();
			writer.SetCallback (callback);
		}

		[MethodImpl (MethodImplOptions.NoInlining)]
		static void AssertOriginalCallback (Reader reader, WeakReference weakCallback)
		{
			var callback = weakCallback.Target as SharedViewDerivedCallback
				?? throw new InvalidOperationException ("The original callback was lost.");
			Assert.AreSame (callback, reader.GetCallback ());
			Assert.AreEqual (1, callback.Invocations);
			Assert.AreEqual (11, callback.LastArgument);
			AssertRegistered (callback);
		}

		static void Collect ()
		{
			GC.Collect (GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
			GC.WaitForPendingFinalizers ();
			JniRuntime.CurrentRuntime.ValueManager.CollectPeers ();
			Java.Lang.JavaSystem.Gc ();
			GC.Collect (GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
			GC.WaitForPendingFinalizers ();
		}

		static void WaitForCollection (WeakReference managed, JniObjectReference java)
		{
			var stopwatch = Stopwatch.StartNew ();
			do {
				Collect ();
				if (!managed.IsAlive && JniEnvironment.Types.IsSameObject (java, default))
					return;
				Thread.Sleep (10);
			} while (stopwatch.Elapsed < TimeSpan.FromSeconds (10));

			Assert.Fail ($"Shared-peer release did not complete: managed alive={managed.IsAlive}, " +
				$"Java alive={!JniEnvironment.Types.IsSameObject (java, default)}.");
		}
	}

	// Only the authoritative callback owner has a binding/JCW contract; the views do not.
	[Register ("net/dot/android/test/SharedPeerFixture", DoNotGenerateAcw = true)]
	public class SharedViewFixturePeer : Java.Lang.Object
	{
		static readonly JniType JavaType = new JniType ("net/dot/android/test/SharedPeerFixture");
		static readonly JniMethodInfo GetValueMethod = JavaType.GetInstanceMethod ("getValue", "()I");

		[Register (".ctor", "(I)V", "")]
		protected SharedViewFixturePeer (int value)
			: base (IntPtr.Zero, JniHandleOwnership.DoNotTransfer)
		{
			SetHandle (JNIEnv.StartCreateInstance (GetType (), "(I)V", new JValue (value)),
				JniHandleOwnership.TransferLocalRef);
			JNIEnv.FinishCreateInstance (Handle, "(I)V", new JValue (value));
		}

		protected SharedViewFixturePeer (IntPtr handle, JniHandleOwnership transfer)
			: base (handle, transfer)
		{
		}

		[Register ("getValue", "()I", "n_GetValue:Java.InteropTests.SharedViewFixturePeer, Mono.Android.NET-Tests")]
		public virtual unsafe int GetValue ()
		{
			try {
				return JniEnvironment.InstanceMethods.CallNonvirtualIntMethod (PeerReference, JavaType.PeerReference, GetValueMethod);
			} finally {
				GC.KeepAlive (this);
			}
		}

		static unsafe int n_GetValue (IntPtr jnienv, IntPtr nativeThis)
			=> JniMarshal.SafeInvokeFunc (jnienv, nativeThis, &InvokeGetValue);

		static int InvokeGetValue (IntPtr jnienv, IntPtr nativeThis)
		{
			var owner = Java.Lang.Object.GetObject<SharedViewFixturePeer> (jnienv, nativeThis, JniHandleOwnership.DoNotTransfer)
				?? throw new InvalidOperationException ("The canonical callback owner was not found.");
			return owner.GetValue ();
		}
	}

	[Register ("net/dot/android/test/SharedViewManagedFixture")]
	public class SharedViewManagedFixture : SharedViewFixturePeer
	{
		[Register (".ctor", "(I)V", "")]
		public SharedViewManagedFixture (int value)
			: base (value)
		{
		}

		public int ManagedOffset { get; set; }
		public int Invocations { get; private set; }

		public override int GetValue ()
		{
			Invocations++;
			return base.GetValue () + ManagedOffset;
		}
	}

	public class SharedViewCallback : Java.Lang.Object, Java.Util.Functions.IIntUnaryOperator
	{
		readonly int offset;

		public SharedViewCallback (int offset)
		{
			this.offset = offset;
		}

		public int Invocations { get; private set; }
		public int LastArgument { get; private set; }

		public virtual int ApplyAsInt (int operand)
		{
			Invocations++;
			LastArgument = operand;
			return operand + offset;
		}
	}

	public class SharedViewDerivedCallback : SharedViewCallback
	{
		public SharedViewDerivedCallback (int offset)
			: base (offset)
		{
		}

		public override int ApplyAsInt (int operand) => base.ApplyAsInt (operand) * 2;
	}
}
