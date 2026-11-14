using System;
using System.Linq;
using System.Xml.Linq;

namespace Java.Interop.Tools.Generator.Semantics
{
	public static class BindingPublisherPolicy
	{
		internal const string HiddenAttribute = "[global::System.ComponentModel.EditorBrowsable (global::System.ComponentModel.EditorBrowsableState.Never)]";
		static readonly string [] GoogleObfuscationNamespaces = [
			"com.google.android.gms.", "com.google.android.play.",
			"com.google.android.libraries.vision.", "com.google.firebase.", "com.google.mlkit.",
		];

		public static string InfrastructureReason (string javaType, XElement declaration)
		{
			if ((string) declaration.Attribute ("obfuscated") == "false" ||
				!GoogleObfuscationNamespaces.Any (prefix => javaType.StartsWith (prefix, StringComparison.Ordinal)))
				return "";
			if (declaration.Name == "class" || declaration.Name == "interface") {
				var package = (string) declaration.Parent?.Attribute ("name") ?? "";
				if (javaType.StartsWith ("com.google.android.gms.", StringComparison.Ordinal) &&
					Array.IndexOf (package.Split ('.'), "internal") >= 0)
					return "google-play-services-internal-package";
			}
			var name = (string) declaration.Attribute ("name") ?? "";
			var last = name.Substring (Math.Max (name.LastIndexOf ('.'), name.LastIndexOf ('$')) + 1);
			return last.StartsWith ("zz", StringComparison.Ordinal) ? "google-obfuscated-implementation" : "";
		}

		public static void RestoreExplicitExceptions (XDocument projected)
		{
			foreach (var declaration in projected.Descendants ().Where (e => (string) e.Attribute ("obfuscated") == "false" &&
				((string) e.Attribute ("api-classification-reason") ?? "").StartsWith ("google-", StringComparison.Ordinal))) {
				var visibility = (string) declaration.Attribute ("api-source-visibility");
				if (visibility != "public" && visibility != "protected")
					continue;
				if ((declaration.Name == "class" || declaration.Name == "interface") &&
					(string) declaration.Attribute ("visibility") != "internal")
					continue;
				// Explicit source restrictions are independent of the publisher's naming policy.
				if ((string) declaration.Attribute ("api-internal") == "true" || declaration.Attribute ("annotated-visibility") != null ||
					declaration.Attribute ("api-visible-for-testing") != null)
					continue;
				declaration.SetAttributeValue ("visibility", visibility);
				declaration.SetAttributeValue ("api-classification", visibility == "public" ? "Public" : "Protected");
				declaration.SetAttributeValue ("api-classification-reason", "explicit-obfuscation-policy-exception");
				if ((string) declaration.Attribute ("api-publisher-hidden-attribute") == "true") {
					var attributes = (string) declaration.Attribute ("customAttributes") ?? "";
					declaration.SetAttributeValue ("customAttributes", attributes.Replace (HiddenAttribute, ""));
					declaration.Attribute ("api-publisher-hidden-attribute")?.Remove ();
				}
			}
		}
	}
}
