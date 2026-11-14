using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MonoDroid.Generation;

namespace Java.Interop.Tools.Generator.Semantics
{
	public sealed record ManagedProjectionDecision (string DeclaringType, string NativeSlot, string Reason, string ManagedName, string ManagedReturn);

	public static class ManagedContractProjection
	{
		public static IReadOnlyList<ManagedProjectionDecision> PrepareKotlinValueClasses (IEnumerable<GenBase> definitions)
		{
			var types = Flatten (definitions).ToArray ();
			var names = new HashSet<string> (types.Select (t => t.FullName).Concat (types.SelectMany (t => NamespacePrefixes (t.Namespace))), StringComparer.Ordinal);
			var decisions = new List<ManagedProjectionDecision> ();
			foreach (var type in types.OfType<ClassGen> ().Where (t => t.IsKotlinInlineClass)) {
				var value_name = type.FullName;
				var prefix = value_name.Substring (0, value_name.Length - type.Name.Length);
				type.KotlinInlineClassValueName = value_name;
				type.FullName = Allocate (value_name + "Boxed", names);
				type.Name = type.FullName.Substring (prefix.Length);
				foreach (var nested in Flatten (type.NestedTypes))
					nested.FullName = type.FullName + nested.FullName.Substring (value_name.Length);
				decisions.Add (new ManagedProjectionDecision (type.JavaName, "", "separate-boxed-and-unboxed-value-class", type.FullName, value_name));
			}
			return decisions.AsReadOnly ();
		}

		public static IReadOnlyList<ManagedProjectionDecision> AllocateTypeNames (IEnumerable<GenBase> definitions)
		{
			var types = Flatten (definitions).ToArray ();
			var decisions = new List<ManagedProjectionDecision> ();
			var namespaces = new HashSet<string> (types.SelectMany (t => NamespacePrefixes (t.Namespace)), StringComparer.Ordinal);
			var value_types = types.OfType<ClassGen> ().Where (t => t.KotlinInlineClassValueName != null).ToArray ();
			var type_names = new HashSet<string> (types.Select (t => t.FullName).Concat (namespaces)
				.Concat (value_types.Select (t => t.KotlinInlineClassValueName)), StringComparer.Ordinal);
			var owners = types.SelectMany (owner => owner.NestedTypes.Select (nested => (Nested: nested, Owner: owner)))
				.ToDictionary (pair => pair.Nested, pair => pair.Owner);
			foreach (var type in value_types) {
				var old_name = type.KotlinInlineClassValueName;
				var name = old_name.Substring (old_name.LastIndexOf ('.') + 1);
				var current_owner = type.FullName.Substring (0, type.FullName.LastIndexOf ('.') + 1);
				if (old_name != current_owner + name) {
					type.KotlinInlineClassValueName = old_name = current_owner + name;
					type_names.Add (old_name);
					decisions.Add (new ManagedProjectionDecision (type.JavaName, "", "rebase-value-class-to-projected-owner", old_name, ""));
				}
				var owner_collision = owners.TryGetValue (type, out var owner) &&
					owner.GetAllMethods ().Concat (ImplementedInterfaces (owner).SelectMany (i => i.GetAllMethods ()))
						.Any (m => m.Name == name || (m.CanGet && m.PropertyName == name));
				if (!namespaces.Contains (old_name) && !owner_collision)
					continue;
				type.KotlinInlineClassValueName = Allocate (old_name + "Type", type_names);
				decisions.Add (new ManagedProjectionDecision (type.JavaName, "", "value-class-name-collision", type.KotlinInlineClassValueName, ""));
			}
			var claimed_names = new HashSet<string> (StringComparer.Ordinal);
			foreach (var type in types.OrderBy (t => t.RawVisibility == "public" ? 0 : 1).ThenBy (t => t.JavaName, StringComparer.Ordinal)) {
				var inherited_names = ImplementedInterfaces (type).SelectMany (i => i.GetAllMethods ())
					.Where (m => m.CanGet).Select (m => m.PropertyName);
				var class_property_collision = false;
				for (var basis = type.BaseGen; basis != null; basis = basis.BaseGen)
					if (basis.GetAllMethods ().Any (m => !m.IsStatic && m.CanGet && m.PropertyName == type.Name &&
						type.GetAllMethods ().Any (candidate => !candidate.IsStatic && candidate.JavaName == m.JavaName &&
							candidate.Parameters.Count == m.Parameters.Count))) {
						class_property_collision = true;
						break;
					}
				var owner_collision = owners.TryGetValue (type, out var owner) &&
					(owner.Name == type.Name ||
					 owner.Fields.Any (f => f.Name == type.Name) ||
					 owner.GetAllMethods ().Any (m => m.Name == type.Name || m.CanGet && m.PropertyName == type.Name) ||
					 ImplementedInterfaces (owner).SelectMany (i => i.GetAllMethods ()).Any (m => m.Name == type.Name || m.CanGet && m.PropertyName == type.Name));
				var namespace_collision = namespaces.Contains (type.FullName);
				var managed_collision = !claimed_names.Add (type.FullName);
				if (!namespace_collision && !owner_collision && !class_property_collision && !managed_collision && !inherited_names.Contains (type.Name))
					continue;
				var old_name = type.FullName;
				var prefix = old_name.Substring (0, old_name.Length - type.Name.Length);
				var proposed = old_name + "Type";
				while (owner != null && proposed.Substring (prefix.Length) == owner.Name)
					proposed += "Type";
				type.FullName = Allocate (proposed, type_names);
				type.Name = type.FullName.Substring (prefix.Length);
				claimed_names.Add (type.FullName);
				foreach (var nested in Flatten (type.NestedTypes)) {
					nested.FullName = type.FullName + nested.FullName.Substring (old_name.Length);
					if (nested is ClassGen inline && inline.KotlinInlineClassValueName != null)
						inline.KotlinInlineClassValueName = type.FullName + inline.KotlinInlineClassValueName.Substring (old_name.Length);
					type_names.Add (nested.FullName);
				}
				decisions.Add (new ManagedProjectionDecision (type.JavaName, "",
					namespace_collision ? "type-namespace-name-collision" : managed_collision ? "type-managed-name-collision" :
					owner_collision ? "nested-type-interface-property-name-collision" :
					class_property_collision ? "type-base-property-name-collision" : "type-interface-property-name-collision", type.FullName, ""));
			}
			return decisions.AsReadOnly ();
		}

