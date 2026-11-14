using System.Threading;
using System.Threading.Tasks;
using Androidx.Lifecycle;
using Net.Dot.Android.Reference;

namespace Reference.Writer
{
	public static class CounterWriter
	{
		public static System.Type ViewType => typeof (Local.SettingsView);
		public static Task<int> IncrementAsync (SettingsRepository repository, CancellationToken cancellationToken = default) =>
			new Local.SettingsView (repository).IncrementAsync (cancellationToken);

		public static void Notify (SettingsRepository repository, IChangeListener listener, ILifecycleOwner owner, int value)
		{
			repository.Dispatch (listener, owner, value);
		}
	}
}
