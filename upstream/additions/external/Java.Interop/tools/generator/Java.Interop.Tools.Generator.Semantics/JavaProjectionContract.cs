using MonoDroid.Generation;
using Xamarin.SourceWriter;

namespace Java.Interop.Tools.Generator.Semantics
{
	static class JavaProjectionContract
	{
		internal const string MetadataName = "Java.Interop.Kotlin.IJavaProjection`1";

		internal static string Name (string peer) => "global::Java.Interop.Kotlin.IJavaProjection<" + peer + ">";

		internal static bool IsAvailable (GenBase peer, CodeGenerationOptions options)
		{
			if (peer == null || !peer.IsAcw || peer.IsApiExcluded || peer is InterfaceGen iface && iface.IsConstSugar (options))
				return false;
			for (var current = peer; current != null; current = current.EnclosingType)
				if (current.RawVisibility != "public")
					return false;
			return peer.IsGeneratable ? options.ExperimentalGenericFacades : peer.HasJavaProjection;
		}

		internal static void Add (TypeWriter writer, GenBase peer, CodeGenerationOptions options)
		{
			if (!options.ExperimentalGenericFacades || !IsAvailable (peer, options))
				return;
			var name = options.GetOutputName (peer.FullName);
			var contract = Name (name);
			writer.Implements.Add (contract);
			var property = new PropertyWriter {
				Name = "Peer", PropertyType = new TypeReferenceWriter (name),
				ExplicitInterfaceImplementation = contract, HasGet = true,
			};
			property.GetBody.Add ("return this;");
			writer.Properties.Add (property);
		}
	}
}