		public static IReadOnlyList<ManagedProjectionDecision> Apply (IEnumerable<GenBase> definitions, CodeGenerationOptions options)
		{
			var types = Flatten (definitions).ToArray ();
			var decisions = new List<ManagedProjectionDecision> ();
			if (options.ExperimentalSemanticModel) {
				ProjectInaccessibleSignatureTypes (types, options, decisions);
				var owners = types.SelectMany (owner => owner.NestedTypes.Select (nested => (Nested: nested, Owner: owner)))
					.ToDictionary (pair => pair.Nested, pair => pair.Owner);
				foreach (var iface in types.OfType<InterfaceGen> ()) {
					for (GenBase owner = iface; owner != null; owner = owners.TryGetValue (owner, out var parent) ? parent : null) {
						if (owner.RawVisibility != "public") {
							iface.SuppressListenerProjection = true;
							break;
						}

					}
				}
				foreach (var type in types) {
					var pending = new Queue<ISymbol> (type.Interfaces);
					var visited = new HashSet<ISymbol> ();
					while (pending.Count > 0) {
						var symbol = pending.Dequeue ();
						if (!visited.Add (symbol))
							continue;
						var iface = (symbol is GenericSymbol generic ? generic.Gen : symbol) as InterfaceGen;
						if (iface == null)
							continue;
						if (!iface.HasProjectedManagedContract) {
							foreach (var ancestor in iface.Interfaces)
								pending.Enqueue (ancestor);
						} else if (!type.Interfaces.Any (existing => existing.FullName == symbol.FullName)) {
							type.Interfaces.Add (symbol);
							decisions.Add (new ManagedProjectionDecision (type.JavaName, iface.JniName,
								"retain-public-contract-through-inaccessible-interface", symbol.FullName, ""));
						}
					}
				}
				foreach (var method in types.OfType<InterfaceGen> ().SelectMany (t => t.GetAllMethods ())) {
					if (method.OverriddenInterfaceMethod?.DeclaringType is InterfaceGen contract && !contract.HasProjectedManagedContract) {
						method.OverriddenInterfaceMethod = null;
						decisions.Add (Decision (method, "retain-default-method-without-inaccessible-clr-contract"));
					}
				}
			}
			var families = new Dictionary<Method, List<Method>> ();
			var implementors = types.Select (t => (Type: t, Interfaces: ImplementedInterfaces (t).ToArray ())).ToArray ();
			var interface_implementors = implementors.SelectMany (i => i.Interfaces.Select (contract => (Contract: contract, i.Type)))
				.ToLookup (i => i.Contract, i => i.Type);
			foreach (var type in types.OfType<ClassGen> ())
				ProjectInheritedPropertySetters (type, options, decisions);
			foreach (var type in types.OfType<ClassGen> ().Where (t => t.IsAbstract).OrderBy (Depth))
				MaterializeAbstractInterfaceMethods (type, options, decisions);

			foreach (var type in types) {
				foreach (var method in type.GetAllMethods ().Where (m => !m.IsStatic)) {
					var root = FindFamilyRoot (method);
					if (!families.TryGetValue (root, out var family))
						families.Add (root, family = new List<Method> ());
					family.Add (method);
				}
			}

			var family_names = new Dictionary<Method, string> ();
			foreach (var group in families.Keys.Where (m => m.DeclaringType.IsGeneratable)
				.GroupBy (m => (m.DeclaringType, Signature: m.Name + "(" + string.Join (",", m.Parameters.Select (p => p.Type)) + ")"))
				.Where (g => g.Select (m => m.Parameters.JniSignature).Distinct ().Count () > 1)) {
				foreach (var method in group) {
					var hash = Convert.ToHexString (System.Security.Cryptography.SHA256.HashData (
						System.Text.Encoding.UTF8.GetBytes (method.Parameters.JniSignature))).Substring (0, 8);
					family_names [method] = method.Name + "Native" + hash;
				}
			}
			foreach (var pair in families) {
				var name = family_names.TryGetValue (pair.Key, out var allocated_name) ? allocated_name : pair.Key.Name;
				if (pair.Key.DeclaringType.IsGeneratable) {
					var owners = pair.Value.Select (m => m.DeclaringType).Concat (
						pair.Key.DeclaringType is InterfaceGen contract ? interface_implementors [contract] : Enumerable.Empty<GenBase> ()).Distinct ();
					var forbidden = new HashSet<string> (owners.SelectMany (t =>
						new [] { t.Name }.Concat (t.NestedTypes.Select (n => n.Name)).Concat (t.Fields.Select (f => f.Name))), StringComparer.Ordinal);
					while (forbidden.Contains (name))
						name = "Invoke" + name;
				}
				foreach (var method in pair.Value) {
					if (pair.Key.DeclaringType is InterfaceGen && method.RetVal.FullName != pair.Key.RetVal.FullName &&
						method.RetVal.NativeType == "IntPtr" && pair.Key.RetVal.NativeType == "IntPtr" &&
						!method.RetVal.IsArray && !pair.Key.RetVal.IsArray) {
						method.RetVal.SetManagedReferenceType (pair.Key.RetVal.Symbol);
						decisions.Add (Decision (method, "inherit-exact-interface-return"));
					}
					ReconcileManagedParameters (method, pair.Key, decisions);
					if (method.Name != name) {
						method.Name = name;
						decisions.Add (Decision (method, "inherit-managed-slot-name"));
					}
					if (method.IsInterfaceDefaultMethod && method.DeclaringType is InterfaceGen && pair.Key != method &&
						pair.Key.DeclaringType is InterfaceGen && method.RetVal.FullName == pair.Key.RetVal.FullName &&
						method.OverriddenInterfaceMethod != pair.Key) {
						method.OverriddenInterfaceMethod = pair.Key;
						decisions.Add (Decision (method, "retain-default-interface-override"));
					}
				}
			}

			foreach (var type in types) {
				foreach (var property in type.Properties.Where (p => p.Setter != null &&
					p.Getter.RetVal.FullName != p.Setter.Parameters [0].Type).ToArray ()) {
					type.Properties.Remove (property);
					type.Methods.Add (property.Getter);
					type.Methods.Add (property.Setter);
					decisions.Add (Decision (property.Getter, "retain-incompatible-property-accessors-as-methods"));
				}
			}
			foreach (var type in types.OrderBy (Depth)) {
				var names = new HashSet<string> (type.Methods.Select (m => m.Name)
					.Concat (type.Properties.Select (p => p.Name))
					.Concat (type.Fields.Select (f => f.Name))
					.Concat (type.NestedTypes.Select (t => t.Name))
					.Append (type.Name), StringComparer.Ordinal);
				var field_names = new HashSet<string> (StringComparer.Ordinal);
				foreach (var field in type.Fields.OrderBy (f => f.JavaName, StringComparer.Ordinal)) {
					if (field.Name == type.Name) {
						field.Name = Allocate (field.Name + "Value", names);
						decisions.Add (new ManagedProjectionDecision (type.JavaName, field.JavaName, "field-type-name-collision", field.Name, field.Symbol.FullName));
					} else if (type.GetAllMethods ().Any (m => m.Name == field.Name || m.AdjustedName == field.Name) ||
						type.Properties.Any (p => p.AdjustedName == field.Name)) {
						field.Name = Allocate (field.Name + "Value", names);
						decisions.Add (new ManagedProjectionDecision (type.JavaName, field.JavaName,
							"field-method-name-collision", field.Name, field.Symbol.FullName));
					} else {
						for (var basis = type.BaseGen; basis != null; basis = basis.BaseGen)
							if (basis.GetAllMethods ().Any (m => m.Name == field.Name)) {
								field.Name = Allocate (field.Name + "Field", names);
								decisions.Add (new ManagedProjectionDecision (type.JavaName, field.JavaName,
									"field-inherited-method-name-collision", field.Name, field.Symbol.FullName));
								break;
							}
					}
					if (!field_names.Add (field.Name)) {
						field.Name = Allocate (field.Name + "Value", names);
						field_names.Add (field.Name);
						decisions.Add (new ManagedProjectionDecision (type.JavaName, field.JavaName,
							"field-field-name-collision", field.Name, field.Symbol.FullName));
					}
				}
				foreach (var property in type.Properties.ToArray ()) {
					var basis = FindBaseMethod (type, property.Getter);
					var base_property = basis?.DeclaringType.Properties.FirstOrDefault (p => p.Getter == basis);
					if (base_property != null && base_property.Setter == null && property.Setter != null) {
						var setter = property.Setter;
						type.Methods.Add (setter);
						property.Setter = null;
						names.Add (setter.Name);
						decisions.Add (Decision (setter, "retain-new-setter-outside-read-only-base-property"));
					}
					if (base_property != null && property.Name != base_property.Name) {
						property.Name = base_property.Name;
						decisions.Add (Decision (property.Getter, "inherit-managed-property-name"));
					} else if (basis != null && base_property == null) {
						type.Properties.Remove (property);
						type.Methods.Add (property.Getter);
						property.Getter.Name = basis.Name;
						decisions.Add (Decision (property.Getter, "retain-base-method-instead-of-property"));
						if (property.Setter != null)
							type.Methods.Add (property.Setter);
					} else if (type.NestedTypes.Any (t => t.Name == property.Name) || property.Name == type.Name) {
						property.Name = Allocate (property.Name + "Value", names);
						decisions.Add (Decision (property.Getter, "property-type-name-collision"));
					} else if (basis == null && type.Methods.Any (m => m.AdjustedName == property.AdjustedName || m.Name == property.Name)) {
						property.Name = Allocate (property.Name + "Value", names);
						decisions.Add (Decision (property.Getter, "property-method-name-collision"));
					}
				}

				foreach (var method in type.GetAllMethods ().Where (m => !m.IsStatic)) {
					var basis = FindBaseMethod (type, method);
					if (basis == null) {
						var hidden = FindManagedBaseMethod (type, method);
						if (hidden != null && !hidden.IsAbstract || method.NativeBridgeSignatures.Any () && method.IsOverride) {
							method.IsOverride = false;
							method.ManagedOverride = method.IsVirtual ? "virtual" : "none";
							decisions.Add (Decision (method, "distinct-native-slot-hides-managed-member"));
						}
						continue;
					}
					ReconcileManagedParameters (method, basis, decisions);
					if (method.RetVal.NativeType != basis.RetVal.NativeType ||
						type.Methods.Contains (method) && !basis.DeclaringType.Methods.Contains (basis)) {
						method.IsOverride = false;
						method.ManagedOverride = method.IsVirtual && type is not ClassGen { IsFinal: true } ? "virtual" : "none";
						decisions.Add (Decision (method, "retain-distinct-managed-method-shape"));
						continue;
					}
					if (method.RetVal.FullName == "int" && (basis.IsReturnEnumified || basis.RetVal.Symbol.IsEnum)) {
						method.RetVal.SetGeneratedEnumType (basis.RetVal.FullName);
						method.IsReturnEnumified = true;
						decisions.Add (Decision (method, "inherit-managed-enum-return"));
					} else if (method.RetVal.FullName != basis.RetVal.FullName &&
						method.RetVal.NativeType == basis.RetVal.NativeType && method.RetVal.NativeType != "IntPtr") {
						method.RetVal.SetManagedReferenceType (basis.RetVal.Symbol);
						decisions.Add (Decision (method, "inherit-managed-value-return"));
					} else if (method.RetVal.FullName != basis.RetVal.FullName &&
						method.RetVal.NativeType == "IntPtr" && basis.RetVal.NativeType == "IntPtr" &&
						!basis.RetVal.IsArray && (!method.RetVal.IsArray ||
							basis.DeclaringType is ClassGen && method.NativeBridgeSignatures.Contains (basis.JniSignature)) &&
						!IsCovariant (method.RetVal.Symbol, basis.RetVal.Symbol)) {
						method.RetVal.SetManagedReferenceType (basis.RetVal.Symbol);
						decisions.Add (Decision (method, "inherit-erased-managed-return"));
					}

					if (method.RetVal.FullName == basis.RetVal.FullName || IsCovariant (method.RetVal.Symbol, basis.RetVal.Symbol)) {
						var can_override = basis.IsAbstract || (!basis.IsFinal && (basis.IsVirtual || basis.IsOverride));
						method.IsOverride = can_override;
						if (can_override) {
							method.ManagedOverride = "override";
							var basis_visibility = basis.Visibility;
							if (method.Visibility == "public" && basis_visibility == "protected") {
								method.PublicContractFacadeName = Allocate ("Invoke" + method.Name, names);
								method.Visibility = basis_visibility;
								foreach (var contract in ImplementedInterfaces (type).SelectMany (i => i.Methods).Where (m =>
									m.JavaName == method.JavaName && (m.JniSignature == method.JniSignature ||
									method.NativeBridgeSignatures.Contains (m.JniSignature))))
									if (!method.PublicContractInterfaceMethods.Contains (contract))
										method.PublicContractInterfaceMethods.Add (contract);
								decisions.Add (new ManagedProjectionDecision (type.JavaName, Slot (method),
									"public-java-method-protected-managed-contract", method.PublicContractFacadeName, method.RetVal.FullName));
							}
						}
						if (!can_override) {
							method.ManagedOverride = "none";
							decisions.Add (Decision (method, "nonvirtual-managed-base-slot"));
						}
					}
				}
				if (type is ClassGen normalized)
					NormalizeProjectedMethods (normalized, decisions);
			}
			foreach (var type in types.OrderBy (Depth)) {
				if (type is InterfaceGen listener && listener.IsListener) {
					var listener_names = new HashSet<string> (listener.Methods.Select (m => m.AdjustedName), StringComparer.Ordinal);
					foreach (var group in listener.Methods.Where (m => m.EventName != "").GroupBy (m => m.AdjustedName).Where (g => g.Count () > 1))
						foreach (var method in group.OrderBy (m => m.JniSignature, StringComparer.Ordinal)) {
							var signature_name = string.Join ("And", method.Parameters.Select (p => {
								var name = new string (p.JavaType.Split ('.').Last ().Where (c => char.IsLetterOrDigit (c) || c == '_').ToArray ());
								return (name.Length == 0 ? "Value" : char.ToUpperInvariant (name [0]) + name.Substring (1)) + (p.IsArray ? "Array" : "");
							}));
							method.ListenerProjectionName = Allocate (method.AdjustedName + "With" +
								(signature_name.Length == 0 ? "NoArguments" : signature_name), listener_names);
							decisions.Add (new ManagedProjectionDecision (type.JavaName, method.JavaName + method.JniSignature,
								"allocate-distinct-listener-overload", method.ListenerProjectionName, method.RetVal.FullName));
						}
				}
				if (type is ClassGen klass) {
					NormalizeProjectedMethods (type, decisions);
					foreach (var method in type.GetAllMethods ().Where (m => !m.IsStatic)) {
						foreach (var contract in ImplementedInterfaces (type).SelectMany (i => i.Methods).Where (m =>
							!m.IsStatic && !m.IsInterfaceDefaultMethod && m.JavaName == method.JavaName &&
							(m.JniSignature == method.JniSignature || method.NativeBridgeSignatures.Contains (m.JniSignature)))) {
							if (method.Visibility == "public" && type.Methods.Contains (method) &&
								method.Name == contract.Name && method.RetVal.FullName == contract.RetVal.FullName &&
								method.Parameters.Select (p => p.Type).SequenceEqual (contract.Parameters.Select (p => p.Type)))
								continue;
							if (!method.PublicContractInterfaceMethods.Contains (contract)) {
								method.PublicContractInterfaceMethods.Add (contract);
								decisions.Add (Decision (method, "retain-explicit-native-interface-contract"));
							}
						}
					}
					PlanInheritedInterfaceContracts (klass, decisions);
					PlanInterfacePropertyContracts (klass, decisions);
					if (klass.IsAbstract)
						NormalizeAbstractInterfaceContracts (klass, decisions);
					MaterializeInheritedAbstractPropertyAliases (klass, options, decisions);
					if (!klass.IsAbstract)
						MaterializeInheritedAbstractMethods (klass, options, decisions);
				} else if (type is InterfaceGen iface) {
					ProjectDefaultInterfacePropertyOverrides (iface, options, decisions);
					PlanInterfaceInvokerPropertyContracts (iface, decisions);
				}
				AllocatePropertyCollisions (type, decisions);
				type.RefreshPropertyLookup ();
			}
			AllocateListenerArgumentNames (types, decisions);
			ReconcileFinalClrOverrides (types, decisions);
			foreach (var type in types.OfType<ClassGen> ().Where (t => !string.IsNullOrEmpty (t.KotlinBoxJniSignature)))
				PrepareValueClassHelperNames (type);
			if (options.ExperimentalSemanticModel) {
				foreach (var type in types) {
					type.Interfaces.RemoveAll (symbol => (symbol as GenBase ?? (symbol as GenericSymbol)?.Gen)?.IsApiExcluded == true);
					if (type.IsApiExcluded)
						type.ExcludeFromGeneration ();
				}
			}
			return decisions.AsReadOnly ();
		}

