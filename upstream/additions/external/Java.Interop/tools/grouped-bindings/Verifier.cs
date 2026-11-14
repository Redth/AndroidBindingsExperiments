#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mono.Cecil;
using Java.Interop.Tools.Generator.Semantics;

public static class AssemblyGroupVerifier
{
	public static void Verify (string directory)
	{
		directory = Path.GetFullPath (directory);
		File.Delete (Path.Combine (directory, "verification.json"));
		using var manifest = JsonDocument.Parse (File.ReadAllText (Path.Combine (directory, "ownership.json")));
		var referencedOwners = manifest.RootElement.TryGetProperty ("ReferencedOwners", out var referenced)
			? JsonSerializer.Deserialize<BindingReferencedOwner[]> (referenced.GetRawText ()) ??
				throw new InvalidDataException ("Invalid referenced owners.")
			: [];
		var externalPeers = referencedOwners.ToDictionary (o => o.JavaType, StringComparer.Ordinal);
		var externalTypes = referencedOwners.ToDictionary (o => o.ManagedType, StringComparer.Ordinal);
		var expected = manifest.RootElement.GetProperty ("Types").EnumerateArray ().ToDictionary (
			t => t.GetProperty ("ManagedType").GetString () ?? throw new InvalidDataException ("Missing managed type."),
			t => t.GetProperty ("Assembly").GetString () ?? throw new InvalidDataException ("Missing managed owner."), StringComparer.Ordinal);
		var names = manifest.RootElement.GetProperty ("Assemblies").EnumerateArray ().Select (a =>
			a.GetProperty ("Assembly").GetString () ?? throw new InvalidDataException ("Missing assembly name.")).ToHashSet (StringComparer.Ordinal);
		var actual = Directory.EnumerateFiles (directory, "*.dll").Select (Path.GetFileNameWithoutExtension)
			.Where (name => name != "Microsoft.Android.Kotlin").ToHashSet (StringComparer.Ordinal);
		if (!actual.SetEquals (names))
			throw new InvalidDataException ("The implementation directory contains missing or unexpected Maven-group DLLs.");
		using var resolver = new DefaultAssemblyResolver ();
		resolver.AddSearchDirectory (directory);
		var seen = new Dictionary<string, string> (StringComparer.Ordinal);
		var resolvedTypes = 0;
		var resolvedMembers = 0;
		var resources = new List<string> ();
		var modules = new HashSet<Guid> ();
		var publicPeers = new Dictionary<string, string> (StringComparer.Ordinal);
		foreach (var name in names.OrderBy (n => n, StringComparer.Ordinal)) {
			using var assembly = AssemblyDefinition.ReadAssembly (Path.Combine (directory, name + ".dll"),
				new ReaderParameters { AssemblyResolver = resolver });
			if (assembly.Name.Name != name)
				throw new InvalidDataException ($"Assembly identity does not match {name}.");
			if (assembly.CustomAttributes.Any (a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
				throw new InvalidDataException ($"Reference-only DLL cannot be delivered as an implementation: {name}.");
			if (!modules.Add (assembly.MainModule.Mvid))
				throw new InvalidDataException ($"Reused module version identity in {name}.");
			foreach (var resource in assembly.MainModule.Resources)
				resources.Add (name + ":" + resource.Name);
			foreach (var type in Flatten (assembly.MainModule.Types).Where (t => t.Name != "<Module>")) {
				if (!seen.TryAdd (type.FullName, name))
					throw new InvalidDataException ($"Duplicate CLR owner for {type.FullName}.");
				var java = type.CustomAttributes.FirstOrDefault (a => a.AttributeType.FullName == "Android.Runtime.RegisterAttribute")
					?.ConstructorArguments.FirstOrDefault ().Value as string;
				if (externalTypes.ContainsKey (type.FullName) || java != null && externalPeers.ContainsKey (java))
					throw new InvalidDataException ($"Referenced canonical type was regenerated: {type.FullName}.");
				if (java != null && !java.StartsWith ("mono/", StringComparison.Ordinal) && IsVisible (type) &&
					type.BaseType?.FullName != "System.Attribute" && !publicPeers.TryAdd (java, type.FullName + ", " + name))
					throw new InvalidDataException ($"Multiple public projections of Java type {java}: {publicPeers [java]} and {type.FullName}, {name}.");
				if (!expected.TryGetValue (type.FullName, out var owner) || owner != name)
					throw new InvalidDataException ($"Unexpected CLR owner for {type.FullName}: {name}.");
			}
			foreach (var reference in assembly.MainModule.GetTypeReferences ()) {
				if (externalTypes.TryGetValue (reference.FullName, out var external) && Scope (reference) != external.Assembly)
					throw new InvalidDataException ($"Incorrect referenced canonical owner for {reference.FullName} in {name}.");
				if (expected.TryGetValue (reference.FullName, out var owner) && Scope (reference) != owner)
					throw new InvalidDataException ($"Incorrect scope for owned type {reference.FullName} in {name}.");
				if (Scope (reference) is string scope && names.Contains (scope)) {
					var target = reference.Resolve () ?? throw new InvalidDataException ($"Cannot resolve {reference.FullName} from {name}.");
					if (target.Module.Assembly.Name.Name != scope)
						throw new InvalidDataException ($"Incorrect resolved identity for {reference.FullName}.");
					resolvedTypes++;
				}
			}
			foreach (var reference in assembly.MainModule.GetMemberReferences ()) {
				if (Scope (reference.DeclaringType) is not string scope || !names.Contains (scope))
					continue;
				if (reference is MethodReference method) {
					if (method.Resolve () == null)
						throw new InvalidDataException ($"Cannot resolve method {method.FullName} from {name}.");
				} else if (reference is FieldReference field) {
					if (field.Resolve () == null)
						throw new InvalidDataException ($"Cannot resolve field {field.FullName} from {name}.");
				}
				resolvedMembers++;
			}
		}
		if (seen.Count != expected.Count)
			throw new InvalidDataException ($"Expected {expected.Count} managed types, found {seen.Count}.");
		File.WriteAllText (Path.Combine (directory, "verification.json"), JsonSerializer.Serialize (new {
			Assemblies = names.Count, Types = seen.Count, OwnedTypeReferencesResolved = resolvedTypes,
			OwnedMemberReferencesResolved = resolvedMembers, DuplicateOwners = 0, Resources = resources,
			PublicJavaPeerOwners = publicPeers.Count, DuplicatePublicJavaPeers = 0,
			ReferencedCanonicalOwners = referencedOwners.Length,
		}, new JsonSerializerOptions { WriteIndented = true }));
		Console.WriteLine ($"Verified {names.Count} assemblies, {seen.Count} type owners, {resolvedTypes} type references and {resolvedMembers} member references.");
	}

	internal static bool IsVisible (TypeDefinition type) =>
		(type.IsPublic || type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamilyOrAssembly) &&
		(type.DeclaringType == null || IsVisible (type.DeclaringType));

	static string? Scope (TypeReference reference) => reference.GetElementType ().Scope switch {
		AssemblyNameReference assembly => assembly.Name,
		ModuleDefinition module => module.Assembly.Name.Name,
		_ => null,
	};

	static IEnumerable<TypeDefinition> Flatten (IEnumerable<TypeDefinition> types)
	{
		foreach (var type in types) {
			yield return type;
			foreach (var nested in Flatten (type.NestedTypes))
				yield return nested;
		}
	}
}
