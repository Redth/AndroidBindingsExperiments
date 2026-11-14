#nullable enable
using System;
using System.Threading.Tasks;
using Android.App;
using Android.Runtime;
using IO.Sentry;
using IO.Sentry.Android.Core;
using IO.Sentry.Protocol;
using IO.Sentry.Transport;
using Java.Interop.Kotlin;

namespace ThirdPartyProbe
{
	[Activity (Name = "net.dot.android.thirdparty.ProbeActivity", MainLauncher = true, Exported = true)]
	public sealed class SentryProbe : OfflineProbeActivity
	{
		protected override int ExpectedChecks => 6;

		protected override Task RunChecksAsync ()
		{
			using var options = new SentryAndroidOptions ();
			options.Dsn = "https://public@example.invalid/1";
			options.Environment = "offline-probe";
			options.Release = "binding-probe@1";
			options.MaxBreadcrumbs = 7;
			using var transport = new RecordingTransport ();
			using var transportFactory = new RecordingTransportFactory (transport);
			options.TransportFactory = transportFactory;
			Check (options.Environment == "offline-probe" && options.Release == "binding-probe@1" && options.MaxBreadcrumbs == 7,
				"Sentry Android options roundtrip");
			Check (ReferenceEquals (((IJavaProjection<SentryOptions>) options).Peer, options),
				"Sentry derived options retain the canonical peer");
			using var e = new SentryEvent ();
			e.SetTag ("source", "csharp");
			Check (e.GetTag ("source") == "csharp", "Sentry event data through JNI");
			using var callback = new DropEventCallback ();
			options.BeforeSend = callback;
			Check (ReferenceEquals (options.BeforeSend, callback), "Sentry callback registration preserves managed identity");
			using var client = new SentryClient (options);
			try {
				using var hint = new Hint ();
				using var captured = client.CaptureEvent (e, null, hint);
				Check (callback.Calls == 1 && callback.LastTag == "csharp", "Sentry native capture executes the managed before-send callback");
				using var empty = SentryId.EmptyId;
				Check (empty != null && captured.Equals (empty) && transportFactory.Calls == 1 && transport.Sends == 0,
					"Sentry dropped event uses the recording transport and sends nothing");
			} finally {
				client.Close ();
			}
			return Task.CompletedTask;
		}

		[Register ("net/dot/android/thirdparty/SentryDropEventCallback")]
		sealed class DropEventCallback : Java.Lang.Object, SentryOptions.IBeforeSendCallback
		{
			internal int Calls { get; private set; }
			internal string? LastTag { get; private set; }

			public SentryEvent? Execute (SentryEvent e, Hint hint)
			{
				Calls++;
				LastTag = e.GetTag ("source");
				return null;
			}
		}

		[Register ("net/dot/android/thirdparty/SentryRecordingTransportFactory")]
		sealed class RecordingTransportFactory : Java.Lang.Object, ITransportFactory
		{
			readonly RecordingTransport transport;
			internal int Calls { get; private set; }

			internal RecordingTransportFactory (RecordingTransport transport) => this.transport = transport;

			public ITransport Create (SentryOptions options, RequestDetails details)
			{
				Calls++;
				return transport;
			}
		}

		[Register ("net/dot/android/thirdparty/SentryRecordingTransport")]
		sealed class RecordingTransport : Java.Lang.Object, ITransport
		{
			internal int Sends { get; private set; }
			internal int Flushes { get; private set; }
			internal bool Closed { get; private set; }
			public RateLimiter? RateLimiter => null;
			public bool IsHealthy => true;
			public void Send (SentryEnvelope envelope, Hint hint) => Sends++;
			public void Flush (long timeoutMillis) => Flushes++;
			public void Close () => Closed = true;
			public void Close (bool isRestarting) => Close ();
		}
	}
}
