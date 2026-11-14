#nullable enable
using Androidx.Activity;
using Androidx.Lifecycle;
using Androidx.Savedstate;

namespace BindingProjection
{
	public static class GroupedConsumer
	{
		public static ILifecycleOwner AsLifecycleOwner (ComponentActivity activity) => activity;

		public static ISavedStateRegistryOwner AsSavedStateRegistryOwner (ComponentActivity activity) => activity;

		public static SavedStateRegistry GetSavedStateRegistry (ComponentActivity activity) => activity.SavedStateRegistry;
	}
}
