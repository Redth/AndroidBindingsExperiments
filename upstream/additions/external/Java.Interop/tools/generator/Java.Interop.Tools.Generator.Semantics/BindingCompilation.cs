using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Java.Interop.Tools.JavaTypeSystem.Models;
using MonoDroid.Generation;

namespace Java.Interop.Tools.Generator.Semantics
{
	// Keep the binary facts separate from the resolver's mutable, pruned graph.
	public sealed class BindingCompilation
	{
		readonly XDocument source;

		public IReadOnlyList<BindingTypeDefinition> Types { get; }

		public BindingCompilation (XDocument api)
		{
			if (api == null)
				throw new ArgumentNullException (nameof (api));
			if (api.Root?.Name != "api")
				throw new InvalidDataException ("A binding compilation requires an <api> document.");

			source = new XDocument (api);
			Types = Array.AsReadOnly (source.Root.Elements ("package")
				.SelectMany (p => p.Elements ().Where (t => t.Name == "class" || t.Name == "interface")
					.Select (t => new BindingTypeDefinition (p, t)))
				.ToArray ());
			if (Types.GroupBy (t => t.JavaName, StringComparer.Ordinal).Any (g => g.Count () > 1))
				throw new InvalidDataException ("Duplicate JVM type identity.");
		}

		public XDocument ExportFacts () => new XDocument (source);

