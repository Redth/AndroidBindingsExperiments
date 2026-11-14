using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using MonoDroid.Generation;
using Xamarin.Android.Tools.Bytecode;

namespace Java.Interop.Tools.Generator.Semantics
{
	public sealed class JvmApiDescriptor : IDisposable
	{
		readonly JsonDocument document;
		readonly Dictionary<string, JsonElement> classes;

		public JvmApiDescriptor (string path)
		{
			using var stream = File.OpenRead (path);
			document = JsonDocument.Parse (stream);
			var root = document.RootElement;
			if (root.GetProperty ("schemaVersion").GetInt32 () != 1 || Text (root, "status") != "complete")
				throw new InvalidDataException ("Unsupported or incomplete JVM API descriptor.");
			classes = root.GetProperty ("classes").EnumerateArray ().ToDictionary (c => RequiredText (c, "binaryName"), StringComparer.Ordinal);
		}

		public XDocument Apply (XDocument input)
		{
			var result = new XDocument (input);
			foreach (var type in result.Root.Elements ("package").SelectMany (p => p.Elements ().Where (t => t.Name == "class" || t.Name == "interface"))) {
				var signature = BindingMethodDefinition.Required (type, "jni-signature");
				if (signature.Length < 3 || signature [0] != 'L' || signature [signature.Length - 1] != ';')
					throw new InvalidDataException ($"Invalid JVM descriptor '{signature}'.");
				var name = signature.Substring (1, signature.Length - 2);
				if (!classes.TryGetValue (name, out var descriptor))
					throw new InvalidDataException ($"The official descriptor does not contain input class '{name}'.");
				var jvm = descriptor.GetProperty ("jvm");
				var methods = jvm.GetProperty ("methods").EnumerateArray ()
					.ToDictionary (m => RequiredText (m, "name") + RequiredText (m, "descriptor"), StringComparer.Ordinal);
				var members = type.Elements ().Where (e => e.Name == "method" || e.Name == "constructor")
					.ToDictionary (e => (e.Name == "constructor" ? "<init>" : BindingMethodDefinition.Required (e, "name")) +
						BindingMethodDefinition.Required (e, "jni-signature"), StringComparer.Ordinal);
				foreach (var pair in members) {
					if (!methods.TryGetValue (pair.Key, out var native))
						throw new InvalidDataException ($"The descriptor disagrees with input member '{name}.{pair.Key}'.");
					ApplyAnnotations (pair.Value, native.GetProperty ("annotations"));
					if (native.TryGetProperty ("bridgeTargets", out var targets))
						pair.Value.SetAttributeValue ("jvm-bridge-targets", targets.GetRawText ());
				}
				var fields = jvm.GetProperty ("fields").EnumerateArray ()
					.ToDictionary (f => RequiredText (f, "name") + ":" + RequiredText (f, "descriptor"), StringComparer.Ordinal);
				foreach (var field in type.Elements ("field")) {
					var key = BindingMethodDefinition.Required (field, "name") + ":" + BindingMethodDefinition.Required (field, "jni-signature");
					if (!fields.TryGetValue (key, out var native))
						throw new InvalidDataException ($"The descriptor disagrees with input field '{name}.{key}'.");
					ApplyAnnotations (field, native.GetProperty ("annotations"));
				}
				ApplyAnnotations (type, jvm.GetProperty ("annotations"));
				type.SetAttributeValue ("api-descriptor-class-sha256", descriptor.GetProperty ("origin").GetProperty ("classSha256").GetString ());
				if (descriptor.TryGetProperty ("kotlin", out var kotlin) && kotlin.ValueKind != JsonValueKind.Null) {
					var status = RequiredText (kotlin, "status");
					type.SetAttributeValue ("kotlin-metadata-version", string.Join (".", kotlin.GetProperty ("version").EnumerateArray ().Select (v => v.GetInt32 ())));
					type.SetAttributeValue ("kotlin-metadata-status", status == "supported" ? "supported" : "unsupported");
					type.SetAttributeValue ("kotlin-semantics-source", "official-kotlin-metadata-jvm");
					if (status == "supported")
						ApplyKotlin (name, type, kotlin, members, methods, fields);
					else {
						foreach (var element in type.DescendantsAndSelf ())
							foreach (var attribute in element.Attributes ().Where (a => a.Name.LocalName.StartsWith ("kotlin-", StringComparison.Ordinal) &&
								a.Name != "kotlin-metadata-version" && a.Name != "kotlin-metadata-status" && a.Name != "kotlin-semantics-source").ToArray ())
								attribute.Remove ();
						RestorePhysicalVisibility (type, jvm);
						foreach (var pair in members)
							RestorePhysicalVisibility (pair.Value, methods [pair.Key]);
						foreach (var field in type.Elements ("field")) {
							var key = BindingMethodDefinition.Required (field, "name") + ":" + BindingMethodDefinition.Required (field, "jni-signature");
							RestorePhysicalVisibility (field, fields [key]);
						}
					}
				}
			}
			result.Root.SetAttributeValue ("kotlin-semantics-source", "official-kotlin-metadata-jvm");
			return result;
		}

