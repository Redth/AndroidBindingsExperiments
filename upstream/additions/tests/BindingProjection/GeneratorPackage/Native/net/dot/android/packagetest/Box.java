package net.dot.android.packagetest;

public class Box<T> {
    private T value;
    public Box() { }
    public T getValue() { return value; }
    public void setValue(T value) { this.value = value; }
    public String echo(String value) { return value; }
    public void fail() { throw new IllegalStateException("package-preview-exception"); }
    public int invoke(Listener listener, int value) {
        listener.onValue(value);
        return value;
    }
    public interface Listener {
        void onValue(int value);
    }
}
