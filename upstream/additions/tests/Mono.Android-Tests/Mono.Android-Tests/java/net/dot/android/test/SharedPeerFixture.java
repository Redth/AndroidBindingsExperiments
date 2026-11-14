package net.dot.android.test;

import java.util.function.IntUnaryOperator;

public class SharedPeerFixture {
    private int value;
    private IntUnaryOperator callback;

    public SharedPeerFixture(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public void setValue(int value) {
        this.value = value;
    }

    public SharedPeerFixture echo(SharedPeerFixture other) {
        return other;
    }

    public IntUnaryOperator getCallback() {
        return callback;
    }

    public void setCallback(IntUnaryOperator callback) {
        this.callback = callback;
    }

    public int invokeCallback(int value) {
        if (callback == null)
            throw new IllegalStateException("No callback is attached.");
        return callback.applyAsInt(value);
    }

    public static class Derived extends SharedPeerFixture {
        public Derived(int value) {
            super(value);
        }

        @Override
        public int getValue() {
            return super.getValue() + 100;
        }
    }
}
