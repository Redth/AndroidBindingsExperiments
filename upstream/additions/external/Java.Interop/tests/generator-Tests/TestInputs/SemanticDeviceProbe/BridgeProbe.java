package net.dot.android.bindingcompiler;

import android.content.Context;
import android.content.Intent;
import androidx.activity.result.contract.ActivityResultContract;

public final class BridgeProbe {
    public static Intent createViaBase(ActivityResultContract contract, Context context, Object input) {
        return contract.createIntent(context, input);
    }

    public static Object parseViaBase(ActivityResultContract contract, int resultCode, Intent intent) {
        return contract.parseResult(resultCode, intent);
    }
}
