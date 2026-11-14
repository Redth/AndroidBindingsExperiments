using System;
using System.Collections.Generic;
using System.Linq;
using Java.Interop.Tools.Generator.Semantics;
using MonoDroid.Generation;
using Xamarin.SourceWriter;

namespace generator.SourceWriters
{
	public sealed class KotlinFlowMethod : MethodWriter
	{
		public static void AddMethods (TypeWriter writer, GenBase type, CodeGenerationOptions options)
		{
			if (!options.ExperimentalKotlinAsync)
				return;
			var names = new HashSet<string> (writer.Methods.Select (m => m.Name), StringComparer.Ordinal);
			foreach (var method in type.Methods.Concat (type.Properties.Select (p => p.Getter))
				.Where (m => m.IsValid && !m.KotlinSuspend && m.Visibility == "public" &&
					(type is not InterfaceGen || options.SupportDefaultInterfaceMethods || (!m.IsStatic && !m.IsInterfaceDefaultMethod)) &&
					KotlinTypeProjection.IsFlow (m.KotlinReturnTypeContract))) {
				var name = method.AdjustedName + "AsAsyncEnumerable";
				for (var suffix = 2; type.ContainsName (name) || !names.Add (name); suffix++)
					name = method.AdjustedName + "AsAsyncEnumerable" + suffix;
				writer.Methods.Add (new KotlinFlowMethod (type, method, name, options));
			}
		}

		KotlinFlowMethod (GenBase type, Method method, string name, CodeGenerationOptions options)
		{
			Name = name;
			IsStatic = method.IsStatic;
			for (var basis = type.BaseGen; basis != null; basis = basis.BaseGen)
				if (basis.IsGeneratable && basis.GetAllMethods ().Any (m => KotlinTypeProjection.IsFlow (m.KotlinReturnTypeContract) &&
					m.AdjustedName == method.AdjustedName && m.Parameters.Select (p => p.Type).SequenceEqual (method.Parameters.Select (p => p.Type))))
					IsShadow = true;
			SetVisibility (type is InterfaceGen ? "" : "public");
			SourceWriterExtensions.AddSupportedOSPlatform (Attributes, method, options);
			SourceWriterExtensions.AddObsolete (Attributes, method.Deprecated, options, deprecatedSince: method.DeprecatedSince);
			SourceWriterExtensions.AddMethodCustomAttributes (Attributes, method);
			var contract = method.KotlinReturnTypeContract;
			var argument = contract.Arguments?.SingleOrDefault ()?.Type;
			var (element, converter) = KotlinTypeProjection.GetValueType (argument, options);
			ReturnType = new TypeReferenceWriter ($"global::System.Collections.Generic.IAsyncEnumerable<{element}>" +
				(contract.Nullable ? options.NullableOperator : ""));
			this.AddMethodParameters (method.Parameters, options, allowParamArray: false);
			var token = "cancellationToken";
			while (method.Parameters.Any (p => p.Name == token))
				token = "_" + token;
			var context = "coroutineContext";
			while (method.Parameters.Any (p => p.Name == context))
				context = "_" + context;
			Parameters.Add (new KotlinSuspendMethod.OptionalParameter (token, "global::System.Threading.CancellationToken", "default"));
			Parameters.Add (new KotlinSuspendMethod.OptionalParameter (context, "global::Java.Interop.IJavaPeerable" + options.NullableOperator, "null"));
			var property = type.Properties.FirstOrDefault (p => p.Getter == method);
			var invoke = property != null ? property.AdjustedName :
				method.AdjustedName + " (" + string.Join (", ", method.Parameters.Select (p => options.GetSafeIdentifier (p.Name))) + ")";
			var local = "__flow";
			while (method.Parameters.Any (p => p.Name == local))
				local = "_" + local;
			Body.Add ($"var {local} = {invoke};");
			Body.Add ($"if ({local} == null)");
			Body.Add (contract.Nullable ? "\treturn null;" :
				"\tthrow new global::System.InvalidOperationException (\"Kotlin returned null for a non-null Flow.\");");
			Body.Add ($"return global::Java.Interop.Kotlin.KotlinCoroutines.AsAsyncEnumerable ({local}, {converter}, {token}, {context});");
		}
	}
}