		public static IReadOnlyList<ManagedProjectionDecision> PrepareAbstractContracts (IEnumerable<GenBase> definitions, CodeGenerationOptions options)
		{
			var types = Flatten (definitions).ToArray ();
			var decisions = new List<ManagedProjectionDecision> ();
			foreach (var iface in types.OfType<InterfaceGen> ())
				iface.FillProperties ();
			foreach (var type in types.OfType<ClassGen> ().Where (t => t.IsAbstract).OrderBy (Depth)) {
				var contracts = ImplementedInterfaces (type).Concat (type.GetAllDerivedInterfaces ()
					.Where (i => i.IsGeneratable && !i.IsApiExcluded)).Distinct ().ToArray ();
				MaterializeAbstractInterfaceMethods (type, options, decisions, contracts);
				type.FillProperties ();
				foreach (var contract in contracts.SelectMany (i => i.Properties).Where (p =>
					!p.Getter.IsStatic && !p.Getter.IsInterfaceDefaultMethod)) {
					if (type.GetAllMethods ().Any (m => m.JavaName == contract.Getter.JavaName && Slot (m) == Slot (contract.Getter)) ||
						type.BaseGen?.GetPropertyByName (contract.Name, true) != null)
						continue;
					Method Copy (Method method)
					{
						var copy = method.Clone (type);
						copy.IsAbstract = copy.IsVirtual = true;
						copy.IsFinal = copy.IsOverride = false;
						copy.ManagedOverride = null;
						if (!copy.Validate (options, method.DeclaringType.TypeParameters, new CodeGeneratorContext ()))
							throw new InvalidDataException ($"Cannot retain abstract property {type.JavaName}.{contract.Name}.");
						return copy;
					}
					type.Properties.Add (new Property (contract.Name) {
						Getter = Copy (contract.Getter), Setter = contract.Setter == null ? null : Copy (contract.Setter),
					});
					decisions.Add (Decision (contract.Getter, "materialize-abstract-interface-property-before-derived-projection"));
				}
				type.RefreshPropertyLookup ();
			}
			return decisions;
		}

