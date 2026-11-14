using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MonoDroid.Generation;
using Xamarin.SourceWriter;

namespace generator.SourceWriters
{
	internal static class KotlinDefaultConstructor
	{
		public static void AddConstructors (ClassWriter writer, ClassGen type, CodeGenerationOptions options, CodeGeneratorContext context)
		{
			if (!options.ExperimentalKotlinAsync || type.IsAbstract)
				return;
			var names = new HashSet<string> (writer.Methods.Select (m => m.Name).Append (type.Name), StringComparer.Ordinal);
			foreach (var constructor in type.Ctors.Where (c => c.Visibility == "public" && !string.IsNullOrEmpty (c.KotlinDefaultJniSignature))) {
				if (constructor.KotlinDefaultMaskCount <= 0 ||
					!constructor.Parameters.Any (p => p.KotlinDefaultArgumentIndex.HasValue))
					throw new InvalidDataException ($"Invalid default-constructor facts for {type.JavaName}.");
				var raw = new Ctor (type) {
					Name = type.Name, Visibility = "private",
					IsNonStaticNestedType = constructor.IsNonStaticNestedType,
				};
				foreach (var parameter in constructor.Parameters) {
					var clone = parameter.Clone ();
					clone.NotNull = false;
					raw.Parameters.Add (clone);
				}
				var occupied = new HashSet<string> (raw.Parameters.Select (p => p.Name), StringComparer.Ordinal);
				string Allocate (string name)
				{
					while (!occupied.Add (name))
						name = "_" + name;
					return name;
				}
				for (var i = 0; i < constructor.KotlinDefaultMaskCount; i++)
					raw.Parameters.Add (new Parameter (Allocate ("__mask" + i), "int", null, false));
				var marker = options.SymbolTable.Lookup ("kotlin.jvm.internal.DefaultConstructorMarker");
				raw.Parameters.Add (new Parameter (Allocate ("__marker"),
					marker is GenBase { RawVisibility: "public" } ? "kotlin.jvm.internal.DefaultConstructorMarker" : "java.lang.Object", null, false) {
					JniTypeOverride = "Lkotlin/jvm/internal/DefaultConstructorMarker;",
				});
				if (!raw.Validate (options, type.GetValidationTypeParameters (options), context) || raw.JniSignature != constructor.KotlinDefaultJniSignature)
					throw new InvalidDataException ($"Default-constructor ABI mismatch on {type.JavaName}: {raw.JniSignature} / {constructor.KotlinDefaultJniSignature}.");
				KotlinDefaultMethod.RestoreParameterProjections (constructor.Parameters, raw.Parameters, 0);
				if (type.Ctors.Any (c => c.Parameters.Select (p => p.Type).SequenceEqual (raw.Parameters.Select (p => p.Type))))
					throw new InvalidDataException ($"A masked constructor collides with an existing managed constructor on {type.JavaName}.");
				writer.Constructors.Add (new BoundConstructor (type, raw, type.InheritsObject, options, context, allowParamArray: false));
				var name = "CreateWithDefaults";
				while (!names.Add (name))
					name += "Value";
				var factory = new MethodWriter {
					Name = name, IsPublic = true, IsStatic = true,
					ReturnType = new TypeReferenceWriter (options.GetOutputName (type.FullName)),
				};
				KotlinDefaultMethod.AddParameters (factory, constructor.Parameters, options);
				SourceWriterExtensions.AddSupportedOSPlatform (factory.Attributes, constructor, options);
				var arguments = constructor.Parameters.Select (p => options.GetSafeIdentifier (p.Name) +
					(p.KotlinDefaultArgumentIndex.HasValue ? ".GetValueOrDefault ()" : "")).ToList ();
				arguments.AddRange (KotlinDefaultMethod.GetMasks (constructor.Parameters, constructor.KotlinDefaultMaskCount, options));
				arguments.Add ("(" + options.GetOutputName (raw.Parameters [raw.Parameters.Count - 1].Type) + ") null");
				factory.Body.Add ("return new " + options.GetOutputName (type.FullName) + " (" + string.Join (", ", arguments) + ");");
				writer.Methods.Add (factory);
			}
		}
	}
}
