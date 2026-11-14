using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MonoDroid.Generation;
using Xamarin.SourceWriter;

namespace generator.SourceWriters
{
	internal sealed class KotlinDefaultMethod : MethodWriter
	{
		const string runtime = "global::Java.Interop.Kotlin.KotlinCoroutines";

		public static void AddMethods (TypeWriter writer, GenBase type, CodeGenerationOptions options)
		{
			if (!options.ExperimentalKotlinAsync)
				return;
			var names = new HashSet<string> (writer.Methods.Select (m => m.Name).Concat (type.NestedTypes.Select (t => t.Name)), StringComparer.Ordinal);
			var helpers = new Dictionary<string, ClassWriter> (StringComparer.Ordinal);
			foreach (var method in type.Methods.Where (m => m.IsValid && !string.IsNullOrEmpty (m.KotlinDefaultJniSignature))
				.OrderBy (m => m.JavaName + m.JniSignature, StringComparer.Ordinal)) {
				if (method.KotlinDefaultMaskCount <= 0 ||
					!method.Parameters.Any (p => p.KotlinDefaultArgumentIndex.HasValue) ||
					method.Parameters.Any (p => p.KotlinDefaultArgumentIndex < 0 ||
						p.KotlinDefaultArgumentIndex >= method.KotlinDefaultMaskCount * 32))
					throw new InvalidDataException ($"Invalid default-argument mask facts for {type.JavaName}.{method.JavaName}.");
				var owner = method.KotlinDefaultOwner;
				if (string.IsNullOrEmpty (owner))
					throw new InvalidDataException ($"No default helper owner for {type.JavaName}.{method.JavaName}.");
				var target = writer;
				var qualifier = "";
				if (owner != type.RawJniName) {
					if (!helpers.TryGetValue (owner, out var helper)) {
						var hash = Convert.ToHexString (SHA256.HashData (Encoding.UTF8.GetBytes (owner))).Substring (0, 12);
						var name = "__KotlinDefaultArguments_" + hash;
						helper = new ClassWriter { Name = name, IsPrivate = true, IsSealed = true, Inherits = "global::Java.Lang.Object" };
						helper.Attributes.Add (new RegisterAttr (owner, noAcw: true) { UseGlobal = true });
						helper.Fields.Add (new PeerMembersField (options, owner, name, false));
						var ctor = new ConstructorWriter { Name = name, IsPublic = true, BaseCall = "base (handle, transfer)" };
						ctor.Parameters.Add (new MethodParameterWriter ("handle", TypeReferenceWriter.IntPtr));
						ctor.Parameters.Add (new MethodParameterWriter ("transfer", new TypeReferenceWriter ("global::Android.Runtime.JniHandleOwnership")));
						helper.Constructors.Add (ctor);
						helpers.Add (owner, helper);
						writer.NestedTypes.Add (helper);
					}
					target = helper;
					qualifier = helper.Name + ".";
				} else if (type is InterfaceGen && !writer.Fields.Any (f => f.Name == "_members")) {
					writer.Fields.Add (new PeerMembersField (options, owner, type.Name, true));
				}

				var raw = method.Clone (type);
				raw.Name = "__InvokeKotlinDefault_" + method.NativeCallbackIdentity;
				raw.JavaName = method.JavaName + "$default";
				raw.Visibility = target == writer ? "private" : "internal";
				raw.IsStatic = raw.IsFinal = true;
				raw.IsAbstract = raw.IsVirtual = raw.IsOverride = false;
				raw.IsInterfaceDefaultMethod = false;
				raw.OverriddenInterfaceMethod = null;
				raw.ExplicitInterface = null;
				raw.ManagedOverride = "none";
				raw.JniSignatureOverride = method.KotlinDefaultJniSignature;
				raw.KotlinDefaultJniSignature = null;
				raw.KotlinSuspend = false;
				foreach (var parameter in raw.Parameters)
					parameter.NotNull = false;
				var raw_names = new HashSet<string> (raw.Parameters.Select (p => p.Name), StringComparer.Ordinal);
				string Allocate (string name)
				{
					while (!raw_names.Add (name))
						name = "_" + name;
					return name;
				}
				if (method.KotlinDefaultReceiver)
					raw.Parameters.AddFirst (new Parameter (Allocate ("__receiver"), type.JavaName, null, false, notNull: true));
				for (var mask = 0; mask < method.KotlinDefaultMaskCount; mask++)
					raw.Parameters.Add (new Parameter (Allocate ("__mask" + mask), "int", null, false));
				raw.Parameters.Add (new Parameter (Allocate ("__marker"), "java.lang.Object", null, false));
				if (!raw.Validate (options, type.GetValidationTypeParameters (options), new CodeGeneratorContext ()))
					throw new InvalidDataException ($"Cannot lower Kotlin default helper {owner}.{raw.JavaName}{raw.JniSignatureOverride}.");
				RestoreParameterProjections (method.Parameters, raw.Parameters, method.KotlinDefaultReceiver ? 1 : 0);
				raw.RetVal.SetManagedReferenceType (method.RetVal.Symbol);
				target.Methods.Add (new BoundMethod (type, raw, options, false, allowParamArray: false));
				var facade_name = method.AdjustedName + "WithDefaults" + (method.KotlinSuspend ? "Async" : "");
				while (type.ContainsName (facade_name) || !names.Add (facade_name))
					facade_name += "Value";
				writer.Methods.Add (new KotlinDefaultMethod (type, method, raw, qualifier, facade_name, options));
			}
		}

