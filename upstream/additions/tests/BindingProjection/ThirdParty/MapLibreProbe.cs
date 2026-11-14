#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using Android.App;
using Android.Graphics;
using Android.Runtime;
using Java.Interop.Kotlin;
using Org.Maplibre.Android;
using Org.Maplibre.Android.Geometry;
using Org.Maplibre.Android.Snapshotter;

namespace ThirdPartyProbe
{
	[Activity (Name = "net.dot.android.thirdparty.ProbeActivity", MainLauncher = true, Exported = true)]
	public sealed class MapLibreProbe : OfflineProbeActivity
	{
		protected override int ExpectedChecks => 6;

		protected override async Task RunChecksAsync ()
		{
			using var library = MapLibre.GetInstance (this);
			using var offline = new Java.Lang.Boolean (false);
			MapLibre.SetConnected (offline);
			using var origin = new LatLng (48.25, 12.5);
			Check (origin.Latitude == 48.25 && origin.Longitude == 12.5, "MapLibre coordinate model");
			using var options = new MapSnapshotter.Options (96, 64);
			options.WithPixelRatio (1.0f);
			options.WithLogo (false);
			options.WithAttribution (false);
			options.WithStyleJson ("""
				{"version":8,"sources":{},"layers":[
				  {"id":"background","type":"background","paint":{"background-color":"#20a050"}}
				]}
				""");
			Check (options.Width == 96 && options.Height == 64, "MapLibre snapshot options");
			Check (ReferenceEquals (((IJavaProjection<MapSnapshotter.Options>) options).Peer, options),
				"MapLibre options retain one canonical peer");
			using var snapshotter = new MapSnapshotter (this, options);
			using var callback = new SnapshotCallback ();
			try {
				snapshotter.Start (callback, callback);
				using var snapshot = await callback.Completion.Task.WaitAsync (TimeSpan.FromSeconds (30));
				using var bitmap = snapshot.Bitmap;
				Check (callback.Calls == 1 && bitmap.Width == 96 && bitmap.Height == 64,
					"MapLibre native renderer invokes the managed snapshot callback");
				var color = bitmap.GetPixel (48, 32);
				Check (Math.Abs (((color >> 16) & 0xff) - 32) <= 2 && Math.Abs (((color >> 8) & 0xff) - 160) <= 2 &&
					Math.Abs ((color & 0xff) - 80) <= 2,
					"MapLibre native renderer produces the requested offline style pixels");
				using var center = new PointF (48, 32);
				using var coordinate = snapshot.LatLngForPixel (center);
				using var returned = snapshot.PixelForLatLng (coordinate);
				Check (Math.Abs (returned.X - center.X) < 0.1f && Math.Abs (returned.Y - center.Y) < 0.1f,
					"MapLibre native projection roundtrip");
				using var output = new MemoryStream ();
				if (!bitmap.Compress (Bitmap.CompressFormat.Png, 100, output))
					throw new InvalidOperationException ("Cannot preserve the rendered snapshot.");
				Artifact ("offline-map.png", output.ToArray ());
			} finally {
				snapshotter.Cancel ();
			}
		}

		[Register ("net/dot/android/thirdparty/MapLibreSnapshotCallback")]
		sealed class SnapshotCallback : Java.Lang.Object, MapSnapshotter.ISnapshotReadyCallback, MapSnapshotter.IErrorHandler
		{
			internal TaskCompletionSource<MapSnapshot> Completion { get; } =
				new TaskCompletionSource<MapSnapshot> (TaskCreationOptions.RunContinuationsAsynchronously);
			internal int Calls { get; private set; }

			public void OnSnapshotReady (MapSnapshot snapshot)
			{
				Calls++;
				Completion.TrySetResult (snapshot);
			}

			public void OnError (string error) => Completion.TrySetException (new InvalidOperationException (error));
		}
	}
}
