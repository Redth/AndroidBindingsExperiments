package net.dot.android.bindingcompiler;

import java.io.Serializable;
import java.util.Iterator;

public final class ContractProbe {
    public static final class DefaultIterator implements Iterator<String> {
        public boolean hasNext() { return true; }
        public String next() { return "next"; }
    }

    public interface Mapper<T> {
        T map(T value);
    }

    public static final class StringMapper implements Mapper<String> {
        public String map(String value) { return "mapped:" + value; }
    }

    public interface First {
        Object read();
    }

    public interface Second {
        Object read();
    }

    public interface Combined extends First, Second {
    }

    public static Object readCombined(Combined peer) { return peer.read(); }

    public interface PropertyContract {
        String getValue();
        void setValue(String value);
    }

    public abstract static class PropertyBase implements PropertyContract {
    }

    private static final class HiddenPropertyPeer extends PropertyBase {
        private String value = "initial";
        public String getValue() { return value; }
        public void setValue(String value) { this.value = value; }
    }

    public static PropertyBase createPropertyPeer() { return new HiddenPropertyPeer(); }

    public abstract static class ArrayBase<T> {
        public abstract void put(T value);
    }

    public static final class ArrayPeer extends ArrayBase<Serializable[]> {
        private int lastSize;
        public void put(Serializable[] value) { lastSize = value.length; }
        public int getLastSize() { return lastSize; }
    }

    public static class zzg {
        public int getAnswer() { return 42; }
    }

    public static final class PublicPeer extends zzg {
    }

    public interface Summary<T extends zzg> {
        CharSequence summarize(T value);
    }

    public static final class PublicSummary implements Summary<PublicPeer> {
        public CharSequence summarize(PublicPeer value) { return "answer:" + value.getAnswer(); }
    }

    public static final PublicPeer instance = new PublicPeer();
    public static PublicPeer instance(int ignored) { return new PublicPeer(); }

    public static final PublicPeer DetectionApi = new PublicPeer();
    public static final PublicPeer DETECTION_API = new PublicPeer();
}