		static void ProjectDefaultInterfacePropertyOverrides (InterfaceGen type, CodeGenerationOptions options, List<ManagedProjectionDecision> decisions)
		{
			foreach (var method in type.Methods.Where (m => m.IsInterfaceDefaultMethod && !m.IsStatic &&
				m.OverriddenInterfaceMethod != null).ToArray ()) {
				var basis = method.OverriddenInterfaceMethod;
				var property = basis.DeclaringType.Properties.FirstOrDefault (p => p.Getter == basis && p.Setter == null);
				if (property == null || method.Parameters.Count != 0 || method.RetVal.FullName != basis.RetVal.FullName)
					continue;
				type.Methods.Remove (method);
				method.ExplicitInterface = options.GetOutputName (basis.DeclaringType.FullName);
				type.Properties.Add (new Property (property.Name) {
					Getter = method,
				});
				decisions.Add (Decision (method, "retain-default-interface-property-override-shape"));
			}
		}

		static void AllocatePropertyCollisions (GenBase type, List<ManagedProjectionDecision> decisions)
		{
			var names = new HashSet<string> (type.Properties.Select (p => p.Name).Concat (type.Methods.Select (m => m.Name))
				.Concat (type.Fields.Select (f => f.Name)).Concat (type.NestedTypes.Select (t => t.Name)), StringComparer.Ordinal);
			foreach (var group in type.Properties.GroupBy (p => p.Name).Where (g => g.Count () > 1).ToArray ()) {
				var ordered = group.OrderBy (p => p.Getter.NativeBridgeSignatures.Contains (p.Getter.JniSignature))
					.ThenBy (p => p.Getter.JniSignature, StringComparer.Ordinal).ToArray ();
				foreach (var property in ordered.Skip (1)) {
					var primary = ordered [0];
					if (property.Getter.JavaName == primary.Getter.JavaName && property.Getter.JniSignature == primary.Getter.JniSignature &&
						property.Getter.RetVal.FullName == primary.Getter.RetVal.FullName && property.Setter == null) {
						type.Properties.Remove (property);
						decisions.Add (Decision (property.Getter, "coalesce-duplicate-native-property-slot"));
						continue;
					}
					property.Name = Allocate (property.Name + "Native", names);
					foreach (var method in new [] { property.Getter, property.Setter }.Where (m => m != null)) {
						method.IsOverride = false;
						method.ManagedOverride = method.IsVirtual ? "virtual" : "none";
					}
					decisions.Add (Decision (property.Getter, "retain-native-property-name-collision"));
				}
			}
		}

		static void MaterializeInheritedAbstractPropertyAliases (ClassGen type, CodeGenerationOptions options, List<ManagedProjectionDecision> decisions)
		{
			var added = false;
			for (var basis = type.BaseGen; basis != null; basis = basis.BaseGen)
				foreach (var property in basis.Properties.Where (p => p.Getter?.IsAbstract == true && p.Setter == null)) {
					if (type.Properties.Any (p => p.Name == property.Name) ||
						!type.GetAllMethods ().Any (m => !m.IsStatic && !m.IsAbstract &&
							m.JavaName == property.Getter.JavaName && Slot (m) == Slot (property.Getter) &&
							m.RetVal.NativeType == property.Getter.RetVal.NativeType))
						continue;
					var getter = property.Getter.Clone (type);
					if (!getter.Validate (options, type.TypeParameters, new CodeGeneratorContext ()))
						throw new InvalidDataException ($"Cannot retain inherited property contract {type.JavaName}.{property.Name}.");
					getter.IsAbstract = false;
					getter.IsVirtual = false;
					getter.IsOverride = true;
					getter.ManagedOverride = "override";
					type.Properties.Add (new Property (property.Name) { Getter = getter });
					added = true;
					decisions.Add (Decision (getter, "retain-inherited-abstract-property-alias"));
				}
			if (added)
				NormalizeProjectedMethods (type, decisions);
		}

		static void MaterializeInheritedAbstractMethods (ClassGen type, CodeGenerationOptions options, List<ManagedProjectionDecision> decisions)
		{
			var supplied = new Dictionary<string, List<Method>> (StringComparer.Ordinal);
			for (var current = type; current != null; current = current.BaseGen)
				foreach (var method in current.Methods.Where (m => !m.IsStatic).ToArray ()) {
					var signature = method.Name + "(" + string.Join (",", method.Parameters.Select (p => p.Type)) + ")";
					if (!supplied.TryGetValue (signature, out var implementations))
						supplied.Add (signature, implementations = new List<Method> ());
					if (!method.IsAbstract) {
						implementations.Add (method);
						continue;
					}
					if (current == type || implementations.Any (m => m.RetVal.FullName == method.RetVal.FullName ||
						IsCovariant (m.RetVal.Symbol, method.RetVal.Symbol)))
						continue;
					var copy = method.Clone (type);
					copy.IsAbstract = copy.IsVirtual = copy.IsFinal = false;
					copy.IsOverride = true;
					copy.ManagedOverride = "override";
					copy.OverriddenInterfaceMethod = null;
					if (!copy.Validate (options, method.DeclaringType.GetValidationTypeParameters (options), new CodeGeneratorContext ()))
						throw new InvalidDataException ($"Cannot retain inherited concrete native contract {type.JavaName}.{method.JavaName}.");
					type.Methods.Add (copy);
					implementations.Add (copy);
					decisions.Add (Decision (copy, "retain-concrete-inherited-abstract-native-method"));
				}
			NormalizeProjectedMethods (type, decisions);
		}

