#nullable enable
using System;
using System.Resources;

namespace Microsoft.Android.BindingGenerator.Semantic.Properties;

static class Resources
{
	static readonly ResourceManager manager = new (typeof (Resources));

	static string Get (string name) =>
		manager.GetString (name) ?? throw new InvalidOperationException ($"Missing binding compiler resource '{name}'.");

	public static string InvalidSelection => Get (nameof (InvalidSelection));
	public static string RequiresBindingProject => Get (nameof (RequiresBindingProject));
	public static string MissingTailImport => Get (nameof (MissingTailImport));
	public static string UnsupportedWorkload => Get (nameof (UnsupportedWorkload));
	public static string MissingToolOutput => Get (nameof (MissingToolOutput));
	public static string EmptyTransform => Get (nameof (EmptyTransform));
	public static string UnsupportedTransform => Get (nameof (UnsupportedTransform));
	public static string MissingNamespaceTransformFile => Get (nameof (MissingNamespaceTransformFile));
	public static string MissingNamespaceReplacement => Get (nameof (MissingNamespaceReplacement));
	public static string MissingPeerName => Get (nameof (MissingPeerName));
	public static string InvalidPeerName => Get (nameof (InvalidPeerName));
	public static string ToolFailure => Get (nameof (ToolFailure));
	public static string MissingManifestDirectory => Get (nameof (MissingManifestDirectory));
	public static string MissingSourceInclude => Get (nameof (MissingSourceInclude));
}
