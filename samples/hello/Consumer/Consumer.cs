namespace Hello.Consumer;

public static class Consumer
{
    public static string? Hello(string name) => HelloBindings.Greeter.Greet(name);
}