		KotlinDefaultMethod (GenBase type, Method method, Method raw, string qualifier, string name, CodeGenerationOptions options)
		{
			Name = name;
			IsStatic = method.IsStatic;
			SetVisibility (type is InterfaceGen ? "" : method.Visibility);
			SourceWriterExtensions.AddSupportedOSPlatform (Attributes, method, options);
			SourceWriterExtensions.AddObsolete (Attributes, method.Deprecated, options, deprecatedSince: method.DeprecatedSince);
			SourceWriterExtensions.AddMethodCustomAttributes (Attributes, method);
			var parameters = method.Parameters.Take (method.Parameters.Count - (method.KotlinSuspend ? 1 : 0)).ToArray ();
			AddParameters (this, parameters, options);
			var occupied = new HashSet<string> (parameters.Select (p => p.Name), StringComparer.Ordinal);
			string Allocate (string candidate)
			{
				while (!occupied.Add (candidate))
					candidate = "_" + candidate;
				return candidate;
			}
			var token = Allocate ("cancellationToken");
			var context = Allocate ("coroutineContext");
			var continuation = Allocate ("__continuation");
			var arguments = new List<string> ();
			if (method.KotlinDefaultReceiver) {
				if (method.IsStatic)
					throw new InvalidDataException ("A static Kotlin default adapter cannot use an implicit receiver.");
				arguments.Add ("this");
			}
			arguments.AddRange (parameters.Select (p => options.GetSafeIdentifier (p.Name) +
				(p.KotlinDefaultArgumentIndex.HasValue ? ".GetValueOrDefault ()" : "")));
			if (method.KotlinSuspend) {
				var continuation_type = options.GetTypeReferenceName (method.Parameters.Last ()).TrimEnd ('?');
				arguments.Add ($"global::Java.Interop.JavaObjectExtensions.JavaCast<{continuation_type}> ({continuation})");
				Parameters.Add (new KotlinSuspendMethod.OptionalParameter (token, "global::System.Threading.CancellationToken", "default"));
				Parameters.Add (new KotlinSuspendMethod.OptionalParameter (context, "global::Java.Interop.IJavaPeerable" + options.NullableOperator, "null"));
			}
			arguments.AddRange (GetMasks (parameters, method.KotlinDefaultMaskCount, options));
			arguments.Add ("null");
			var call = qualifier + raw.AdjustedName + " (" + string.Join (", ", arguments) + ")";
			if (method.KotlinSuspend) {
				var (result, converter) = KotlinSuspendMethod.GetResult (method, options);
				ReturnType = new TypeReferenceWriter ("global::System.Threading.Tasks.Task" + (result == null ? "" : "<" + result + ">"));
				Body.Add (result == null ? $"return {runtime}.RunUnitAsync ({continuation} => {call}, {token}, {context});" :
					$"return {runtime}.RunAsync ({continuation} => {call}, {converter}, {token}, {context});");
			} else {
				ReturnType = new TypeReferenceWriter (options.GetTypeReferenceName (method.RetVal));
				Body.Add ((method.IsVoid ? "" : "return ") + call + ";");
			}
		}

		internal static void AddParameters (ITakeParameters writer, IEnumerable<Parameter> parameters, CodeGenerationOptions options)
		{
			foreach (var parameter in parameters.OrderBy (p => p.KotlinDefaultArgumentIndex.HasValue)) {
				var parameter_type = options.GetTypeReferenceName (parameter);
				if (parameter_type.StartsWith ("params ", StringComparison.Ordinal))
					parameter_type = parameter_type.Substring ("params ".Length);
				writer.Parameters.Add (parameter.KotlinDefaultArgumentIndex.HasValue ?
					new KotlinSuspendMethod.OptionalParameter (options.GetSafeIdentifier (parameter.Name),
						"global::Java.Interop.Kotlin.KotlinOptional<" + parameter_type + ">", "default") :
					new MethodParameterWriter (options.GetSafeIdentifier (parameter.Name), new TypeReferenceWriter (parameter_type)));
			}
		}

		internal static void RestoreParameterProjections (ParameterList original, ParameterList lowered, int offset)
		{
			for (var index = 0; index < original.Count; index++) {
				var parameter = original [index];
				if (parameter.KotlinValueProjection == null &&
					(parameter.JniType.StartsWith ("L", StringComparison.Ordinal) || parameter.JniType.StartsWith ("[", StringComparison.Ordinal)))
					lowered [index + offset].SetManagedReferenceType (parameter.Symbol);
			}
		}

		internal static IEnumerable<string> GetMasks (IEnumerable<Parameter> parameters, int count, CodeGenerationOptions options)
		{
			for (var mask = 0; mask < count; mask++) {
				var bits = parameters.Where (p => p.KotlinDefaultArgumentIndex / 32 == mask).Select (p =>
					"(" + options.GetSafeIdentifier (p.Name) + ".IsSpecified ? 0 : (1 << " + p.KotlinDefaultArgumentIndex % 32 + "))");
				yield return string.Join (" | ", bits.DefaultIfEmpty ("0"));
			}
		}
	}
}
