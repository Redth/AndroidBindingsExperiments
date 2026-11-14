using Android.Content;
using Android.Runtime;
using Android.Views;
using AndroidX.SwipeRefreshLayout.Widget;
using Microsoft.Maui.Platform;

namespace Legacy.Consumer;

public static class LegacyApi
{
	public static SwipeRefreshLayout Create (Context context) => new LegacySwipe (context);
	public static SwipeRefreshLayout Identity (SwipeRefreshLayout view) => view;
	public static void SetRefreshing (SwipeRefreshLayout view, bool refreshing) => view.Refreshing = refreshing;
	public static bool ReadRefreshing (SwipeRefreshLayout view) => view.Refreshing;
	public static void SetScrollCallback (SwipeRefreshLayout view, SwipeRefreshLayout.IOnChildScrollUpCallback callback) =>
		view.SetOnChildScrollUpCallback (callback);
	public static bool Measure (SwipeRefreshLayout view)
	{
		view.OnMeasure (View.MeasureSpec.MakeMeasureSpec (80, MeasureSpecMode.Exactly),
			View.MeasureSpec.MakeMeasureSpec (80, MeasureSpecMode.Exactly));
		return view.MeasuredWidth == 80 && view.MeasuredHeight == 80;
	}
}

[Register ("net/dot/android/compat/LegacySwipe")]
public sealed class LegacySwipe : MauiSwipeRefreshLayout
{
	public LegacySwipe (Context context) : base (context) { }
	public int ScrollCalls { get; private set; }
	public override bool CanChildScrollUp ()
	{
		ScrollCalls++;
		return base.CanChildScrollUp ();
	}
}

[Register ("net/dot/android/compat/LegacyScrollCallback")]
public sealed class LegacyScrollCallback : Java.Lang.Object, SwipeRefreshLayout.IOnChildScrollUpCallback
{
	public int Calls { get; private set; }
	public SwipeRefreshLayout? LastParent { get; private set; }
	public bool CanChildScrollUp (SwipeRefreshLayout? parent, View? child)
	{
		Calls++;
		LastParent = parent;
		return true;
	}
}

[Register ("net/dot/android/compat/LegacyRefreshListener")]
public sealed class LegacyRefreshListener : Java.Lang.Object, SwipeRefreshLayout.IOnRefreshListener
{
	public int Calls { get; private set; }
	public void OnRefresh () => Calls++;
}