		public BindingProjectionPlan Plan (JavaTypeCollection resolved)
		{
			if (resolved == null)
				throw new ArgumentNullException (nameof (resolved));

			var overrides = new List<BindingOverrideProjection> ();
			var renames = new List<BindingFieldRename> ();
			var opaque_fields = new List<BindingOpaqueFieldProjection> ();
			var restored_methods = new List<BindingRestoredMethod> ();
			var method_renames = new List<BindingMethodRename> ();
			var definitions = Types.ToDictionary (t => t.JavaName, StringComparer.Ordinal);
			var referenced_contracts = new HashSet<(string Type, string Slot)> ();
			var protected_contracts = new HashSet<(string Type, string Slot)> ();
			foreach (var pair in resolved.TypesFlattened.Where (p => definitions.ContainsKey (p.Key))) {
				foreach (var method in pair.Value.Methods.Where (m => !m.IsStatic &&
					m.BaseMethod?.Visibility == "public" && !definitions.ContainsKey (m.BaseMethod.DeclaringType.FullName)))
					referenced_contracts.Add ((pair.Key, method.Name + method.JniSignature));
				foreach (var method in pair.Value.Methods.Where (m => !m.IsStatic &&
					m.BaseMethod?.Visibility == "protected" && !definitions.ContainsKey (m.BaseMethod.DeclaringType.FullName)))
					protected_contracts.Add ((pair.Key, method.Name + method.JniSignature));
				var pending = new Stack<JavaTypeModel> (pair.Value.ImplementsModels
					.Where (i => i.ReferencedType != null).Select (i => i.ReferencedType));
				var visited = new HashSet<JavaTypeModel> ();
				while (pending.Count > 0) {
					var contract = pending.Pop ();
					if (!visited.Add (contract))
						continue;
					if (!definitions.ContainsKey (contract.FullName) && contract.Visibility == "public")
						foreach (var method in pair.Value.Methods.Where (m => !m.IsStatic &&
							contract.Methods.Any (c => c.Visibility == "public" && !c.IsStatic && c.Name == m.Name &&
								c.JniSignature.Substring (0, c.JniSignature.IndexOf (')') + 1) ==
								m.JniSignature.Substring (0, m.JniSignature.IndexOf (')') + 1))))
							referenced_contracts.Add ((pair.Key, method.Name + method.JniSignature));
					foreach (var parent in contract.ImplementsModels)
						if (parent.ReferencedType != null)
							pending.Push (parent.ReferencedType);
				}
			}
			var accessibility = new BindingAccessibilityProjection (ExportFacts (), referenced_contracts, protected_contracts);
			var inaccessible = new HashSet<string> (accessibility.Decisions.Where (d => d.NativeSlot == "" &&
				d.ManagedVisibility != "public" && d.ManagedVisibility != "protected").Select (d => d.DeclaringType), StringComparer.Ordinal);
			var nested_types = Types.ToLookup (t => t.JavaName.Substring (0, t.JavaName.LastIndexOf ('.') + 1), StringComparer.Ordinal);

			foreach (var type in Types) {
				if (!resolved.TypesFlattened.TryGetValue (type.JavaName, out var model))
					continue;

				foreach (var method in model.Methods.Where (m => !m.IsStatic)) {
					if (method.IsSynthetic && (method.Visibility == "public" || method.Visibility == "protected") &&
						!model.Methods.Any (m => !m.IsSynthetic && m.Name == method.Name && m.Parameters.Count == method.Parameters.Count))
						restored_methods.Add (new BindingRestoredMethod (type.JavaName, method.Name, method.JniSignature));
					if (method.Name == "finalize" && method.JniSignature == "()V")
						method_renames.Add (new BindingMethodRename (type.JavaName, method.Name, method.JniSignature, "JavaFinalize"));
				}

				foreach (var method in model.Methods.Where (m => !m.IsStatic && !m.IsSynthetic && !m.IsBridge)) {
					var basis = method.BaseMethod;
					if (basis == null || basis.IsStatic || basis.Parameters.Count != method.Parameters.Count)
						continue;

					// Array rank is part of the slot, not just its generic element type.
					if (basis.Parameters.Zip (method.Parameters, (b, d) =>
						b.TypeModel?.ReferencedTypeParameter == null && b.TypeModel?.ArrayPart != d.TypeModel?.ArrayPart).Any (different => different))
						continue;

					var bridge = model.Methods.SingleOrDefault (m => m.IsBridge && m.IsSynthetic &&
						m.Name == method.Name && m.JniSignature == basis.JniSignature);
					if (bridge == null)
						continue;
					var base_parameters = basis.JniSignature.Substring (0, basis.JniSignature.IndexOf (')') + 1);
					if (!method.JniSignature.StartsWith (base_parameters, StringComparison.Ordinal) &&
						model.Methods.Any (m => m != method && !m.IsStatic && !m.IsSynthetic && m.Name == method.Name &&
							m.JniSignature.StartsWith (base_parameters, StringComparison.Ordinal)))
						continue;

					var parameters = method.Parameters.Select ((p, i) =>
						basis.Parameters [i].TypeModel?.ReferencedTypeParameter != null
							? bridge.Parameters [i].Type : p.Type).ToArray ();
					var return_type = basis.ReturnTypeModel?.ReferencedTypeParameter != null ? bridge.Return : method.Return;
					if (definitions.TryGetValue (return_type, out var return_definition) &&
						return_definition.Attributes.TryGetValue ("visibility", out var return_visibility) &&
						return_visibility != "public" && return_visibility != "protected" &&
						definitions.TryGetValue (method.Return, out var typed_return) &&
						typed_return.Attributes.TryGetValue ("visibility", out var typed_visibility) &&
						(typed_visibility == "public" || typed_visibility == "protected"))
						return_type = method.Return;

					if (return_type == method.Return && parameters.SequenceEqual (method.Parameters.Select (p => p.Type)))
						continue;

					if (!JniSignatureUtilities.TryParseMethodSignature (method.JniSignature, out var native_parameters, out var native_return) ||
						!JniSignatureUtilities.TryParseMethodSignature (bridge.JniSignature, out var bridge_parameters, out var bridge_return) ||
						native_parameters.Length != bridge_parameters.Length)
						throw new InvalidDataException ($"Invalid bridge descriptors for {type.JavaName}.{method.Name}{method.JniSignature}.");
					if (!JniSignatureUtilities.AreAbiCompatible (native_return, bridge_return) ||
						native_parameters.Where ((p, i) => !JniSignatureUtilities.AreAbiCompatible (p, bridge_parameters [i])).Any ()) {
						var original = type.Methods.Single (m => m.Name == method.Name && m.Descriptor == method.JniSignature);
						var binary_bridge = type.Methods.Single (m => m.Name == bridge.Name && m.Descriptor == bridge.JniSignature);
						if (!BindingProjectionPlan.BridgeInvokes (binary_bridge, type.JavaName, original))
							throw new InvalidDataException ($"The physical bridge does not target {type.JavaName}.{method.Name}{method.JniSignature}.");
						restored_methods.Add (new BindingRestoredMethod (type.JavaName, bridge.Name, bridge.JniSignature));
						method_renames.Add (new BindingMethodRename (type.JavaName, method.Name, method.JniSignature,
							method.Name + "Native" + Convert.ToHexString (SHA256.HashData (Encoding.UTF8.GetBytes (method.JniSignature))).Substring (0, 8)));
						continue;
					}

					overrides.Add (new BindingOverrideProjection (
						type.JavaName, method.Name, method.JniSignature,
						basis.DeclaringType.FullName, bridge.JniSignature,
						return_type, Array.AsReadOnly (parameters),
						Array.AsReadOnly (basis.Parameters.Select (p => p.IsNotNull).ToArray ())));
				}

				foreach (var bridge in model.Methods.Where (m => !m.IsStatic && m.IsSynthetic &&
					(m.Visibility == "public" || m.Visibility == "protected"))) {
					if ((RequiresAbstractBaseBridge (model, bridge) || RequiresInterfaceContract (model, bridge)) &&
						!overrides.Any (p => p.DeclaringType == type.JavaName && p.Name == bridge.Name &&
							p.BridgeDescriptor == bridge.JniSignature))
						restored_methods.Add (new BindingRestoredMethod (type.JavaName, bridge.Name, bridge.JniSignature));
				}

				var names = new HashSet<string> (type.Methods.Select (m => m.Name)
					.Concat (type.Fields.Select (f => f ["name"])), StringComparer.Ordinal);
				var nested = nested_types [type.JavaName + "."].ToArray ();
				names.UnionWith (nested.Select (t => t.JavaName.Substring (type.JavaName.Length + 1)));

				foreach (var field in type.Fields) {
					var name = field ["name"];
					if (field.TryGetValue ("type", out var field_type) &&
						definitions.TryGetValue (field_type, out var field_definition) &&
						field_definition.Attributes.TryGetValue ("visibility", out var field_visibility) &&
						(field_visibility != "public" && field_visibility != "protected" || inaccessible.Contains (field_type)))
						opaque_fields.Add (new BindingOpaqueFieldProjection (
							type.JavaName, name, field ["jni-signature"], "java.lang.Object"));
					var target = nested.SingleOrDefault (t => t.JavaName == type.JavaName + "." + name);
					if (target == null || !target.Attributes.TryGetValue ("visibility", out var visibility) || visibility != "public")
						continue;

					var managed_name = name + "Instance";
					for (var suffix = 2; !names.Add (managed_name); suffix++)
						managed_name = name + "Instance" + suffix;
					renames.Add (new BindingFieldRename (type.JavaName, name, managed_name));
				}
			}

			return new BindingProjectionPlan (this, overrides.AsReadOnly (), renames.AsReadOnly (), opaque_fields.AsReadOnly (),
				restored_methods.Distinct ().ToList ().AsReadOnly (), method_renames.AsReadOnly (), accessibility);
		}