		static void RestorePhysicalVisibility (XElement target, JsonElement jvm)
		{
			var visibility = (string) target.Attribute ("jvm-visibility") ?? RequiredText (jvm, "visibility");
			target.SetAttributeValue ("visibility", visibility == "package" ? "" : visibility);
		}

		void ApplyKotlin (string name, XElement type, JsonElement kotlin, Dictionary<string, XElement> members,
			Dictionary<string, JsonElement> methods, Dictionary<string, JsonElement> fields)
		{
			var scope = ClassScope (name);
			var container = kotlin.TryGetProperty ("class", out var klass) ? klass :
				kotlin.TryGetProperty ("package", out var package) ? package : default;
			if (container.ValueKind == JsonValueKind.Undefined)
				return;
			if (klass.ValueKind != JsonValueKind.Undefined) {
				ApplyVisibility (type, RequiredText (klass, "visibility"), Text (classes [name].GetProperty ("jvm"), "visibility"));
				ApplyTypeParameters (type, klass, scope);
				if (Text (klass, "visibility") is "local" or "private" or "private_to_this")
					return;
				foreach (var constructor in klass.GetProperty ("constructors").EnumerateArray ()) {
					var key = MemberKey (constructor, "signature");
					if (key != null && members.TryGetValue (key, out var member)) {
						ApplyVisibility (member, RequiredText (constructor, "visibility"), Text (methods [key], "visibility"));
						ApplyParameters (member, constructor.GetProperty ("parameters"), scope, suspend: false);
					}
				}
			}
			foreach (var function in container.GetProperty ("functions").EnumerateArray ()) {
				var key = MemberKey (function, "signature");
				if (key == null || !members.TryGetValue (key, out var member))
					continue;
				var local = ExtendScope (scope, function);
				ApplyVisibility (member, RequiredText (function, "visibility"), Text (methods [key], "visibility"));
				ApplyTypeParameters (member, function, local);
				var result = TypeContract (function.GetProperty ("returnType"), local);
				member.SetAttributeValue ("kotlin-return-type", result.ToJson ());
				member.SetAttributeValue ("kotlin-source-name", RequiredText (function, "name"));
				var suspend = function.GetProperty ("suspend").GetBoolean ();
				if (suspend) {
					member.SetAttributeValue ("kotlin-suspend", true);
					member.SetAttributeValue ("kotlin-suspend-return-nullable", result.Nullable);
					if (result.Classifier != null) {
						var descriptor = KotlinUtilities.ConvertKotlinClassToJava (result.Classifier);
						member.SetAttributeValue ("kotlin-suspend-return-type", TypeNameUtilities.JniSignatureToJavaTypeName (descriptor));
					}
				}
				ApplyParameters (member, function.GetProperty ("parameters"), local, suspend);
			}
			foreach (var property in container.GetProperty ("properties").EnumerateArray ()) {
				var local = ExtendScope (scope, property);
				var contract = TypeContract (property.GetProperty ("returnType"), local);
				foreach (var accessor in new [] { "getter", "setter" }) {
					var key = MemberKey (property, accessor);
					if (key == null || !members.TryGetValue (key, out var member))
						continue;
					var metadata = property.GetProperty (accessor);
					ApplyVisibility (member, Text (metadata, "visibility") ?? RequiredText (property, "visibility"), Text (methods [key], "visibility"));
					ApplyTypeParameters (member, property, local);
					if (accessor == "getter")
						member.SetAttributeValue ("kotlin-return-type", contract.ToJson ());
					else {
						var parameter = member.Elements ("parameter").LastOrDefault ();
						parameter?.SetAttributeValue ("kotlin-type", contract.ToJson ());
					}
				}
				var fieldKey = MemberKey (property, "field", field: true);
				if (fieldKey != null && fields.ContainsKey (fieldKey)) {
					var field = type.Elements ("field").SingleOrDefault (f => (string) f.Attribute ("name") + ":" + (string) f.Attribute ("jni-signature") == fieldKey);
					if (field != null) {
						ApplyVisibility (field, RequiredText (property, "visibility"), Text (fields [fieldKey], "visibility"));
						field.SetAttributeValue ("kotlin-type", contract.ToJson ());
					}
				}
			}
		}