		static void ReconcileFinalClrOverrides (IEnumerable<GenBase> types, List<ManagedProjectionDecision> decisions)
		{
			foreach (var type in types.OfType<ClassGen> ().OrderBy (Depth))
				foreach (var method in type.GetAllMethods ().Where (m => !m.IsStatic && (m.IsOverride || m.ManagedOverride == "override"))) {
					var property = type.Properties.FirstOrDefault (p => p.Getter == method || p.Setter == method);
					Method target = null;
					for (var basis = type.BaseGen; basis != null && target == null; basis = basis.BaseGen)
						target = property == null ? basis.Methods.FirstOrDefault (m => m.AdjustedName == method.AdjustedName &&
							m.Parameters.Select (p => p.Type).SequenceEqual (method.Parameters.Select (p => p.Type))) :
							basis.Properties.Where (p => p.AdjustedName == property.AdjustedName)
								.Select (p => property.Getter == method ? p.Getter : p.Setter).FirstOrDefault (m => m != null);
					if (target != null && (target.IsAbstract || !target.IsFinal && target.ManagedOverride != "none" &&
						(target.IsVirtual || target.IsOverride || target.ManagedOverride is "virtual" or "override")))
						continue;
					method.IsOverride = false;
					method.ManagedOverride = "none";
					decisions.Add (Decision (method, "respect-final-clr-base-member-shape"));
				}
		}

		internal static void PrepareValueClassHelperNames (ClassGen type)
		{
			if (type.KotlinBoxManagedName != null)
				return;
			var names = new HashSet<string> (type.GetAllMethods ().Select (m => m.AdjustedName)
				.Concat (type.Properties.Select (p => p.AdjustedName)).Concat (type.Fields.Select (f => f.Name))
				.Concat (type.NestedTypes.Select (t => t.Name)).Append (type.Name), StringComparer.Ordinal);
			type.KotlinBoxManagedName = Allocate ("Box", names);
			type.KotlinUnboxManagedName = Allocate ("Unbox", names);
			if (!string.IsNullOrEmpty (type.KotlinConstructorJniSignature)) {
				type.KotlinCreateValueManagedName = Allocate ("CreateValue", names);
				type.KotlinCreateManagedName = Allocate ("Create", names);
			}
		}

		static void ProjectInaccessibleSignatureTypes (IEnumerable<GenBase> types, CodeGenerationOptions options, List<ManagedProjectionDecision> decisions)
		{
			bool Accessible (GenBase type, bool publicMember, bool internalOwner)
			{
				for (var current = type; current != null; current = current.EnclosingType) {
					if (current.IsApiExcluded)
						return false;
					if (current.RawVisibility == "public" || internalOwner && current.RawVisibility == "internal" ||
						!publicMember && (current.RawVisibility == "protected" || current.RawVisibility == "protected internal"))
						continue;
					return false;
				}
				return true;
			}

			ISymbol Project (ISymbol symbol, bool publicMember, bool internalOwner)
			{
				if (symbol is ArraySymbol array && Project (array.ElementSymbol, publicMember, internalOwner) != array.ElementSymbol)
					return options.SymbolTable.Lookup ("java.lang.Object") ??
						throw new InvalidDataException ($"No java.lang.Object peer for inaccessible array type {array.JavaName}.");
				if (symbol is CollectionSymbol collection && collection.TypeArguments is ISymbol[] arguments) {
					var projected = arguments.Select (a => Project (a, publicMember, internalOwner)).ToArray ();
					if (!projected.SequenceEqual (arguments))
						return collection.WithTypeArguments (projected, options);
				}
				var type = symbol as GenBase ?? (symbol as GenericSymbol)?.Gen ??
					(symbol is GenericTypeParameter ? options.SymbolTable.Lookup (symbol.FullName) as GenBase : null);
				if (type == null || Accessible (type, publicMember, internalOwner))
					return symbol;
				return options.SymbolTable.Lookup ("java.lang.Object") ??
					throw new InvalidDataException ($"No java.lang.Object peer for inaccessible signature type {type.JavaName}.");
			}

			foreach (var type in types) {
				var internalOwner = type.RawVisibility == "internal" || type.EnclosingType != null && type.EnclosingType.RawVisibility == "internal";
				foreach (var method in type.GetAllMethods ()) {
					if (method.RetVal.KotlinValueProjection is ClassGen value && !Accessible (value, method.Visibility == "public", internalOwner)) {
						method.RetVal.UseKotlinUnderlyingType ();
						decisions.Add (Decision (method, "restricted-value-class-retains-underlying-native-value"));
					}
					var result = Project (method.RetVal.Symbol, method.Visibility == "public", internalOwner);
					if (result != method.RetVal.Symbol) {
						method.RetVal.SetManagedReferenceType (result);
						decisions.Add (Decision (method, "retain-inaccessible-native-return-as-opaque-peer"));
					}
				}
				var members = type.GetAllMethods ().Cast<MethodBase> ();
				if (type is ClassGen klass)
					members = members.Concat (klass.Ctors);
				foreach (var member in members)
					foreach (var parameter in member.Parameters) {
						if (parameter.KotlinValueProjection is ClassGen value && !Accessible (value, member.Visibility == "public", internalOwner)) {
							parameter.UseKotlinUnderlyingType ();
							decisions.Add (new ManagedProjectionDecision (type.JavaName, member.Name,
								"restricted-value-class-parameter-retains-underlying-native-value", member.Name, parameter.Type));
						}
						var projected = Project (parameter.Symbol, member.Visibility == "public", internalOwner);
						if (projected == parameter.Symbol)
							continue;
						parameter.SetManagedReferenceType (projected);
						decisions.Add (new ManagedProjectionDecision (type.JavaName,
							member is Method method ? method.JavaName + method.JniSignature : "<init>" + ((Ctor) member).JniSignature,
							"retain-inaccessible-native-parameter-as-opaque-peer", member.Name, projected.FullName));
					}
				foreach (var field in type.Fields) {
					var projected = Project (field.Symbol, field.Visibility == "public", internalOwner);
					if (projected == field.Symbol)
						continue;
					field.SetManagedReferenceType (projected);
					decisions.Add (new ManagedProjectionDecision (type.JavaName, field.JavaName + ":" + field.NativeJniSignature,
						"retain-inaccessible-native-field-contract", field.Name, projected.FullName));
				}
			}
		}

		static void ReconcileManagedParameters (Method method, Method basis, List<ManagedProjectionDecision> decisions)
		{
			if (Slot (method) != Slot (basis) && method.DeclaringType.GetAllMethods ().Any (m =>
				m != method && !m.IsStatic && !m.IsAbstract && m.JavaName == method.JavaName && Slot (m) == Slot (basis)))
				return;
			for (var i = 0; i < method.Parameters.Count; i++) {
				var parameter = method.Parameters [i];
				var base_parameter = basis.Parameters [i];
				if (parameter.Type == base_parameter.Type)
					continue;
				if (parameter.Type == "int" && (base_parameter.IsEnumified || base_parameter.Symbol.IsEnum)) {
					parameter.SetGeneratedEnumType (base_parameter.Type);
					decisions.Add (Decision (method, "inherit-managed-enum-parameter"));
				} else if (parameter.NativeType == base_parameter.NativeType && (parameter.IsArray == base_parameter.IsArray ||
					parameter.NativeType == "IntPtr" && basis.DeclaringType is ClassGen && method.NativeBridgeSignatures.Contains (basis.JniSignature))) {
					parameter.SetManagedReferenceType (base_parameter.Symbol);
					decisions.Add (Decision (method, base_parameter.Type == "Java.Lang.Object"
						? "inherit-erased-managed-parameter" : "inherit-exact-managed-parameter"));
				}
			}
		}

