package net.dot.android.descriptor.fixtures;

import androidx.annotation.RestrictTo;
import androidx.annotation.VisibleForTesting;
import java.util.List;

public class JavaFixture<T extends Number & Comparable<T>> {
    public static final double NON_FINITE = Double.NEGATIVE_INFINITY;
    public static final float NOT_A_NUMBER = Float.NaN;
    public static final String LONE_SURROGATE = "\uD800";
    public List<? super String> lower(List<? super String> value) { return value; }
    public List<? extends T> upper(List<? extends T> value) { return value; }
    public T[] array(T[] value) { return value; }

    public static class Parent<T> {
        public T echo(T value) { return value; }
    }

    public static class Child extends Parent<String> {
        @Override public String echo(String value) { return value; }
        public Integer echo(Integer value) { return value; }
    }

    @RestrictTo(RestrictTo.Scope.LIBRARY_GROUP)
    public void internalOperation() {}

    @VisibleForTesting(otherwise = VisibleForTesting.PROTECTED)
    public void testOperation() {}
}
