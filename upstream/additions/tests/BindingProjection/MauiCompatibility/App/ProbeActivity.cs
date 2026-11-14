using System;
using Android.App;
using Android.OS;
using Android.Util;
using Android.Views;
using AndroidX.SwipeRefreshLayout.Widget;
using Java.Interop;
using Legacy.Consumer;
using Microsoft.Maui.Platform;

namespace Compat.App;

[Activity (Name = "net.dot.android.compatpilot.ProbeActivity", MainLauncher = true, Exported = true)]
public sealed class ProbeActivity : Activity
{
	protected override void OnCreate (Bundle? savedInstanceState)
	{
		base.OnCreate (savedInstanceState);
		try {
			int checks = RunChecks ();
			Log.Info ("MauiCompatProbe", $"PASS {checks}/13");
		} catch (Exception error) {
			Log.Error ("MauiCompatProbe", "FAIL " + error);
		}
	}

	unsafe int RunChecks ()
	{
		int checks = 0;
		void Check (bool success, string name)
		{
			if (!success)
				throw new InvalidOperationException (name);
			checks++;
		}
		using var view = LegacyApi.Create (this);
		var legacy = view as LegacySwipe ?? throw new InvalidOperationException ("Precompiled subclass was not created.");
		Check (view is MauiSwipeRefreshLayout, "Actual shipped MAUI superclass");
		Check (view.GetType ().BaseType == typeof (MauiSwipeRefreshLayout), "Unchanged consumer hierarchy");
		Check (ReferenceEquals (LegacyApi.Identity (view), view), "Old signature accepts new implementation");
		LegacyApi.SetRefreshing (view, true);
		Check (LegacyApi.ReadRefreshing (view), "Precompiled property write/read");
		LegacyApi.SetRefreshing (view, false);
		Check (!view.Refreshing, "New caller observes old caller mutation");
		Check (LegacyApi.Measure (view), "Shipped MAUI OnMeasure calls regenerated public virtual slot");
#if SEMANTIC_BINDING
		view.InvokeOnMeasure (View.MeasureSpec.MakeMeasureSpec (80, MeasureSpecMode.Exactly),
			View.MeasureSpec.MakeMeasureSpec (80, MeasureSpecMode.Exactly));
		Check (view.MeasuredWidth == 80, "New additive API works on the same legacy instance");
#else
		Check (LegacyApi.Measure (view), "Original-binding baseline control");
#endif
		using var callback = new LegacyScrollCallback ();
		LegacyApi.SetScrollCallback (view, callback);
		var members = new JniPeerMembers ("net/dot/android/compat/CompatibilityDriver", typeof (Java.Lang.Object));
		JniArgumentValue* args = stackalloc JniArgumentValue [3];
		args [0] = new JniArgumentValue (view);
		int before = legacy.ScrollCalls;
		bool scroll = members.StaticMethods.InvokeBooleanMethod ("canScroll.(Landroidx/swiperefreshlayout/widget/SwipeRefreshLayout;)Z", args);
		Check (scroll && legacy.ScrollCalls > before, "Java virtual dispatch reaches unchanged managed subclass");
		Check (callback.Calls > 0, "Regenerated binding invokes old callback interface");
		Check (ReferenceEquals (callback.LastParent, view), "Callback preserves canonical managed peer");
		using var child = new View (this);
		args [0] = new JniArgumentValue (callback);
		args [1] = new JniArgumentValue (view);
		args [2] = new JniArgumentValue (child);
		int callbackBefore = callback.Calls;
		Check (members.StaticMethods.InvokeBooleanMethod ("childCallback.(Landroidx/swiperefreshlayout/widget/SwipeRefreshLayout$OnChildScrollUpCallback;Landroidx/swiperefreshlayout/widget/SwipeRefreshLayout;Landroid/view/View;)Z", args) &&
			callback.Calls == callbackBefore + 1, "Old interface implementation called directly from Java");
		using var listener = new LegacyRefreshListener ();
		args [0] = new JniArgumentValue (listener);
		members.StaticMethods.InvokeVoidMethod ("refresh.(Landroidx/swiperefreshlayout/widget/SwipeRefreshLayout$OnRefreshListener;)V", args);
		Check (listener.Calls == 1, "Old listener implementation called from Java");
		Check (typeof (SwipeRefreshLayout).Assembly.GetName ().Name == "Xamarin.AndroidX.SwipeRefreshLayout" &&
			typeof (SwipeRefreshLayout).Assembly.GetName ().Version == new Version (1, 0, 0, 0), "Original assembly owner");
		return checks;
	}
}