		static void AllocateListenerArgumentNames (IEnumerable<GenBase> types, List<ManagedProjectionDecision> decisions)
		{
			foreach (var scope in types.GroupBy (t => t.FullName.Substring (0, t.FullName.Length - t.Name.Length))) {
				var names = new HashSet<string> (scope.Select (t => t.Name), StringComparer.Ordinal);
				foreach (var listener in scope.OfType<InterfaceGen> ().Where (i => i.IsListener).OrderBy (i => i.JavaName, StringComparer.Ordinal)) {
					foreach (var method in listener.Methods.Where (m => m.EventName != "" &&
						(m.RetVal.IsVoid || m.IsEventHandlerWithHandledProperty) &&
						(!m.IsSimpleEventHandler || m.IsEventHandlerWithHandledProperty))) {
						var name = listener.GetArgsName (method);
						if (names.Add (name))
							continue;
						var allocated = Allocate (listener.Name.Substring (1) + name, names);
						if (listener.Methods.Count > 1)
							method.ArgsType = allocated;
						else
							listener.ArgsType = allocated;
						decisions.Add (new ManagedProjectionDecision (listener.JavaName, method.JavaName + method.JniSignature,
							"allocate-distinct-listener-argument-type", allocated, method.RetVal.FullName));
					}
				}
			}
		}

		static void ProjectInheritedPropertySetters (ClassGen type, CodeGenerationOptions options, List<ManagedProjectionDecision> decisions)
		{
			foreach (var method in type.Methods.Where (m => !m.IsStatic && m.CanSet).ToArray ()) {
				var basis = FindBaseMethod (type, method);
				var property = basis?.DeclaringType.Properties.FirstOrDefault (p => p.Setter == basis);
				if (property?.Getter == null || type.Properties.Any (p => p.Name == property.Name))
					continue;
				var getter = property.Getter.Clone (type);
				if (!getter.Validate (options, type.TypeParameters, new CodeGeneratorContext ()))
					continue;
				getter.IsOverride = true;
				getter.ManagedOverride = "override";
				type.Properties.Add (new Property (property.Name) { Getter = getter, Setter = method });
				type.Methods.Remove (method);
				decisions.Add (new ManagedProjectionDecision (type.JavaName, method.JavaName + method.JniSignature,
					"inherit-managed-property-setter-shape", property.Name, getter.RetVal.FullName));
			}
			type.RefreshPropertyLookup ();
		}

		static void PlanInheritedInterfaceContracts (ClassGen type, List<ManagedProjectionDecision> decisions)
		{
			var inherited = new List<Method> ();
			for (var current = type.BaseGen; current != null; current = current.BaseGen)
				inherited.AddRange (current.GetAllMethods ().Where (m => !m.IsStatic));
			var methods = type.GetAllMethods ().Concat (inherited).ToArray ();
			foreach (var contract in ImplementedInterfaces (type).SelectMany (i => i.Methods).Where (m => !m.IsStatic && !m.IsInterfaceDefaultMethod)) {
				if (methods.Any (m => (type.Methods.Contains (m) && m.PublicContractInterfaceMethods.Contains (contract)) ||
					(m.DeclaringType.Methods.Contains (m) && m.Visibility == "public" && m.JavaName == contract.JavaName &&
					m.Name == contract.Name && m.RetVal.FullName == contract.RetVal.FullName &&
					m.Parameters.Select (p => p.Type).SequenceEqual (contract.Parameters.Select (p => p.Type)))))
					continue;
				var inherited_native_slot = inherited.Any (m => m.JavaName == contract.JavaName && m.JniSignature == contract.JniSignature) ||
					type.Properties.Any (p => p.Getter.JavaName == contract.JavaName && p.Getter.JniSignature == contract.JniSignature);
				var default_bridge = HasInterfaceBridge (type, contract);
				if ((type.IsAbstract && !inherited_native_slot && !default_bridge) || type.InheritedInterfaceContracts.Contains (contract))
					continue;
				type.InheritedInterfaceContracts.Add (contract);
				decisions.Add (new ManagedProjectionDecision (type.JavaName, contract.JavaName + contract.JniSignature,
					inherited_native_slot ? "retain-inherited-native-interface-contract" : "retain-concrete-native-interface-contract",
					contract.Name, contract.RetVal.FullName));
			}
		}

		static bool HasInterfaceBridge (GenBase type, Method contract) =>
			ImplementedInterfaces (type).SelectMany (i => i.Methods.Concat (i.ImportedDefaultInterfaceContracts)).Any (m =>
				m.JavaName == contract.JavaName && (m.NativeBridgeSignatures.Contains (contract.JniSignature) ||
					(m.IsInterfaceDefaultMethod && Slot (m) == Slot (contract))));

		static void MaterializeAbstractInterfaceMethods (ClassGen type, CodeGenerationOptions options, List<ManagedProjectionDecision> decisions,
			IEnumerable<InterfaceGen> contracts = null)
		{
			foreach (var contract in (contracts ?? ImplementedInterfaces (type)).SelectMany (i => i.Methods)
				.Where (m => m.IsAbstract && !m.IsStatic && !m.IsInterfaceDefaultMethod)) {
				var present = false;
				for (var current = type; current != null && !present; current = current.BaseGen)
					present = (!current.IsGeneratable && ImplementedInterfaces (current).Any (i => i == contract.DeclaringType)) ||
						current.GetAllMethods ().Any (m => !m.IsStatic && m.JavaName == contract.JavaName &&
							(Slot (m) == Slot (contract) || m.NativeBridgeSignatures.Contains (contract.JniSignature)));
				if (present || HasInterfaceBridge (type, contract))
					continue;
				var method = contract.Clone (type);
				method.IsAbstract = method.IsVirtual = true;
				method.IsFinal = method.IsOverride = false;
				method.OverriddenInterfaceMethod = null;
				method.ManagedOverride = null;
				if (!method.Validate (options, contract.DeclaringType.TypeParameters, new CodeGeneratorContext ()))
					throw new InvalidDataException ($"Cannot project abstract interface contract {contract.DeclaringType.JavaName}.{contract.JavaName}{contract.JniSignature} on {type.JavaName}.");
				type.Methods.Add (method);
				type.ExplicitlyImplementedInterfaceMethods.Remove (contract.GetSignature ());
				decisions.Add (Decision (method, "materialize-actual-abstract-class-contract"));
			}
		}

		static void PlanInterfacePropertyContracts (ClassGen type, List<ManagedProjectionDecision> decisions)
		{
			var classes = new List<ClassGen> ();
			for (var current = type; current != null; current = current.BaseGen)
				classes.Add (current);
			var properties = classes.SelectMany (c => c.Properties).ToArray ();
			var methods = classes.SelectMany (c => c.GetAllMethods ()).ToArray ();
			foreach (var contract in ImplementedInterfaces (type).SelectMany (i => i.Properties).Where (p =>
				!p.Getter.IsStatic && !p.Getter.IsInterfaceDefaultMethod)) {
				if (classes.Any (c => c.InterfacePropertyContracts.Contains (contract)) || properties.Any (p =>
					p.Getter.Visibility == "public" && !p.Getter.IsStatic &&
					(contract.Setter == null || p.Setter?.Visibility == "public") && HasManagedPropertyContract (p, contract)))
					continue;
				if (type.IsAbstract && new [] { contract.Getter, contract.Setter }.Where (m => m != null)
					.Any (accessor => !methods.Any (m => !m.IsStatic && !m.IsAbstract && Slot (m) == Slot (accessor))))
					continue;
				type.InterfacePropertyContracts.Add (contract);
				decisions.Add (new ManagedProjectionDecision (type.JavaName, contract.Getter.JavaName + contract.Getter.JniSignature,
					"retain-explicit-native-interface-property", contract.Name, contract.Getter.RetVal.FullName));
			}
		}

		static void PlanInterfaceInvokerPropertyContracts (InterfaceGen type, List<ManagedProjectionDecision> decisions)
		{
			var properties = new Dictionary<string, Property> (StringComparer.Ordinal);
			foreach (var contract in new [] { type }.Concat (type.GetAllDerivedInterfaces ().Where (i => i.HasProjectedManagedContract))
				.SelectMany (i => i.Properties).Where (p => !p.Getter.IsStatic && !p.Getter.IsInterfaceDefaultMethod)) {
				if (!properties.TryGetValue (contract.AdjustedName, out var property)) {
					properties.Add (contract.AdjustedName, contract);
					continue;
				}
				if (HasManagedPropertyContract (property, contract) || type.InheritedPropertyContracts.Contains (contract))
					continue;
				type.InheritedPropertyContracts.Add (contract);
				decisions.Add (new ManagedProjectionDecision (type.JavaName, contract.Getter.JavaName + contract.Getter.JniSignature,
					"retain-inherited-interface-property-accessors", contract.Name, contract.Getter.RetVal.FullName));
			}
		}

