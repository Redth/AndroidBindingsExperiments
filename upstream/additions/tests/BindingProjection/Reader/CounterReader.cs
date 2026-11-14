using System;
using System.Collections.Generic;
using System.Threading;
using Android.Content;
using Android.Runtime;
using Androidx.Lifecycle;
using Net.Dot.Android.Reference;

namespace Reference.Reader
{
	public static class CounterReader
	{
		public static Type ViewType => typeof (Local.SettingsView);
		public static SettingsRepository Open (Context context) => SettingsRepository.Open (context);

		public static IAsyncEnumerable<int> Observe (SettingsRepository repository, ILifecycleOwner owner, CancellationToken cancellationToken) =>
			new Local.SettingsView (repository).Observe (cancellationToken, repository.CollectionContext (owner));
	}

	[Register ("net/dot/android/reference/ManagedChangeListener")]
	public sealed class ManagedChangeListener : Java.Lang.Object, IChangeListener
	{
		public ILifecycleOwner? LastOwner { get; private set; }
		public int LastValue { get; private set; }
		public int Calls { get; private set; }

		public void Changed (int value, ILifecycleOwner owner)
		{
			LastOwner = owner;
			LastValue = value;
			Calls++;
		}
	}
}
