package net.dot.android.compat;

import androidx.swiperefreshlayout.widget.SwipeRefreshLayout;
import android.view.View;

public final class CompatibilityDriver {
    public static boolean canScroll(SwipeRefreshLayout view) {
        return view.canChildScrollUp();
    }
    public static boolean childCallback(SwipeRefreshLayout.OnChildScrollUpCallback callback,
            SwipeRefreshLayout parent, View child) {
        return callback.canChildScrollUp(parent, child);
    }
    public static void refresh(SwipeRefreshLayout.OnRefreshListener listener) {
        listener.onRefresh();
    }
}