		static bool HasManagedPropertyContract (Property property, Property contract) =>
			property.AdjustedName == contract.AdjustedName && property.Getter.JavaName == contract.Getter.JavaName &&
			property.Getter.RetVal.FullName == contract.Getter.RetVal.FullName &&
			(contract.Setter == null || (property.Setter != null && property.Setter.JavaName == contract.Setter.JavaName &&
			property.Setter.Parameters [0].Type == contract.Setter.Parameters [0].Type));

		static void NormalizeProjectedMethods (GenBase type, List<ManagedProjectionDecision> decisions)
		{
			foreach (var property in type.Properties)
				foreach (var duplicate in type.Methods.Where (m => m.JavaName == property.Getter.JavaName &&
					m.JniSignature == property.Getter.JniSignature && m.RetVal.FullName == property.Getter.RetVal.FullName).ToArray ()) {
					foreach (var contract in duplicate.PublicContractInterfaceMethods)
						if (!property.Getter.PublicContractInterfaceMethods.Contains (contract))
							property.Getter.PublicContractInterfaceMethods.Add (contract);
					type.Methods.Remove (duplicate);
					decisions.Add (Decision (duplicate, "coalesce-native-method-and-property-accessor"));
				}
			foreach (var group in type.Methods.GroupBy (m => m.JavaName + m.JniSignature).ToArray ()) {
				foreach (var duplicate in group.Skip (1).ToArray ()) {
					type.Methods.Remove (duplicate);
					decisions.Add (new ManagedProjectionDecision (type.JavaName, duplicate.JavaName + duplicate.JniSignature,
						"coalesce-duplicate-native-slot", group.First ().Name, group.First ().RetVal.FullName));
				}
			}
			var names = new HashSet<string> (type.Methods.Select (m => m.Name).Concat (type.Properties.Select (p => p.Name))
				.Concat (type.Fields.Select (f => f.Name)).Concat (type.NestedTypes.Select (t => t.Name)).Append (type.Name), StringComparer.Ordinal);
			var static_collisions = type.Methods.GroupBy (m => $"{m.Name}({string.Join (",", m.Parameters.Select (p => p.Type))})")
				.Where (g => g.Any (m => m.IsStatic) && g.Any (m => !m.IsStatic)).Select (g => g.First ().Name).Distinct ().ToArray ();
			foreach (var name in static_collisions) {
				var methods = type.Methods.Where (m => m.IsStatic && m.Name == name).ToArray ();
				var allocated = Allocate (methods.All (m => m.RetVal.FullName == type.FullName) ? "Create" + name : name + "Static", names);
				foreach (var method in methods) {
					method.Name = allocated;
					decisions.Add (Decision (method, "static-instance-managed-signature-collision"));
				}
			}
			foreach (var group in type.Methods.Where (m => !m.IsStatic)
				.GroupBy (m => $"{m.Name}({string.Join (",", m.Parameters.Select (p => p.Type))})").Where (g => g.Count () > 1)) {
				var ordinary = group.Where (m => !m.NativeBridgeSignatures.Contains (m.JniSignature)).ToArray ();
				Method inherited_primary = null;
				for (var basis = type.BaseGen; basis != null && inherited_primary == null; basis = basis.BaseGen)
					foreach (var contract in basis.Methods.Where (m => m.IsAbstract && m.Name == group.First ().Name &&
						m.Parameters.Select (p => p.Type).SequenceEqual (group.First ().Parameters.Select (p => p.Type)))) {
						if (ordinary.Any (m => m.RetVal.FullName == contract.RetVal.FullName || IsCovariant (m.RetVal.Symbol, contract.RetVal.Symbol)))
							continue;
						inherited_primary = group.OrderByDescending (m => Slot (m) == Slot (contract))
							.FirstOrDefault (m => !m.IsAbstract && (m.RetVal.FullName == contract.RetVal.FullName ||
								IsCovariant (m.RetVal.Symbol, contract.RetVal.Symbol)));
					}
				var primary = inherited_primary ?? (ordinary.Length == 1 ? ordinary [0] :
					ordinary.Length == 0 && group.All (m => !m.IsAbstract) ?
						group.OrderByDescending (m => group.Count (other => IsCovariant (m.RetVal.Symbol, other.RetVal.Symbol)))
							.ThenByDescending (m => CollectionReturnRank (m.JniSignature))
							.ThenBy (m => m.JniSignature, StringComparer.Ordinal).First () : null);
				if (primary == null)
					continue;
				foreach (var bridge in group.Where (m => m != primary && !m.IsAbstract &&
					(inherited_primary != null || m.NativeBridgeSignatures.Contains (m.JniSignature)))) {
					var suffix = bridge.NativeBridgeSignatures.Contains (bridge.JniSignature) ? "Bridge" :
						"Native" + Convert.ToHexString (System.Security.Cryptography.SHA256.HashData (
							System.Text.Encoding.UTF8.GetBytes (bridge.JniSignature))).Substring (0, 8);
					bridge.Name = Allocate (bridge.Name + suffix, names);
					bridge.IsVirtual = false;
					bridge.IsOverride = false;
					bridge.ManagedOverride = "none";
					decisions.Add (new ManagedProjectionDecision (type.JavaName, bridge.JavaName + bridge.JniSignature,
						"retain-native-bridge-as-nonvirtual-managed-alias", bridge.Name, bridge.RetVal.FullName));
				}
			}

			static int CollectionReturnRank (string descriptor)
			{
				var result = descriptor.Substring (descriptor.IndexOf (')') + 1);
				return result switch {
					"Ljava/util/NavigableSet;" or "Ljava/util/NavigableMap;" => 5,
					"Ljava/util/SortedSet;" or "Ljava/util/SortedMap;" => 4,
					"Ljava/util/List;" or "Ljava/util/Set;" or "Ljava/util/Map;" => 3,
					"Ljava/util/Collection;" => 2,
					"Ljava/lang/Iterable;" => 1,
					_ => 0,
				};
			}
			foreach (var group in type.Methods.GroupBy (m => $"{m.AdjustedName}({string.Join (",", m.Parameters.Select (p => p.Type))})")
				.Where (g => g.Count () > 1).ToArray ()) {
				var renameable = group.Where (m => !m.IsAbstract && !m.IsOverride &&
					(m.IsStatic || FindBaseMethod (type, m) is not Method basis || m.RetVal.NativeType != basis.RetVal.NativeType)).ToArray ();
				if (group.Count () - renameable.Length > 1)
					continue;
				var distinct_returns = group.Select (m => m.RetVal.FullName).Distinct ().Count () > 1;
				foreach (var method in renameable.OrderBy (m => m.JavaName + m.JniSignature, StringComparer.Ordinal)) {
					if (!JniSignatureUtilities.TryParseMethodSignature (method.JniSignature, out var parameters, out var return_type))
						throw new InvalidDataException ($"Invalid JVM descriptor for {type.JavaName}.{method.JavaName}: {method.JniSignature}.");
					var hint = distinct_returns ? "Returning" + IdentifierHint (method.RetVal.FullName.Split ('.').Last ()) :
						parameters.Length > 0 ? "With" + string.Join ("And", parameters.Select (NativeTypeHint)) :
						"Returning" + NativeTypeHint (return_type);
					method.Name = Allocate (method.Name + hint, names);
					decisions.Add (Decision (method, "retain-erased-overload-with-distinct-managed-name"));
				}
			}
		}

		static string IdentifierHint (string value) =>
			new string (value.Where (c => char.IsLetterOrDigit (c) || c == '_').ToArray ());

