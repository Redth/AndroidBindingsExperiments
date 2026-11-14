using System;
using System.Linq;
using MonoDroid.Generation;
using Xamarin.Android.Tools.Bytecode;
using generator.SourceWriters;

namespace Java.Interop.Tools.Generator.Semantics
{
	internal static class KotlinTypeProjection
	{
		const string runtime = "global::Java.Interop.Kotlin.KotlinCoroutines";

		public static bool IsFlow (KotlinTypeContract type) => type != null &&
			(type.Classifier == "kotlinx/coroutines/flow/Flow" ||
			type.Classifier == "kotlinx/coroutines/flow/StateFlow" ||
			type.Classifier == "kotlinx/coroutines/flow/SharedFlow" ||
			type.Classifier == "kotlinx/coroutines/flow/MutableStateFlow" ||
			type.Classifier == "kotlinx/coroutines/flow/MutableSharedFlow");

		public static (string type, string converter) GetValueType (KotlinTypeContract type, CodeGenerationOptions options)
			=> GetValueType (type, options, 0);

		static (string type, string converter) GetValueType (KotlinTypeContract type, CodeGenerationOptions options, int depth)
		{
			if (type == null)
				return ("global::Java.Lang.Object" + options.NullableOperator, "__value => __value");
			var scalar = KotlinSuspendMethod.GetSimpleResult (type.Classifier, type.Nullable, options);
			if (type.Classifier == "kotlin/String" || type.Classifier == "kotlin/Int" ||
				type.Classifier == "kotlin/Boolean" || type.Classifier == "kotlin/Long" ||
				type.Classifier == "kotlin/Double" || type.Classifier == "kotlin/Float" ||
				type.Classifier == "kotlin/Short" || type.Classifier == "kotlin/Byte" || type.Classifier == "kotlin/Char")
				return scalar;
			if (depth > 32)
				throw new System.IO.InvalidDataException ("Kotlin managed projection exceeds its maximum nesting depth.");
			var collection = type.Classifier switch {
				"kotlin/collections/List" or "kotlin/collections/MutableList" => "List",
				"kotlin/collections/Set" or "kotlin/collections/MutableSet" => "Set",
				"kotlin/collections/Map" or "kotlin/collections/MutableMap" => "Map",
				_ => null,
			};
			if (collection != null) {
				var count = collection == "Map" ? 2 : 1;
				if (type.Arguments == null || type.Arguments.Count != count)
					throw new System.IO.InvalidDataException ($"Invalid Kotlin {collection} type argument count.");
				var arguments = type.Arguments.Select (a => GetReadType (a, options, depth + 1)).ToArray ();
				var name = "global::Java.Interop.Kotlin.Kotlin" + collection + "<" + string.Join (", ", arguments.Select (a => a.type)) + ">";
				var variable = "__kotlinCollection" + depth;
				return (name + (type.Nullable ? options.NullableOperator : ""),
					variable + " => " + runtime + ".Get" + (type.Nullable ? "Nullable" : "") + collection + " (" + variable + ", " +
					string.Join (", ", arguments.Select (a => a.converter)) + ")");
			}
			if (!string.IsNullOrEmpty (type.Classifier)) {
				var descriptor = KotlinUtilities.ConvertKotlinClassToJava (type.Classifier, convertUnsignedToPrimitive: false);
				var java_name = TypeNameUtilities.JniSignatureToJavaTypeName (descriptor);
				var symbol = java_name == null ? null : options.SymbolTable.Lookup (java_name);
				var peer = symbol as GenBase ?? (symbol as GenericSymbol)?.Gen;
				if (peer?.RawVisibility == "public") {
					var name = options.GetOutputName (peer.FullName);
					return (name + (type.Nullable ? options.NullableOperator : ""),
						runtime + (type.Nullable ? ".GetNullablePeer<" : ".GetPeer<") + name + ">");
				}
			}
			return ("global::Java.Lang.Object" + options.NullableOperator, "__value => __value");
		}

		static (string type, string converter) GetReadType (KotlinTypeArgumentContract argument, CodeGenerationOptions options, int depth)
		{
			switch (argument.Projection) {
			case "In":
			case "Star":
				return ("global::Java.Lang.Object" + options.NullableOperator, "__value => __value");
			case "":
			case "Inv":
			case "Invariant":
			case "Out":
				return GetValueType (argument.Type, options, depth);
			default:
				throw new System.IO.InvalidDataException ($"Unknown Kotlin type projection '{argument.Projection}'.");
			}
		}
	}
}
