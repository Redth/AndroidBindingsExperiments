#nullable enable
using System;
using Com.Google.Firebase;
using Com.Google.Firebase.Auth;
using Com.Google.Mlkit.Vision.Barcode;
using Com.Google.Mlkit.Vision.Common;
using GoogleTask = Com.Google.Android.Gms.Tasks.Task;

namespace BindingProjection
{
	public static class ConsolidatedConsumer
	{
		public static FirebaseApp GetApp (FirebaseAuth auth) => auth.App;

		public static GoogleTask SignIn (FirebaseAuth auth) => auth.SignInAnonymously ();

		public static GoogleTask Scan (IBarcodeScanner scanner, InputImage image) => scanner.Process (image);

		public static Type HealthConnectClientType => typeof (Androidx.Health.Connect.Client.IHealthConnectClient);
	}
}