		static void ApplyParameters (XElement member, JsonElement parameters, Dictionary<int, JsonElement> scope, bool suspend)
		{
			var source = parameters.EnumerateArray ().ToArray ();
			var native = member.Elements ("parameter").ToArray ();
			var offset = native.Length - source.Length - (suspend ? 1 : 0);
			if (offset < 0)
				throw new InvalidDataException ($"Kotlin parameter mapping exceeds JVM arity for '{member.Attribute ("name")?.Value}'.");
			for (var i = 0; i < source.Length; i++) {
				native [offset + i].SetAttributeValue ("kotlin-type", TypeContract (source [i].GetProperty ("type"), scope).ToJson ());
				if (source [i].GetProperty ("defaultValue").GetBoolean ())
					native [offset + i].SetAttributeValue ("kotlin-default-argument-index", i);
			}
		}

		static void ApplyTypeParameters (XElement target, JsonElement container, Dictionary<int, JsonElement> scope)
		{
			if (!container.TryGetProperty ("typeParameters", out var parameters))
				return;
			target.SetAttributeValue ("kotlin-type-parameters", parameters.GetRawText ());
		}

		Dictionary<int, JsonElement> ClassScope (string name, HashSet<string> visiting = null)
		{
			visiting ??= new HashSet<string> (StringComparer.Ordinal);
			if (!visiting.Add (name))
				throw new InvalidDataException ($"Cyclic JVM enclosing classes in descriptor: '{name}'.");
			var result = new Dictionary<int, JsonElement> ();
			var jvm = classes [name].GetProperty ("jvm");
			var outer = jvm.TryGetProperty ("innerClasses", out var innerClasses)
				? innerClasses.EnumerateArray ().Where (c => Text (c, "binaryName") == name)
					.Select (c => Text (c, "outerName")).FirstOrDefault (n => !string.IsNullOrEmpty (n))
				: null;
			outer ??= Text (jvm, "outerClass");
			var nested = name.LastIndexOf ('$');
			if (outer == null && nested > 0)
				outer = name.Substring (0, nested);
			if (outer != null && classes.ContainsKey (outer))
				result = ClassScope (outer, visiting);
			if (classes [name].TryGetProperty ("kotlin", out var kotlin) && kotlin.ValueKind != JsonValueKind.Null &&
				kotlin.TryGetProperty ("class", out var klass))
				result = ExtendScope (result, klass);
			visiting.Remove (name);
			return result;
		}

		static Dictionary<int, JsonElement> ExtendScope (Dictionary<int, JsonElement> scope, JsonElement container)
		{
			var result = new Dictionary<int, JsonElement> (scope);
			if (container.TryGetProperty ("typeParameters", out var parameters))
				foreach (var parameter in parameters.EnumerateArray ())
					result [parameter.GetProperty ("id").GetInt32 ()] = parameter;
			return result;
		}