		static bool RequiresAbstractBaseBridge (JavaTypeModel type, JavaMethodModel bridge)
		{
			if (type is not JavaClassModel klass)
				return false;
			var parameters = bridge.JniSignature.Substring (0, bridge.JniSignature.IndexOf (')') + 1);
			if (type.Methods.Any (m => !m.IsStatic && !m.IsSynthetic && m.Name == bridge.Name &&
				m.JniSignature.StartsWith (parameters, StringComparison.Ordinal)))
				return false;
			for (var basis = klass.BaseTypeReference?.ReferencedType as JavaClassModel; basis != null;
				basis = basis.BaseTypeReference?.ReferencedType as JavaClassModel) {
				var declaration = basis.Methods.FirstOrDefault (m => !m.IsStatic && m.Name == bridge.Name &&
					m.JniSignature == bridge.JniSignature);
				if (declaration != null)
					return declaration.IsAbstract;
			}
			return false;
		}

		static bool RequiresInterfaceContract (JavaTypeModel type, JavaMethodModel method)
		{
			var parameters = method.JniSignature.Substring (0, method.JniSignature.IndexOf (')') + 1);
			if (type.Methods.Any (m => !m.IsStatic && !m.IsSynthetic && m.Name == method.Name &&
				m.JniSignature.StartsWith (parameters, StringComparison.Ordinal)))
				return false;
			var pending = new Stack<JavaTypeModel> ();
			var visited = new HashSet<JavaTypeModel> ();
			pending.Push (type);
			while (pending.Count > 0) {
				var current = pending.Pop ();
				if (!visited.Add (current))
					continue;
				if (current is JavaInterfaceModel && current.Methods.Any (m => !m.IsStatic &&
					m.Name == method.Name && m.JniSignature == method.JniSignature))
					return true;
				foreach (var implemented in current.ImplementsModels)
					if (implemented.ReferencedType != null)
						pending.Push (implemented.ReferencedType);
				if (current is JavaClassModel klass && klass.BaseTypeReference?.ReferencedType != null)
					pending.Push (klass.BaseTypeReference.ReferencedType);
			}
			return false;
		}
	}

