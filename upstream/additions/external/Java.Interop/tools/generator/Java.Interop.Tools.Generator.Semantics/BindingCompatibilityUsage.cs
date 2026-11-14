#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Java.Interop.Tools.Generator.Semantics;

public sealed record BindingUsageEvidence (string Owner, string Type, string Use, string Consumer, string? Member);
public sealed record BindingCompatibilitySelection (string Scope, BindingUsageEvidence [] Evidence, BindingCompatibilityProfile Profile);

public static class BindingCompatibilityUsage
{
	public static BindingCompatibilitySelection Select (BindingCompatibilityProfile full, IEnumerable<string> consumers, string references)
	{
		using var resolver = new DefaultAssemblyResolver ();
		resolver.AddSearchDirectory (references);
		var evidence = new HashSet<BindingUsageEvidence> ();
		var contracts = full.Types.ToDictionary (t => t.Owner + "|" + t.Name, StringComparer.Ordinal);
		var required = contracts.Keys.ToDictionary (k => k, _ => new HashSet<string> (StringComparer.Ordinal), StringComparer.Ordinal);
		var roles = contracts.Keys.ToDictionary (k => k, _ => new HashSet<string> (StringComparer.Ordinal), StringComparer.Ordinal);
		var owners = full.Types.Select (t => t.Owner).ToHashSet (StringComparer.Ordinal);
		var inputs = full.Inputs.ToDictionary (i => i.Identity, StringComparer.Ordinal);
		string Key (TypeDefinition type) => type.Module.Assembly.Name.FullName + "|" + type.FullName;
		void TypeUse (TypeDefinition type, string use, string consumer)
		{
			string key = Key (type);
			if (!contracts.ContainsKey (key))
				return;
			roles [key].Add (use);
			evidence.Add (new (type.Module.Assembly.Name.FullName, type.FullName, use, consumer, null));
		}
		void MethodUse (MethodDefinition method, string use, string consumer)
		{
			string key = Key (method.DeclaringType);
			if (!contracts.TryGetValue (key, out var contract))
				return;
			var described = BindingCompatibilityProfile.Describe (method.DeclaringType).Members
				.FirstOrDefault (m => m.Kind == "method" && m.Signature == BindingCompatibilityProfile.DescribeMethod (method));
			if (described == null)
				return;
			required [key].Add (described.Signature);
			roles [key].Add (use);
			evidence.Add (new (contract.Owner, contract.Name, use, consumer, described.Signature));
		}
		foreach (var path in consumers) {
			using var assembly = AssemblyDefinition.ReadAssembly (path, new ReaderParameters { AssemblyResolver = resolver });
			inputs [assembly.Name.FullName] = new (assembly.Name.FullName,
				Convert.ToHexString (SHA256.HashData (File.ReadAllBytes (path))).ToLowerInvariant ());
			foreach (var type in BindingCompatibilityProfile.Flatten (assembly.MainModule.Types)) {
				string consumer = assembly.Name.Name + ":" + type.FullName;
				var bases = new List<TypeDefinition> ();
				for (var reference = type.BaseType; reference != null;) {
					var basis = reference.Resolve () ?? throw new InvalidDataException ($"Unresolved base {reference.FullName}.");
					bases.Add (basis);
					TypeUse (basis, "subclass", consumer);
					reference = basis.BaseType;
				}
				foreach (var iface in type.Interfaces) {
					var definition = iface.InterfaceType.Resolve () ?? throw new InvalidDataException ($"Unresolved interface {iface.InterfaceType.FullName}.");
					TypeUse (definition, "implement", consumer);
					foreach (var method in definition.Methods.Where (m => !m.IsStatic && m.IsAbstract))
						MethodUse (method, "interface-obligation", consumer);
				}
				foreach (var method in type.Methods) {
					if (method.IsVirtual && !method.IsNewSlot)
						foreach (var basis in bases)
							foreach (var slot in basis.Methods.Where (m => m.IsVirtual && m.Name == method.Name &&
									m.Parameters.Select (p => p.ParameterType.FullName).SequenceEqual (method.Parameters.Select (p => p.ParameterType.FullName))))
								MethodUse (slot, "override", consumer + "::" + method.Name);
					if (!method.HasBody)
						continue;
					foreach (var instruction in method.Body.Instructions) {
						if (instruction.Operand is MethodReference called) {
							string owner = called.DeclaringType.GetElementType ().Scope is AssemblyNameReference scope ? scope.FullName : "";
							if (!owners.Contains (owner))
								continue;
							var target = called.Resolve () ?? throw new InvalidDataException ($"Unresolved member {called.FullName}.");
							MethodUse (target, instruction.OpCode.Code == Code.Newobj ? "construct" :
								target.IsConstructor ? "base-constructor" : "member-call", consumer + "::" + method.Name);
						} else if (instruction.Operand is FieldReference field) {
							string owner = field.DeclaringType.Scope is AssemblyNameReference scope ? scope.FullName : "";
							string key = owner + "|" + field.DeclaringType.FullName;
							if (!contracts.TryGetValue (key, out var contract))
								continue;
							foreach (var member in contract.Members.Where (m => m.Kind == "field" && m.Name == field.Name))
								required [key].Add (member.Signature);
							TypeUse (field.DeclaringType.Resolve (), "field-access", consumer + "::" + method.Name);
						}
					}
				}
			}
		}
		foreach (var root in full.Roots)
			evidence.Add (new (root.Owner, root.Type, "public-signature", root.ExposedBy, null));
		var selected = full.Types.Select (t => {
			string key = t.Owner + "|" + t.Name;
			var methods = required [key];
			if (roles [key].Contains ("subclass") || roles [key].Contains ("construct") || roles [key].Contains ("base-constructor")) {
				// Legacy XAPeerMembers uses these virtual properties without a direct consumer MemberRef.
				foreach (var member in t.Members.Where (m => m.Kind == "method" &&
						(m.Name == "get_ThresholdClass" || m.Name == "get_ThresholdType" || m.Name == "get_JniPeerMembers"))) {
					methods.Add (member.Signature);
					evidence.Add (new (t.Owner, t.Name, "legacy-interop-dispatch", "Android.Runtime.XAPeerMembers", member.Signature));
				}
			}
			var names = t.Members.Where (m => methods.Contains (m.Signature)).Select (m => m.Name).ToHashSet (StringComparer.Ordinal);
			var members = t.Members.Where (m => methods.Contains (m.Signature) ||
				m.Kind == "property" && (names.Contains ("get_" + m.Name) || names.Contains ("set_" + m.Name)) ||
				m.Kind == "event" && (names.Contains ("add_" + m.Name) || names.Contains ("remove_" + m.Name))).ToArray ();
			return t with { Members = members, Preservation = roles [key].Count == 0 ? "identity" : "observed" };
		}).ToArray ();
		return new ("Observed compiled consumers only; not a guarantee for unlisted plugins, reflection, or arbitrary subclasses.",
			evidence.OrderBy (e => e.Owner, StringComparer.Ordinal).ThenBy (e => e.Type, StringComparer.Ordinal)
				.ThenBy (e => e.Use, StringComparer.Ordinal).ThenBy (e => e.Consumer, StringComparer.Ordinal).ThenBy (e => e.Member, StringComparer.Ordinal).ToArray (),
			full with { Types = selected, Inputs = inputs.Values.OrderBy (i => i.Identity, StringComparer.Ordinal).ToArray () });
	}
}