		static KotlinTypeContract TypeContract (JsonElement type, Dictionary<int, JsonElement> scope, HashSet<int> visiting = null, int depth = 0)
		{
			if (depth > 128)
				throw new InvalidDataException ("Kotlin descriptor type exceeds the nesting limit.");
			visiting ??= new HashSet<int> ();
			var classifier = type.GetProperty ("classifier");
			var result = new KotlinTypeContract {
				Nullable = type.GetProperty ("nullable").GetBoolean (),
				IsSuspend = type.GetProperty ("suspend").GetBoolean (),
				DefinitelyNonNull = type.GetProperty ("definitelyNonNull").GetBoolean (),
			};
			if (Text (classifier, "kind") == "parameter") {
				var id = classifier.GetProperty ("id").GetInt32 ();
				if (!scope.TryGetValue (id, out var parameter))
					throw new InvalidDataException ($"Kotlin descriptor references out-of-scope type parameter {id}.");
				result.TypeParameter = RequiredText (parameter, "name");
				result.TypeParameterId = id;
				if (visiting.Add (id)) {
					result.UpperBounds = parameter.GetProperty ("upperBounds").EnumerateArray ()
						.Select (b => TypeContract (b, scope, visiting, depth + 1)).ToList ();
					visiting.Remove (id);
				}
			} else
				result.Classifier = RequiredText (classifier, "name");
			foreach (var argument in type.GetProperty ("arguments").EnumerateArray ()) {
				var variance = RequiredText (argument, "variance");
				var value = argument.GetProperty ("type");
				result.Arguments.Add (new KotlinTypeArgumentContract {
					Projection = variance switch { "IN" => "In", "OUT" => "Out", "INVARIANT" => "Inv", "STAR" => "Star",
						_ => throw new InvalidDataException ($"Unknown Kotlin descriptor variance '{variance}'.") },
					Type = value.ValueKind == JsonValueKind.Null ? null : TypeContract (value, scope, visiting, depth + 1),
				});
			}
			if (type.TryGetProperty ("outerType", out var outer) && outer.ValueKind != JsonValueKind.Null)
				result.OuterType = TypeContract (outer, scope, visiting, depth + 1);
			if (type.TryGetProperty ("flexibleUpperBound", out var flexible) && flexible.ValueKind != JsonValueKind.Null)
				result.FlexibleUpperBound = TypeContract (flexible.GetProperty ("type"), scope, visiting, depth + 1);
			return result;
		}

		static void ApplyVisibility (XElement member, string source, string physical)
		{
			if (physical != null)
				member.SetAttributeValue ("jvm-visibility", physical == "package" ? "" : physical);
			member.SetAttributeValue ("kotlin-source-visibility", source);
			member.SetAttributeValue ("visibility", source switch {
				"public" => "public", "protected" => "protected", "internal" => member.Name == "interface" ? "" :
					member.Name == "class" ? "private" : "kotlin-internal", "private" or "private_to_this" or "local" => "private",
				_ => throw new InvalidDataException ($"Unknown Kotlin descriptor visibility '{source}'."),
			});
		}

		static void ApplyAnnotations (XElement target, JsonElement annotations)
		{
			foreach (var annotation in annotations.EnumerateArray ()) {
				var name = RequiredText (annotation, "descriptor");
				var values = annotation.GetProperty ("values");
				switch (name) {
				case "Landroidx/annotation/RestrictTo;":
					if (!values.TryGetProperty ("value", out var scopes) || scopes.ValueKind != JsonValueKind.Array)
						throw new InvalidDataException ("Invalid RestrictTo annotation in descriptor.");
					target.SetAttributeValue ("annotated-visibility", string.Join (" ", scopes.EnumerateArray ().Select (s =>
						s.ValueKind == JsonValueKind.Array && s.GetArrayLength () == 2 ? s [1].GetString () :
							throw new InvalidDataException ("Invalid RestrictTo enum value."))));
					break;
				case "Lorg/jetbrains/annotations/ApiStatus$Internal;":
					target.SetAttributeValue ("api-internal", true);
					break;
				case "Landroidx/annotation/VisibleForTesting;":
					var intended = values.TryGetProperty ("otherwise", out var access) ? access.GetInt32 () : 2;
					target.SetAttributeValue ("api-visible-for-testing", intended switch {
						2 => "private", 3 => "package", 4 => "protected", 5 => "none", _ => "unknown:" + intended,
					});
					break;
				}
			}
		}

		static string MemberKey (JsonElement container, string property, bool field = false)
		{
			if (!container.TryGetProperty (property, out var signature) || signature.ValueKind == JsonValueKind.Null)
				return null;
			return RequiredText (signature, "name") + (field ? ":" : "") + RequiredText (signature, "descriptor");
		}

		static string Text (JsonElement value, string name) =>
			value.TryGetProperty (name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString () : null;
		static string RequiredText (JsonElement value, string name) => Text (value, name) ??
			throw new InvalidDataException ($"JVM descriptor is missing string '{name}'.");
		public void Dispose () => document.Dispose ();
	}
}