	public sealed class BindingTypeDefinition
	{
		public string JavaName { get; }
		public IReadOnlyDictionary<string, string> Attributes { get; }
		public IReadOnlyList<BindingMethodDefinition> Methods { get; }
		public IReadOnlyList<IReadOnlyDictionary<string, string>> Fields { get; }
		public IReadOnlyList<IReadOnlyDictionary<string, string>> Constructors { get; }

		internal BindingTypeDefinition (XElement package, XElement type)
		{
			JavaName = GetJavaName (package, type);
			Attributes = BindingMethodDefinition.ReadAttributes (type);
			Methods = Array.AsReadOnly (type.Elements ("method").Select (m => new BindingMethodDefinition (m)).ToArray ());
			Fields = Array.AsReadOnly (type.Elements ("field").Select (BindingMethodDefinition.ReadAttributes).ToArray ());
			Constructors = Array.AsReadOnly (type.Elements ("constructor").Select (BindingMethodDefinition.ReadAttributes).ToArray ());

			if (Methods.GroupBy (m => (m.Name, m.Descriptor)).Any (g => g.Count () > 1))
				throw new InvalidDataException ($"Duplicate JVM method identity in {JavaName}.");
		}

		internal static string GetJavaName (XElement package, XElement type)
		{
			var package_name = BindingMethodDefinition.Required (package, "name");
			var type_name = BindingMethodDefinition.Required (type, "name");
			return package_name.Length == 0 ? type_name : package_name + "." + type_name;
		}
	}

	public sealed class BindingMethodDefinition
	{
		public string Name { get; }
		public string Descriptor { get; }
		public IReadOnlyDictionary<string, string> Attributes { get; }
		public IReadOnlyList<IReadOnlyDictionary<string, string>> Parameters { get; }

		internal BindingMethodDefinition (XElement method)
		{
			Name = Required (method, "name");
			Descriptor = Required (method, "jni-signature");
			if (!JniSignatureUtilities.TryParseMethodSignature (Descriptor, out var parameters, out _) ||
				parameters.Length != method.Elements ("parameter").Count ())
				throw new InvalidDataException ($"Invalid JVM descriptor for {Name}: {Descriptor}.");
			Attributes = ReadAttributes (method);
			Parameters = Array.AsReadOnly (method.Elements ("parameter").Select (ReadAttributes).ToArray ());
		}

		internal static string Required (XElement element, string name) =>
			element.Attribute (name)?.Value ?? throw new InvalidDataException ($"Missing {name} on <{element.Name}>.");

		internal static IReadOnlyDictionary<string, string> ReadAttributes (XElement element) =>
			new ReadOnlyDictionary<string, string> (element.Attributes ().ToDictionary (a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal));
	}

	public sealed record BindingOverrideProjection (
		string DeclaringType, string Name, string Descriptor,
		string BaseType, string BridgeDescriptor,
		string ManagedReturnType, IReadOnlyList<string> ManagedParameterTypes,
		IReadOnlyList<bool> ManagedParameterNotNull);

	public sealed record BindingFieldRename (string DeclaringType, string Name, string ManagedName);
	public sealed record BindingOpaqueFieldProjection (string DeclaringType, string Name, string Descriptor, string ManagedType);
	public sealed record BindingRestoredMethod (string DeclaringType, string Name, string Descriptor);
	public sealed record BindingMethodRename (string DeclaringType, string Name, string Descriptor, string ManagedName);

	public sealed class BindingProjectionPlan
	{
		public BindingCompilation Compilation { get; }
		public IReadOnlyList<BindingOverrideProjection> Overrides { get; }
		public IReadOnlyList<BindingFieldRename> FieldRenames { get; }
		public IReadOnlyList<BindingOpaqueFieldProjection> OpaqueFields { get; }
		public IReadOnlyList<BindingRestoredMethod> RestoredMethods { get; }
		public IReadOnlyList<BindingMethodRename> MethodRenames { get; }
		public BindingAccessibilityProjection Accessibility { get; }

