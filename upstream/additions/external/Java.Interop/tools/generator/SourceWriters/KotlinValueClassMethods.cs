using System;
using System.IO;
using Java.Interop.Tools.Generator.Semantics;
using Java.Interop.Tools.TypeNameMappings;
using MonoDroid.Generation;
using Xamarin.SourceWriter;

namespace generator.SourceWriters
{
	internal static class KotlinValueClassMethods
	{
		public static void AddMethods (TypeWriter writer, ClassGen type, CodeGenerationOptions options)
		{
			if (!options.ExperimentalSemanticModel || string.IsNullOrEmpty (type.KotlinBoxJniSignature))
				return;
			ManagedContractProjection.PrepareValueClassHelperNames (type);
			var underlying = JavaNativeTypeManager.ReturnTypeFromSignature ("()" + type.KotlinValueClassUnderlyingJniType)?.Type
				?? throw new InvalidDataException ($"Invalid value-class backing descriptor on {type.JavaName}.");

			Method Create (string javaName, string name, string descriptor, bool isStatic, bool returnsPeer)
			{
				var method = new Method (type) {
					JavaName = javaName, Name = name, Visibility = "public",
					IsStatic = isStatic, IsFinal = true, JniSignatureOverride = descriptor,
					Return = returnsPeer ? type.JavaName : underlying,
					ManagedReturn = returnsPeer ? type.FullName : null,
					ReturnNotNull = returnsPeer || type.KotlinValueClassUnderlyingNotNull,
					KotlinInlineClassReturnJniType = !returnsPeer && type.IsKotlinInlineClass ? "L" + type.RawJniName + ";" : null,
				};
				method.FillReturnType ();
				if (isStatic)
					method.Parameters.Add (new Parameter ("value", underlying, null, false,
						notNull: type.KotlinValueClassUnderlyingNotNull));
				if (!method.Validate (options, type.TypeParameters, new CodeGeneratorContext ()))
					throw new InvalidDataException ($"Cannot project Kotlin value-class helper {type.JavaName}.{javaName}{descriptor}.");
				return method;
			}

			var box = Create ("box-impl", type.KotlinBoxManagedName, type.KotlinBoxJniSignature, true, true);
			writer.Methods.Add (new BoundMethod (type, box, options, false));
			writer.Methods.Add (new BoundMethod (type,
				Create ("unbox-impl", type.KotlinUnboxManagedName, type.KotlinUnboxJniSignature, false, false), options, false));
			if (string.IsNullOrEmpty (type.KotlinConstructorJniSignature))
				return;
			writer.Methods.Add (new BoundMethod (type,
				Create ("constructor-impl", type.KotlinCreateValueManagedName, type.KotlinConstructorJniSignature, true, false), options, false));
			var factory = new MethodWriter {
				Name = type.KotlinCreateManagedName, IsPublic = true, IsStatic = true,
				ReturnType = new TypeReferenceWriter (options.GetOutputName (type.FullName)),
			};
			factory.AddMethodParameters (box.Parameters, options);
			factory.Body.Add ($"return {type.KotlinBoxManagedName} ({type.KotlinCreateValueManagedName} (value));");
			writer.Methods.Add (factory);
		}
	}
}
