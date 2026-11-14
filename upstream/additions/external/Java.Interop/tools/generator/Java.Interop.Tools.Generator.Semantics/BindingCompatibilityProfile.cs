#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Mono.Cecil;
using MonoDroid.Generation;

namespace Java.Interop.Tools.Generator.Semantics;

public sealed record BindingContractAssembly (string Identity, string Sha256);
public sealed record BindingContractMember (string Kind, string Name, string Signature, string Flags, string? JniName, string? JniSignature,
	string []? ParameterTypes = null, string? ReturnType = null);
public sealed record BindingContractType (string Owner, string Name, string? JniName, string Flags, string? BaseType,
	string [] Interfaces, string [] Constraints, BindingContractMember [] Members, string Preservation = "full");
public sealed record BindingContractRoot (string Type, string Owner, string ExposedBy);
public sealed record BindingCompatibilityProfile (int SchemaVersion, BindingContractAssembly [] Inputs,
	BindingContractRoot [] Roots, BindingContractType [] Types)
{
	public static readonly JsonSerializerOptions JsonOptions = new () { WriteIndented = true };

	public static BindingCompatibilityProfile Extract (IEnumerable<string> roots, string referenceDirectory)
	{
		using var resolver = new DefaultAssemblyResolver ();
		resolver.AddSearchDirectory (referenceDirectory);
		var parameters = new ReaderParameters { AssemblyResolver = resolver };
		var sources = roots.Select (path => AssemblyDefinition.ReadAssembly (path, parameters)).ToArray ();
		try {
			var queue = new Queue<TypeDefinition> ();
			var contracts = new Dictionary<string, BindingContractType> (StringComparer.Ordinal);
			var inputAssemblies = new Dictionary<string, BindingContractAssembly> (StringComparer.Ordinal);
			var rootEntries = new HashSet<BindingContractRoot> ();
			void Input (AssemblyDefinition assembly)
			{
				string path = assembly.MainModule.FileName;
				inputAssemblies [assembly.Name.FullName] = new (assembly.Name.FullName,
					Convert.ToHexString (SHA256.HashData (File.ReadAllBytes (path))).ToLowerInvariant ());
			}
			void Visit (TypeReference? reference, string? origin)
			{
				if (reference == null || reference is GenericParameter)
					return;
				if (reference is GenericInstanceType instance)
					foreach (var argument in instance.GenericArguments)
						Visit (argument, origin);
				if (reference is TypeSpecification specification) {
					Visit (specification.ElementType, origin);
					return;
				}
				string owner = Owner (reference);
				if (!IsBindingOwner (owner))
					return;
				var definition = reference.Resolve () ?? throw new InvalidDataException ($"Unresolved protected type {reference.FullName} in {owner}.");
				if (origin != null)
					rootEntries.Add (new (definition.FullName, definition.Module.Assembly.Name.FullName, origin));
				string key = Key (definition);
				if (contracts.ContainsKey (key))
					return;
				contracts.Add (key, Describe (definition));
				Input (definition.Module.Assembly);
				queue.Enqueue (definition);
			}
			foreach (var source in sources) {
				Input (source);
				foreach (var type in Flatten (source.MainModule.Types).Where (Visible))
					foreach (var reference in SurfaceReferences (type))
						Visit (reference, source.Name.Name + ":" + type.FullName);
			}
			while (queue.Count > 0) {
				var type = queue.Dequeue ();
				foreach (var reference in SurfaceReferences (type))
					Visit (reference, null);
				if (type.DeclaringType != null)
					Visit (type.DeclaringType, null);
				foreach (var nested in type.NestedTypes.Where (Visible))
					Visit (nested, null);
			}
			return new (1, inputAssemblies.Values.OrderBy (a => a.Identity, StringComparer.Ordinal).ToArray (),
				rootEntries.OrderBy (r => r.Owner, StringComparer.Ordinal).ThenBy (r => r.Type, StringComparer.Ordinal)
					.ThenBy (r => r.ExposedBy, StringComparer.Ordinal).ToArray (),
				contracts.Values.OrderBy (t => t.Owner, StringComparer.Ordinal).ThenBy (t => t.Name, StringComparer.Ordinal).ToArray ());
		} finally {
			foreach (var source in sources)
				source.Dispose ();
		}
	}

	public IReadOnlyList<string> Validate (AssemblyDefinition candidate)
	{
		if (SchemaVersion != 1)
			throw new InvalidDataException ($"Unsupported binding compatibility profile schema {SchemaVersion}.");
		var errors = new List<string> ();
		var actualTypes = Flatten (candidate.MainModule.Types).ToDictionary (t => t.FullName, StringComparer.Ordinal);
		foreach (var expected in Types.Where (t => AssemblyNameReference.Parse (t.Owner).Name == candidate.Name.Name)) {
			if (expected.Owner != candidate.Name.FullName)
				errors.Add ($"Owner changed for {expected.Name}: expected {expected.Owner}, got {candidate.Name.FullName}.");
			if (!actualTypes.TryGetValue (expected.Name, out var type)) {
				errors.Add ($"Missing protected type {expected.Name}.");
				continue;
			}
			var actual = Describe (type);
			if ((expected.Preservation != "identity" && expected.Flags != actual.Flags) ||
					expected.BaseType != actual.BaseType || expected.JniName != actual.JniName ||
					((int.Parse (expected.Flags) ^ int.Parse (actual.Flags)) & (int) (TypeAttributes.ClassSemanticMask | TypeAttributes.VisibilityMask)) != 0)
				errors.Add ($"Type shape/base/JNI identity changed: {expected.Name}.");
			if (!expected.Interfaces.All (actual.Interfaces.Contains) || !expected.Constraints.SequenceEqual (actual.Constraints))
				errors.Add ($"Interface or generic contract changed: {expected.Name}.");
			var actualMembers = actual.Members.ToDictionary (m => m.Kind + "|" + m.Signature, StringComparer.Ordinal);
			foreach (var member in expected.Members) {
				if (!actualMembers.TryGetValue (member.Kind + "|" + member.Signature, out var found)) {
					errors.Add ($"Missing protected {member.Kind}: {expected.Name}::{member.Signature}.");
				} else if (member.Flags != found.Flags || member.JniName != found.JniName || member.JniSignature != found.JniSignature) {
					errors.Add ($"Member flags/JNI contract changed: {expected.Name}::{member.Signature}.");
				}
			}
			var oldMethods = expected.Members.Where (m => m.Kind == "method").Select (m => m.Signature).ToHashSet (StringComparer.Ordinal);
			foreach (var method in type.Methods.Where (Visible).Where (_ => expected.Preservation != "identity"))
				if (!oldMethods.Contains (MethodSignature (method)) && method.IsAbstract)
					errors.Add ($"New abstract obligation breaks existing implementers: {expected.Name}::{method.Name}.");
		}
		foreach (var owner in Types.Select (t => t.Owner).Distinct (StringComparer.Ordinal))
			if (AssemblyNameReference.Parse (owner).Name == candidate.Name.Name && owner != candidate.Name.FullName)
				errors.Add ($"Assembly identity changed: {owner} -> {candidate.Name.FullName}.");
		if (!Types.Any (t => AssemblyNameReference.Parse (t.Owner).Name == candidate.Name.Name))
			errors.Add ($"Candidate {candidate.Name.Name} has no protected owner in this profile.");
		return errors;
	}

	public void Apply (XDocument api, string assemblyName)
	{
		if (SchemaVersion != 1)
			throw new InvalidDataException ($"Unsupported binding compatibility profile schema {SchemaVersion}.");
		string ownerName = AssemblyNameReference.Parse (assemblyName).Name;
		var contracts = Types.Where (t => AssemblyNameReference.Parse (t.Owner).Name == ownerName && t.JniName != null)
			.ToDictionary (t => t.JniName ?? throw new InvalidDataException ("Missing JNI contract name."), StringComparer.Ordinal);
		var protectedOwners = Types.Where (t => t.JniName != null)
			.GroupBy (t => t.JniName ?? "", StringComparer.Ordinal)
			.ToDictionary (g => g.Key, g => g.Select (t => AssemblyNameReference.Parse (t.Owner).Name).ToHashSet (StringComparer.Ordinal), StringComparer.Ordinal);
		var seen = new HashSet<string> (StringComparer.Ordinal);
		foreach (var package in api.Root?.Elements ("package") ?? []) {
			foreach (var type in package.Elements ().Where (e => e.Name == "class" || e.Name == "interface")) {
				string jniName = ((string?) type.Attribute ("jni-signature") ?? "").TrimStart ('L').TrimEnd (';');
				if (protectedOwners.TryGetValue (jniName, out var owners) && !owners.Contains (ownerName))
					throw new InvalidDataException ($"Protected owner cannot change: {jniName} belongs to {string.Join (", ", owners)}, not {ownerName}.");
				if (!contracts.TryGetValue (jniName, out var contract))
					continue;
				seen.Add (jniName);
				int nested = contract.Name.IndexOf ('/');
				string topName = nested < 0 ? contract.Name : contract.Name.Substring (0, nested);
				int separator = topName.LastIndexOf ('.');
				string ns = separator < 0 ? "" : topName.Substring (0, separator);
				string name = contract.Name.Substring (separator + 1).Replace ('/', '.');
				package.SetAttributeValue ("managedName", ns);
				type.SetAttributeValue ("managedName", name);
				foreach (var member in contract.Members.Where (m => m.JniName != null && m.Kind == "method" && m.JniName != ".ctor")) {
					var matches = type.Elements ("method").Where (m =>
						(string?) m.Attribute ("name") == member.JniName && (string?) m.Attribute ("jni-signature") == member.JniSignature).ToArray ();
					if (matches.Length != 1)
						throw new InvalidDataException ($"Protected JNI slot is missing or ambiguous: {contract.Name}::{member.JniName}{member.JniSignature}.");
					if (member.Name.StartsWith ("get_", StringComparison.Ordinal) || member.Name.StartsWith ("set_", StringComparison.Ordinal))
						matches [0].SetAttributeValue ("propertyName", member.Name.Substring (4));
					else {
						matches [0].SetAttributeValue ("managedName", member.Name);
						matches [0].SetAttributeValue ("propertyName", "");
					}
				}
			}
		}
		foreach (var missing in contracts.Keys.Where (k => !seen.Contains (k)))
			throw new InvalidDataException ($"Protected JNI type is missing: {missing}.");
	}

	public void RestoreOverrides (IEnumerable<GenBase> definitions, string assemblyName)
	{
		var contracts = Types.Where (t => AssemblyNameReference.Parse (t.Owner).Name == AssemblyNameReference.Parse (assemblyName).Name)
			.Where (t => t.JniName != null).ToDictionary (t => t.JniName ?? "", StringComparer.Ordinal);
		foreach (var type in FlattenGenerators (definitions)) {
			if (!contracts.TryGetValue (type.RawJniName, out var contract))
				continue;
			foreach (var method in type.GetAllMethods ()) {
				var member = contract.Members.FirstOrDefault (m => m.Kind == "method" && m.JniName == method.JavaName && m.JniSignature == method.JniSignature);
				if (member == null)
					continue;
				var flags = (MethodAttributes) int.Parse (member.Flags, System.Globalization.CultureInfo.InvariantCulture);
				method.Visibility = (flags & MethodAttributes.MemberAccessMask) switch {
					MethodAttributes.Public => "public",
					MethodAttributes.Family => "protected",
					MethodAttributes.FamORAssem => "protected internal",
					_ => throw new InvalidDataException ($"Unsupported protected method visibility: {member.Name}."),
				};
				method.IsVirtual = (flags & MethodAttributes.Virtual) != 0;
				method.IsOverride = method.IsVirtual && (flags & MethodAttributes.NewSlot) == 0;
				method.ManagedOverride = method.IsOverride ? "override" : method.IsVirtual ? "virtual" : "none";
			}
		}
	}

	public BindingContractType? ContractFor (GenBase type) => Types.FirstOrDefault (t => t.JniName == type.RawJniName && t.Name.Replace ('/', '.') == type.FullName);

	static IEnumerable<GenBase> FlattenGenerators (IEnumerable<GenBase> types)
	{
		foreach (var type in types) {
			yield return type;
			foreach (var nested in FlattenGenerators (type.NestedTypes))
				yield return nested;
		}
	}

	public static BindingContractType Describe (TypeDefinition type)
	{
		var members = new List<BindingContractMember> ();
		foreach (var method in type.Methods.Where (Visible)) {
			var registration = Registration (method);
			members.Add (new ("method", method.Name, MethodSignature (method),
				((int) (method.Attributes & (MethodAttributes.MemberAccessMask | MethodAttributes.Static | MethodAttributes.Final |
					MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Abstract | MethodAttributes.SpecialName))).ToString (),
				registration.Name, registration.Signature, method.Parameters.Select (p => p.ParameterType.FullName.Replace ('/', '.')).ToArray (),
				method.ReturnType.FullName.Replace ('/', '.')));
		}
		foreach (var field in type.Fields.Where (Visible)) {
			var registration = Registration (field);
			string value = field.HasConstant ? JsonSerializer.Serialize (field.Constant) : "";
			members.Add (new ("field", field.Name, field.Name + ":" + Signature (field.FieldType),
				((int) field.Attributes).ToString () + "|" + value, registration.Name, registration.Signature));
		}
		foreach (var property in type.Properties.Where (p => p.GetMethod != null && Visible (p.GetMethod) || p.SetMethod != null && Visible (p.SetMethod))) {
			var registration = Registration (property);
			members.Add (new ("property", property.Name, property.Name + ":" + Signature (property.PropertyType) +
				"(" + string.Join (",", property.Parameters.Select (p => Signature (p.ParameterType))) + ")",
				$"{property.GetMethod != null && Visible (property.GetMethod)}|{property.SetMethod != null && Visible (property.SetMethod)}", registration.Name, registration.Signature));
		}
		foreach (var evt in type.Events.Where (e => e.AddMethod != null && Visible (e.AddMethod) || e.RemoveMethod != null && Visible (e.RemoveMethod)))
			members.Add (new ("event", evt.Name, evt.Name + ":" + Signature (evt.EventType), "", null, null));
		return new (type.Module.Assembly.Name.FullName, type.FullName, Registration (type).Name,
			((int) (type.Attributes & (TypeAttributes.VisibilityMask | TypeAttributes.ClassSemanticMask | TypeAttributes.Abstract |
				TypeAttributes.Sealed | TypeAttributes.LayoutMask))).ToString (), type.BaseType == null ? null : Signature (type.BaseType),
			type.Interfaces.Select (i => Signature (i.InterfaceType)).OrderBy (s => s, StringComparer.Ordinal).ToArray (),
			Constraints (type.GenericParameters), members.OrderBy (m => m.Kind, StringComparer.Ordinal).ThenBy (m => m.Signature, StringComparer.Ordinal).ToArray ());
	}

	public static IEnumerable<TypeDefinition> Flatten (IEnumerable<TypeDefinition> types)
	{
		foreach (var type in types) {
			yield return type;
			foreach (var nested in Flatten (type.NestedTypes))
				yield return nested;
		}
	}

	public static (string? Name, string? Signature) Registration (ICustomAttributeProvider provider)
	{
		var attribute = provider.CustomAttributes.FirstOrDefault (a => a.AttributeType.FullName == "Android.Runtime.RegisterAttribute");
		return (attribute?.ConstructorArguments.FirstOrDefault ().Value as string,
			attribute?.ConstructorArguments.Skip (1).FirstOrDefault ().Value as string);
	}

	static IEnumerable<TypeReference> SurfaceReferences (TypeDefinition type)
	{
		if (type.BaseType != null)
			yield return type.BaseType;
		foreach (var iface in type.Interfaces)
			yield return iface.InterfaceType;
		foreach (var constraint in type.GenericParameters.SelectMany (p => p.Constraints))
			yield return constraint.ConstraintType;
		foreach (var method in type.Methods.Where (Visible)) {
			yield return method.ReturnType;
			foreach (var parameter in method.Parameters)
				yield return parameter.ParameterType;
			foreach (var constraint in method.GenericParameters.SelectMany (p => p.Constraints))
				yield return constraint.ConstraintType;
		}
		foreach (var field in type.Fields.Where (Visible))
			yield return field.FieldType;
	}

	public static bool Visible (TypeDefinition type) => type.DeclaringType == null ? type.IsPublic :
		(type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly) && Visible (type.DeclaringType);
	static bool Visible (MethodDefinition method) => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly;
	static bool Visible (FieldDefinition field) => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly;
	static bool IsBindingOwner (string owner) => owner.StartsWith ("Xamarin.AndroidX.", StringComparison.Ordinal) ||
		owner.StartsWith ("Xamarin.Google.Android.Material,", StringComparison.Ordinal);
	static string Key (TypeDefinition type) => type.Module.Assembly.Name.FullName + "|" + type.FullName;
	static string Owner (TypeReference type) => type.Scope is AssemblyNameReference assembly ? assembly.FullName :
		type.Scope is ModuleDefinition module ? module.Assembly.Name.FullName : type.Scope.Name;
	static string Signature (TypeReference type) => type switch {
		GenericParameter p => (p.Type == GenericParameterType.Method ? "!!" : "!") + p.Position,
		GenericInstanceType g => Signature (g.ElementType) + "<" + string.Join (",", g.GenericArguments.Select (Signature)) + ">",
		ArrayType a => Signature (a.ElementType) + "[" + new string (',', a.Rank - 1) + "]",
		ByReferenceType r => Signature (r.ElementType) + "&",
		PointerType p => Signature (p.ElementType) + "*",
		OptionalModifierType m => Signature (m.ElementType) + " modopt(" + Signature (m.ModifierType) + ")",
		RequiredModifierType m => Signature (m.ElementType) + " modreq(" + Signature (m.ModifierType) + ")",
		_ => "[" + SignatureOwner (type) + "]" + type.FullName,
	};
	static string SignatureOwner (TypeReference type)
	{
		string owner = Owner (type);
		string name = owner.Split (',') [0];
		return name == "System.Runtime" || name == "mscorlib" || name == "System.Private.CoreLib" ? "System.Runtime" :
			name == "Java.Interop" ? "Java.Interop" : owner;
	}
	static string [] Constraints (IEnumerable<GenericParameter> parameters) => parameters.Select (p =>
		p.Position + ":" + (int) p.Attributes + ":" + string.Join (",", p.Constraints.Select (c => Signature (c.ConstraintType)).OrderBy (s => s, StringComparer.Ordinal))).ToArray ();
	public static string DescribeMethod (MethodDefinition method) => MethodSignature (method);
	static string MethodSignature (MethodDefinition method) => method.Name + "`" + method.GenericParameters.Count + "(" +
		string.Join (",", method.Parameters.Select (p => Signature (p.ParameterType) + ":" + (int) p.Attributes +
			(p.HasConstant ? "=" + JsonSerializer.Serialize (p.Constant) : ""))) + "):" + Signature (method.ReturnType) +
		"|" + string.Join (";", Constraints (method.GenericParameters));

}