		static string NativeTypeHint (string descriptor)
		{
			var rank = descriptor.TakeWhile (c => c == '[').Count ();
			var type = descriptor.Substring (rank);
			var name = type [0] == 'L' ? IdentifierHint (type.Substring (1, type.Length - 2).Split ('/').Last ()) :
				type switch {
					"Z" => "Boolean",
					"B" => "Byte",
					"C" => "Char",
					"S" => "Int16",
					"I" => "Int32",
					"J" => "Int64",
					"F" => "Single",
					"D" => "Double",
					"V" => "Void",
					_ => throw new InvalidDataException ($"Invalid JVM type descriptor: {descriptor}."),
				};
			return name + string.Concat (Enumerable.Repeat ("Array", rank));
		}

		static void NormalizeAbstractInterfaceContracts (ClassGen type, List<ManagedProjectionDecision> decisions)
		{
			var methods = new List<Method> ();
			for (var current = type; current != null; current = current.BaseGen)
				methods.AddRange (current.GetAllMethods ());
			foreach (var contract in ImplementedInterfaces (type).SelectMany (i => i.Methods).Where (m =>
				m.IsAbstract && !m.IsStatic && m.IsGeneric && !m.IsInterfaceDefaultMethod)) {
				if (methods.Any (m => m.PublicContractInterfaceMethods.Contains (contract) ||
					(m.Visibility == "public" && m.JavaName == contract.JavaName && m.Name == contract.Name &&
					m.RetVal.FullName == contract.RetVal.FullName &&
					m.Parameters.Select (p => p.Type).SequenceEqual (contract.Parameters.Select (p => p.Type)))))
					continue;
				if (type.ExplicitlyImplementedInterfaceMethods.Remove (contract.GetSignature ()))
					decisions.Add (new ManagedProjectionDecision (type.JavaName, contract.JavaName + contract.JniSignature,
						"abstract-interface-slot-not-an-explicit-generic-adapter", contract.Name, contract.RetVal.FullName));
			}
		}

		static ManagedProjectionDecision Decision (Method method, string reason) =>
			new ManagedProjectionDecision (method.DeclaringType.JavaName, Slot (method), reason, method.Name, method.RetVal.FullName);

		static Method FindFamilyRoot (Method method)
		{
			var root = method;
			for (var basis = FindBaseMethod (method.DeclaringType, method); basis != null; basis = FindBaseMethod (basis.DeclaringType, basis))
				root = basis;
			if (!root.DeclaringType.IsGeneratable)
				return root;
			var contract = ImplementedInterfaces (root.DeclaringType).SelectMany (i => i.GetAllMethods ())
				.FirstOrDefault (m => !m.IsStatic && MatchesInterfaceContract (root, m));
			return contract == null ? root : FindFamilyRoot (contract);
		}

		static bool MatchesInterfaceContract (Method method, Method contract)
		{
			if (method.JavaName != contract.JavaName)
				return false;
			if (Slot (method, method.ManagedBaseJniSignature) == Slot (contract))
				return true;
			if (!method.NativeBridgeSignatures.Contains (contract.JniSignature) || contract.DeclaringType is not InterfaceGen iface)
				return false;
			for (var current = method.DeclaringType; current != null; current = current.BaseGen) {
				var mappings = new Dictionary<string, string> ();
				if (current.GetGenericMappings (iface, mappings) && method.Parameters.Count == contract.Parameters.Count &&
					method.Parameters.Zip (contract.Parameters, (m, c) => m.Type == c.GetGenericType (mappings)).All (matches => matches)) {
					method.ManagedBaseJniSignature = contract.JniSignature;
					return true;
				}
			}
			return false;
		}

		static Method FindBaseMethod (GenBase type, Method method)
		{
			if (method == null)
				return null;
			Method implicit_contract = null;
			for (var basis = type.BaseGen; basis != null; basis = basis.BaseGen) {
				var methods = basis.GetAllMethods ().Where (m => !m.IsStatic && Slot (m) == Slot (method, method.ManagedBaseJniSignature)).ToArray ();
				var exact = methods.FirstOrDefault (m => m.JniSignature == method.JniSignature);
				if (exact != null)
					return exact;
				if (methods.Length == 1)
					return methods [0];
				var bridge_contract = basis.GetAllMethods ().FirstOrDefault (m => !m.IsStatic &&
					m.JavaName == method.JavaName && method.NativeBridgeSignatures.Contains (m.JniSignature) &&
					m.Parameters.Count == method.Parameters.Count &&
					method.Parameters.Zip (m.Parameters, (actual, erased) => actual.JniType == erased.JniType ||
						erased.JniType == "Ljava/lang/Object;" && actual.NativeType == "IntPtr").All (compatible => compatible));
				if (bridge_contract != null) {
					method.ManagedBaseJniSignature = bridge_contract.JniSignature;
					return bridge_contract;
				}
				if (implicit_contract == null && basis is ClassGen klass && klass.IsAbstract && klass.IsGeneratable)
					implicit_contract = ImplementedInterfaces (basis).SelectMany (i => i.GetAllMethods ())
						.FirstOrDefault (m => !m.IsStatic && m.IsAbstract && !m.IsInterfaceDefaultMethod &&
							Slot (m) == Slot (method, method.ManagedBaseJniSignature));
			}
			return implicit_contract;
		}

		static bool IsCovariant (ISymbol derived, ISymbol basis)
		{
			var type = derived as GenBase ?? (derived as GenericSymbol)?.Gen;
			var target = basis as GenBase ?? (basis as GenericSymbol)?.Gen;
			if (type == null || target == null)
				return false;
			for (var current = type; current != null; current = current.BaseGen)
				if (current.FullName == target.FullName)
					return true;
			return ImplementedInterfaces (type).Any (i => i.FullName == target.FullName);
		}

		static IEnumerable<InterfaceGen> ImplementedInterfaces (GenBase type)
		{
			var seen = new HashSet<InterfaceGen> ();
			for (var current = type; current != null; current = current.BaseGen)
				foreach (var contract in current.GetAllDerivedInterfaces ())
					if (contract.HasProjectedManagedContract && seen.Add (contract))
						yield return contract;
		}

		static Method FindManagedBaseMethod (GenBase type, Method method)
		{
			for (var basis = type.BaseGen; basis != null; basis = basis.BaseGen) {
				var match = basis.GetAllMethods ().FirstOrDefault (m => !m.IsStatic && m.Name == method.Name &&
					m.Parameters.Select (p => p.Type).SequenceEqual (method.Parameters.Select (p => p.Type)));
				if (match != null)
					return match;
			}
			return null;
		}

		static string Slot (Method method, string descriptor = null)
		{
			descriptor ??= method.JniSignature;
			return method.JavaName + descriptor.Substring (0, descriptor.IndexOf (')') + 1);
		}

		static int Depth (GenBase type) => type.BaseGen == null ? 0 : Depth (type.BaseGen) + 1;

		static IEnumerable<string> NamespacePrefixes (string value)
		{
			while (!string.IsNullOrEmpty (value)) {
				yield return value;
				var separator = value.LastIndexOf ('.');
				value = separator < 0 ? "" : value.Substring (0, separator);
			}
		}

		static string Allocate (string name, HashSet<string> names)
		{
			var candidate = name;
			for (var suffix = 2; !names.Add (candidate); suffix++)
				candidate = name + suffix;
			return candidate;
		}

		static IEnumerable<GenBase> Flatten (IEnumerable<GenBase> types)
		{
			foreach (var type in types) {
				yield return type;
				foreach (var nested in Flatten (type.NestedTypes))
					yield return nested;
			}
		}

		public static void WriteReport (string path, IReadOnlyList<ManagedProjectionDecision> decisions)
		{
			using (var stream = File.Create (path))
				JsonSerializer.Serialize (stream, decisions, new JsonSerializerOptions { WriteIndented = true });
		}
	}
}