		internal BindingProjectionPlan (BindingCompilation compilation, IReadOnlyList<BindingOverrideProjection> overrides, IReadOnlyList<BindingFieldRename> fieldRenames, IReadOnlyList<BindingOpaqueFieldProjection> opaqueFields,
			IReadOnlyList<BindingRestoredMethod> restoredMethods, IReadOnlyList<BindingMethodRename> methodRenames, BindingAccessibilityProjection accessibility)
		{
			Compilation = compilation;
			Overrides = overrides;
			FieldRenames = fieldRenames;
			OpaqueFields = opaqueFields;
			RestoredMethods = restoredMethods;
			MethodRenames = methodRenames;
			Accessibility = accessibility;
		}

		// This is a lowering adapter for the existing peer emitter, not a new emitter.
		public XDocument Lower (XDocument adjusted)
		{
			if (adjusted == null)
				throw new ArgumentNullException (nameof (adjusted));
			if (adjusted.Root?.Name != "api")
				throw new InvalidDataException ("Lowering requires an <api> document.");

			var result = new XDocument (adjusted);
			var types = Compilation.Types.ToDictionary (t => t.JavaName, StringComparer.Ordinal);
			var projections = Overrides.ToDictionary (p => (p.DeclaringType, p.Name, p.Descriptor));
			var renames = FieldRenames.ToDictionary (r => (r.DeclaringType, r.Name));
			var opaque_fields = OpaqueFields.ToDictionary (f => (f.DeclaringType, f.Name));
			var restored_methods = RestoredMethods.ToLookup (m => m.DeclaringType, StringComparer.Ordinal);
			var method_renames = MethodRenames.ToDictionary (m => (m.DeclaringType, m.Name, m.Descriptor));
			var raw_types = Compilation.ExportFacts ().Root.Elements ("package")
				.SelectMany (p => p.Elements ().Where (t => t.Name == "class" || t.Name == "interface")
					.Select (t => (Name: BindingTypeDefinition.GetJavaName (p, t), Type: t)))
				.ToDictionary (t => t.Name, t => t.Type, StringComparer.Ordinal);

			foreach (var package in result.Root.Elements ("package")) {
				foreach (var type in package.Elements ().Where (t => t.Name == "class" || t.Name == "interface")) {
					var full_name = BindingTypeDefinition.GetJavaName (package, type);
					if (!types.TryGetValue (full_name, out var facts))
						throw new InvalidDataException ($"No binary facts for {full_name}.");

					CopyKotlinFacts (facts.Attributes, type);
					var constructors = raw_types [full_name].Elements ("constructor")
						.ToDictionary (c => BindingMethodDefinition.Required (c, "jni-signature"), StringComparer.Ordinal);
					foreach (var constructor in type.Elements ("constructor")) {
						var descriptor = BindingMethodDefinition.Required (constructor, "jni-signature");
						if (!constructors.TryGetValue (descriptor, out var original))
							throw new InvalidDataException ($"No binary constructor facts for {full_name}{descriptor}.");
						CopyKotlinFacts (BindingMethodDefinition.ReadAttributes (original), constructor);
						var parameters = constructor.Elements ("parameter").ToArray ();
						var originals = original.Elements ("parameter").ToArray ();
						if (parameters.Length != originals.Length)
							throw new InvalidDataException ($"Constructor parameter count changed for {full_name}{descriptor}.");
						for (var i = 0; i < parameters.Length; i++)
							CopyKotlinFacts (BindingMethodDefinition.ReadAttributes (originals [i]), parameters [i]);
					}
					foreach (var restored in restored_methods [full_name]) {
						if (!type.Elements ("method").Any (m => m.Attribute ("name")?.Value == restored.Name && m.Attribute ("jni-signature")?.Value == restored.Descriptor)) {
							var original = raw_types [full_name].Elements ("method").Single (m =>
								m.Attribute ("name")?.Value == restored.Name && m.Attribute ("jni-signature")?.Value == restored.Descriptor);
							type.Add (new XElement (original));
						}
					}
					foreach (var field in type.Elements ("field")) {
						var key = (full_name, BindingMethodDefinition.Required (field, "name"));
						if (renames.TryGetValue (key, out var rename))
							field.SetAttributeValue ("managedName", rename.ManagedName);
						if (opaque_fields.TryGetValue (key, out var opaque)) {
							field.SetAttributeValue ("type", opaque.ManagedType);
							field.SetAttributeValue ("managed-jni-signature", opaque.Descriptor);
						}
					}
					var methods = facts.Methods.ToDictionary (m => (m.Name, m.Descriptor));

					foreach (var method in type.Elements ("method")) {
						var name = BindingMethodDefinition.Required (method, "name");
						var descriptor = BindingMethodDefinition.Required (method, "jni-signature");
						if (!methods.TryGetValue ((name, descriptor), out var fact))
							throw new InvalidDataException ($"No binary facts for {full_name}.{name}{descriptor}.");

						CopyKotlinFacts (fact.Attributes, method);
						method.SetAttributeValue ("managed-jni-signature", descriptor);
						var bridges = facts.Methods.Where (m => m.Name == name && m.Parameters.Count == fact.Parameters.Count &&
							m.Attributes.TryGetValue ("bridge", out var bridge) && bridge == "true" &&
							m.Attributes.TryGetValue ("synthetic", out var synthetic) && synthetic == "true" &&
							(m.Descriptor == descriptor || BridgeInvokes (m, full_name, fact)));
						if (bridges.Any ())
							method.SetAttributeValue ("managed-bridge-signatures", string.Join ("|", bridges.Select (m => m.Descriptor)));
						if (method_renames.TryGetValue ((full_name, name, descriptor), out var method_rename))
							method.SetAttributeValue ("managedName", method_rename.ManagedName);
						var parameters = method.Elements ("parameter").ToArray ();
						if (parameters.Length != fact.Parameters.Count)
							throw new InvalidDataException ($"Parameter count changed for {full_name}.{name}{descriptor}.");
						for (var i = 0; i < parameters.Length; i++)
							CopyKotlinFacts (fact.Parameters [i], parameters [i]);

						if (!projections.TryGetValue ((full_name, name, descriptor), out var projection))
							continue;

						method.SetAttributeValue ("return", projection.ManagedReturnType);
						method.SetAttributeValue ("managed-base-jni-signature", projection.BridgeDescriptor);
						JniSignatureUtilities.TryParseMethodSignature (descriptor, out var native_parameters, out _);
						for (var i = 0; i < parameters.Length; i++) {
							parameters [i].SetAttributeValue ("type", projection.ManagedParameterTypes [i]);
							parameters [i].SetAttributeValue ("managed-jni-type", native_parameters [i]);
							parameters [i].SetAttributeValue ("not-null", projection.ManagedParameterNotNull [i] ? "true" : "false");
						}
					}
				}
			}

			Accessibility.Apply (result);
			// Keep nominal symbols through validation; exclude their managed peers only after contract projection.
			foreach (var type in result.Root.Elements ("package").SelectMany (p => p.Elements ()
				.Where (t => t.Name == "class" || t.Name == "interface"))
				.Where (t => (string) t.Attribute ("api-classification") == "Excluded"))
				type.SetAttributeValue ("visibility", "internal");
			return result;
		}

