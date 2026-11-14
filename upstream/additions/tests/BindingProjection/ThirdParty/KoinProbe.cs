#nullable enable
using System;
using System.Collections;
using System.Threading.Tasks;
using Android.App;
using Android.Runtime;
using Org.Koin.Core;
using Org.Koin.Core.Definition;
using Org.Koin.Core.Instance;
using Org.Koin.Core.Module;
using Java.Interop.Kotlin;
using Kotlin.Jvm;

namespace ThirdPartyProbe
{
	[Activity (Name = "net.dot.android.thirdparty.ProbeActivity", MainLauncher = true, Exported = true)]
	public sealed class KoinProbe : OfflineProbeActivity
	{
		protected override int ExpectedChecks => 6;

		protected override Task RunChecksAsync ()
		{
			using var koin = new Koin ();
			using var module = new Module ();
			using var factory = new ManagedSingletonFactory ();
			using var stringClass = Java.Lang.Class.ForName ("java.lang.String");
			using var kotlinClass = JvmClassMappingKt.GetKotlinClass (stringClass);
			using var registry = koin.ScopeRegistry;
			using var rootScope = registry.RootScope;
			using var qualifier = rootScope.ScopeQualifier;
			using var singleton = Kind.Singleton ?? throw new InvalidOperationException ("No singleton kind.");
			using var definition = new BeanDefinition (qualifier, kotlinClass, null, factory, singleton, new ArrayList ());
			using var instanceFactory = new SingleInstanceFactory (definition);
			module.IndexPrimaryType (instanceFactory);
			try {
				koin.LoadModules (new [] { module }, allowOverride: false, createEagerInstances: false);
				Check (factory.Calls == 0, "Koin singleton provider is lazy");
				var view = new KoinView (koin);
				Check (view.Get<string> (kotlinClass, null, null) == "managed-singleton",
					"Koin generic explicit-KClass resolution returns a typed string");
				Check (view.Get<string> (kotlinClass, null, null) == "managed-singleton" && factory.Calls == 1,
					"Koin caches a singleton created by the managed Kotlin Function2 callback");
				using var integerClass = Java.Lang.Class.ForName ("java.lang.Integer");
				using var kotlinInteger = JvmClassMappingKt.GetKotlinClass (integerClass);
				Check (view.GetOrNull<int?> (kotlinInteger, null, null) == null, "Koin missing generic dependency retains null");
				using var property = new Java.Lang.String ("configured");
				koin.SetProperty ("mode", property);
				Check (view.GetProperty<string> ("mode") == "configured", "Koin generic property conversion");
				Check (ReferenceEquals (((IJavaProjection<Koin>) view).Peer, koin),
					"Koin typed view shares its canonical peer");
			} finally {
				koin.Close ();
			}
			return Task.CompletedTask;
		}

		[Register ("net/dot/android/thirdparty/KoinManagedSingletonFactory")]
		sealed class ManagedSingletonFactory : Java.Lang.Object, Kotlin.Jvm.Functions.IFunction2
		{
			internal int Calls { get; private set; }

			public Java.Lang.Object Invoke (Java.Lang.Object? scope, Java.Lang.Object? parameters)
			{
				Calls++;
				return new Java.Lang.String ("managed-singleton");
			}
		}
	}
}
