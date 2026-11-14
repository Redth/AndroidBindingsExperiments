using System;
using System.Collections.Generic;
using System.Linq;
using MonoDroid.Generation;
using Xamarin.SourceWriter;
using Java.Interop.Tools.Generator.Semantics;

namespace generator.SourceWriters
{
	public class KotlinSuspendMethod : MethodWriter
	{
		const string runtime = "global::Java.Interop.Kotlin.KotlinCoroutines";

		public static void AddMethods (TypeWriter writer, GenBase type, CodeGenerationOptions opt)
		{
			if (!opt.ExperimentalKotlinAsync)
				return;

			var names = new HashSet<string> (writer.Methods.Select (m => m.Name), StringComparer.Ordinal);
			foreach (var group in type.Methods.Where (m => m.KotlinSuspend && m.IsValid &&
				(type is not InterfaceGen || opt.SupportDefaultInterfaceMethods || (!m.IsStatic && !m.IsInterfaceDefaultMethod)))
				.GroupBy (m => m.AdjustedName).OrderBy (g => g.Key, StringComparer.Ordinal)) {
				var name = group.Key + "Async";
				// A convenience must never replace a real native member.
				if (type.ContainsName (name))
					name = group.Key + "KotlinAsync";
				for (var suffix = 2; type.ContainsName (name) || !names.Add (name); suffix++)
					name = group.Key + "KotlinAsync" + suffix;
				foreach (var method in group) {
					if (method.Parameters.Count == 0 ||
						!method.JniSignature.EndsWith ("Lkotlin/coroutines/Continuation;)Ljava/lang/Object;", StringComparison.Ordinal))
						throw new InvalidOperationException ($"Invalid suspend ABI: {type.JavaName}.{method.JavaName}{method.JniSignature}.");
					writer.Methods.Add (new KotlinSuspendMethod (method, name, opt));
				}
			}
		}

		KotlinSuspendMethod (Method method, string name, CodeGenerationOptions opt)
		{
			Name = name;
			IsStatic = method.IsStatic;
			for (var basis = method.DeclaringType.BaseGen; basis != null; basis = basis.BaseGen)
				if (basis.IsGeneratable && basis.GetAllMethods ().Any (m => m.KotlinSuspend && m.AdjustedName == method.AdjustedName &&
					m.Parameters.Select (p => p.Type).SequenceEqual (method.Parameters.Select (p => p.Type))))
					IsShadow = true;
			SetVisibility (method.DeclaringType is InterfaceGen ? "" : method.Visibility);
			SourceWriterExtensions.AddSupportedOSPlatform (Attributes, method, opt);
			SourceWriterExtensions.AddObsolete (Attributes, method.Deprecated, opt, deprecatedSince: method.DeprecatedSince);
			SourceWriterExtensions.AddMethodCustomAttributes (Attributes, method);

			var parameters = new ParameterList ();
			foreach (var parameter in method.Parameters.Take (method.Parameters.Count - 1))
				parameters.Add (parameter);
			this.AddMethodParameters (parameters, opt, allowParamArray: false);
			var token = "cancellationToken";
			while (parameters.Any (p => p.Name == token))
				token = "_" + token;
			Parameters.Add (new OptionalParameter (token, "global::System.Threading.CancellationToken", "default"));
			var context = "coroutineContext";
			while (parameters.Any (p => p.Name == context))
				context = "_" + context;
			Parameters.Add (new OptionalParameter (context, "global::Kotlin.Coroutines.ICoroutineContext" + opt.NullableOperator, "null"));

			var continuation = "__continuation";
			while (parameters.Any (p => p.Name == continuation))
				continuation = "_" + continuation;
			var continuation_type = opt.GetTypeReferenceName (method.Parameters.Last ()).TrimEnd ('?');
			var native_continuation = $"global::Java.Interop.JavaObjectExtensions.JavaCast<{continuation_type}> ({continuation})";
			var arguments = string.Join (", ", parameters.Select (p => opt.GetSafeIdentifier (p.Name)).Append (native_continuation));
			var invoke = $"{continuation} => {method.AdjustedName} ({arguments})";
			var (result, converter) = GetResult (method, opt);
			if (result == null) {
				ReturnType = new TypeReferenceWriter ("global::System.Threading.Tasks.Task");
				Body.Add ($"return {runtime}.RunUnitAsync ({invoke}, {token}, {context});");
			} else {
				ReturnType = new TypeReferenceWriter ($"global::System.Threading.Tasks.Task<{result}>");
				Body.Add ($"return {runtime}.RunAsync ({invoke}, {converter}, {token}, {context});");
			}
		}

		internal static (string type, string converter) GetResult (Method method, CodeGenerationOptions opt)
		{
			if (method.KotlinReturnTypeContract != null) {
				if (method.KotlinReturnTypeContract.Classifier == "kotlin/Unit" && !method.KotlinReturnTypeContract.Nullable)
					return (null, null);
				return KotlinTypeProjection.GetValueType (method.KotlinReturnTypeContract, opt);
			}
			return GetSimpleResult (method.KotlinSuspendReturnType, method.KotlinSuspendReturnNullable, opt);
		}

		internal static (string type, string converter) GetSimpleResult (string classifier, bool nullable, CodeGenerationOptions opt)
		{
			var suffix = nullable ? "?" : "";
			switch (classifier) {
			case "kotlin/Unit" when !nullable:
				return (null, null);
			case "kotlin/String":
				return ("string" + (nullable ? opt.NullableOperator : ""), runtime + (nullable ? ".GetNullableString" : ".GetString"));
			case "kotlin/Int":
				return ("int" + suffix, runtime + (nullable ? ".GetNullableInt32" : ".GetInt32"));
			case "kotlin/Boolean":
				return ("bool" + suffix, runtime + (nullable ? ".GetNullableBoolean" : ".GetBoolean"));
			case "kotlin/Long":
				return ("long" + suffix, runtime + (nullable ? ".GetNullableInt64" : ".GetInt64"));
			case "kotlin/Double":
				return ("double" + suffix, runtime + (nullable ? ".GetNullableDouble" : ".GetDouble"));
			case "kotlin/Float":
				return ("float" + suffix, runtime + (nullable ? ".GetNullableSingle" : ".GetSingle"));
			case "kotlin/Short":
				return ("short" + suffix, runtime + (nullable ? ".GetNullableInt16" : ".GetInt16"));
			case "kotlin/Byte":
				return ("sbyte" + suffix, runtime + (nullable ? ".GetNullableSByte" : ".GetSByte"));
			case "kotlin/Char":
				return ("char" + suffix, runtime + (nullable ? ".GetNullableChar" : ".GetChar"));
			default:
				// Complex results retain the honest erased peer until a typed
				// projection can preserve their complete Kotlin contract.
				return ("global::Java.Lang.Object" + opt.NullableOperator, "value => value");
			}
		}

		internal sealed class OptionalParameter : MethodParameterWriter
		{
			readonly string defaultValue;

			public OptionalParameter (string name, string type, string defaultValue) : base (name, new TypeReferenceWriter (type))
			{
				this.defaultValue = defaultValue;
			}

			public override void WriteParameter (CodeWriter writer)
			{
				base.WriteParameter (writer);
				writer.Write (" = " + defaultValue);
			}
		}
	}
}