		internal static bool BridgeInvokes (BindingMethodDefinition bridge, string owner, BindingMethodDefinition method)
		{
			if (!bridge.Attributes.TryGetValue ("jvm-bridge-targets", out var targets))
				return true;
			using var document = JsonDocument.Parse (targets);
			return document.RootElement.EnumerateArray ().Any (target =>
				target.GetProperty ("name").GetString () == method.Name &&
				target.GetProperty ("descriptor").GetString () == method.Descriptor &&
				target.GetProperty ("owner").GetString ()?.Replace ('/', '.').Replace ('$', '.') == owner);
		}

		static void CopyKotlinFacts (IReadOnlyDictionary<string, string> source, XElement target)
		{
			foreach (var pair in source)
				if (pair.Key.StartsWith ("kotlin-", StringComparison.Ordinal) || pair.Key.StartsWith ("api-", StringComparison.Ordinal) ||
					pair.Key.StartsWith ("jvm-", StringComparison.Ordinal) || pair.Key == "source-file-name" || pair.Key == "annotated-visibility")
					target.SetAttributeValue (pair.Key, pair.Value);
		}

		public void WriteReport (string path)
		{
			var factsName = Path.GetFileName (path) + ".native.xml";
			Compilation.ExportFacts ().Save (Path.Combine (Path.GetDirectoryName (Path.GetFullPath (path)), factsName));
			using (var stream = File.Create (path))
				JsonSerializer.Serialize (stream, new {
					SchemaVersion = 2,
					Backend = "legacy-peer-emitter",
					BinaryFactsFile = factsName,
					Types = Compilation.Types,
					Overrides,
					FieldRenames,
					OpaqueFields,
					RestoredMethods,
					MethodRenames,
					Accessibility = Accessibility.Decisions,
				}, new JsonSerializerOptions { WriteIndented = true });
		}
	}
}
