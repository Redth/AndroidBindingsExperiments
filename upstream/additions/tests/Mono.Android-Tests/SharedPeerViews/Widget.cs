#nullable enable

using System;

using Android.Runtime;

using Java.Interop;

namespace SharedPeerViews
{
	// Compiled independently into both libraries, not registered as another Java peer.
	public sealed partial class Widget : IDisposable
	{
		static readonly JniType JavaType = new JniType ("net/dot/android/test/SharedPeerFixture");
		Java.Lang.Object? peer;

		public Widget (Java.Lang.Object peer)
		{
			Validate (peer, JavaType);
			this.peer = peer;
		}

		/// <summary>
		/// Borrowed canonical peer for crossing projection boundaries. Disposing it invalidates every view.
		/// </summary>
		public Java.Lang.Object Peer {
			get {
				var owner = peer;
				if (owner == null || !owner.PeerReference.IsValid)
					throw new ObjectDisposedException (nameof (Widget));
				return owner;
			}
		}

		/// <summary>Releases only this view's managed root, never the canonical peer.</summary>
		public void Dispose ()
		{
			peer = null;
		}

		static void Validate (Java.Lang.Object value, JniType expectedType)
		{
			ArgumentNullException.ThrowIfNull (value);
			if (!value.PeerReference.IsValid)
				throw new ObjectDisposedException (nameof (value));
			try {
				if (!expectedType.IsInstanceOfType (value.PeerReference))
					throw new InvalidCastException ($"The Java object is not an instance of {expectedType.Name}.");
			} finally {
				GC.KeepAlive (value);
			}
		}

		static Java.Lang.Object? ReadPeer (ref JniObjectReference reference)
		{
			try {
				if (!reference.IsValid)
					return null;
				return Java.Lang.Object.GetObject<Java.Lang.Object> (reference.Handle, JniHandleOwnership.DoNotTransfer)
					?? throw new InvalidOperationException ("A non-null Java result did not resolve to a managed peer.");
			} finally {
				JniObjectReference.Dispose (ref reference);
			}
		}
	}
}
