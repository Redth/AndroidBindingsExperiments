using PackagePreview;

namespace Preview.Consumer;

public static class Consumer
{
	public static string? Echo (string value)
	{
		using var box = new PreviewBox ();
		return box.HandwrittenEcho (value);
	}
}
