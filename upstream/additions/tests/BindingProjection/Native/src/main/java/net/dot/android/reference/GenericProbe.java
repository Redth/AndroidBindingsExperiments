package net.dot.android.reference;

import androidx.annotation.RestrictTo;
import androidx.annotation.VisibleForTesting;
import java.util.ArrayList;
import java.util.List;

public final class GenericProbe {
    private GenericProbe() {}

    public static class Box<T> {
        private T value;
        public T publicValue;

        public Box(T value) { this.value = value; this.publicValue = value; }
        public T get() { return value; }
        public void set(T value) { this.value = value; }
        public T[] array(T[] values) { return values; }
        public int[] primitiveArray(int[] values) { return values; }
        public <V> V echo(V value) { return value; }

        @RestrictTo(RestrictTo.Scope.LIBRARY)
        public T internalValue() { return value; }

        @VisibleForTesting(otherwise = VisibleForTesting.PROTECTED)
        public T testValue() { return value; }
    }

    public static final class StringBox extends Box<String> {
        public StringBox(String value) { super(value); }
    }

    public static final class ListBox<U> extends Box<List<U>> {
        public ListBox(List<U> value) { super(value); }
    }

    public static final class BoundedBox<T extends CharSequence & Comparable<T>> extends Box<T> {
        public BoundedBox(T value) { super(value); }
    }

    public interface Record {
        String getName();
    }

    public static final class TextRecord implements Record {
        private final String name;
        public TextRecord(String name) { this.name = name; }
        public String getName() { return name; }
        public <V> V echo(V value) { return value; }
    }

    public static final class RecordResponse<T extends Record> {
        private final T record;
        public RecordResponse(T record) { this.record = record; }
        public T getRecord() { return record; }
        public <R extends Record> RecordResponse<R> replace(R value) { return new RecordResponse<R>(value); }
    }

    public static <T extends Record> RecordResponse<T> readRecord(T record) {
        return new RecordResponse<T>(record);
    }

    public interface Source<T> {
        T read();
        void write(T value);
    }

    public static <T> T roundtrip(Source<T> source, T value) {
        source.write(value);
        return source.read();
    }

    public static List<? super String> lowerBoundedValues() {
        List<Object> values = new ArrayList<>();
        values.add(Integer.valueOf(23));
        values.add("text");
        return values;
    }

    public static Box<? extends Number> covariantBox() {
        return new Box<Integer>(Integer.valueOf(31));
    }

    @RestrictTo(RestrictTo.Scope.LIBRARY)
    public static final class InternalResult {
        @Override public String toString() { return "opaque-result"; }
    }

    public static InternalResult opaqueResult() { return new InternalResult(); }
}
