#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mono.Cecil;

public static class ImplementationCatalog
{
	public static void Write (string directory, string contracts)
	{
		using var manifest = JsonDocument.Parse (File.ReadAllText (contracts));
		var bootstrap = manifest.RootElement.TryGetProperty ("BootstrapAssembly", out var identity) ? identity.GetString () :
			Path.GetFileNameWithoutExtension (manifest.RootElement.GetProperty ("Bootstrap").GetString ());
		if (string.IsNullOrEmpty (bootstrap))
			throw new InvalidDataException ("No bootstrap identity.");
		var groups = manifest.RootElement.GetProperty ("Assemblies").EnumerateArray ().ToDictionary (
			a => a.GetProperty ("Assembly").GetString () ?? throw new InvalidDataException ("No contract assembly."),
			a => a.GetProperty ("MavenGroup").GetString () ?? throw new InvalidDataException ("No Maven group."), StringComparer.Ordinal);
		var artifacts = manifest.RootElement.GetProperty ("Assemblies").EnumerateArray ().ToDictionary (
			a => a.GetProperty ("Assembly").GetString () ?? throw new InvalidDataException ("No contract assembly."),
			a => a.TryGetProperty ("MavenArtifact", out var artifact) ? artifact.GetString () : null, StringComparer.Ordinal);
		using var resolver = new DefaultAssemblyResolver ();
		resolver.AddSearchDirectory (directory);
		resolver.AddSearchDirectory (Path.GetDirectoryName (contracts));
		var assemblies = new List<AssemblyDefinition> ();
		try {
			foreach (var name in groups.Keys) {
				var assembly = AssemblyDefinition.ReadAssembly (Path.Combine (directory, name + ".dll"),
					new ReaderParameters { ReadingMode = ReadingMode.Immediate, AssemblyResolver = resolver });
				assemblies.Add (assembly);
				if (assembly.CustomAttributes.Any (a => a.AttributeType.FullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute"))
					throw new InvalidDataException ($"Not an implementation assembly: {name}.");
				foreach (var type in assembly.MainModule.Types.Where (t => t.Name.StartsWith ("<PrivateImplementationDetails>", StringComparison.Ordinal)))
					type.Name += "_" + Convert.ToHexString (SHA256.HashData (Encoding.UTF8.GetBytes (name))).Substring (0, 12);
			}
			var owners = assemblies.SelectMany (a => Flatten (a.MainModule.Types).Where (t => t.Name != "<Module>")
				.Select (t => (Type: t.FullName, Assembly: a.Name.Name))).ToDictionary (t => t.Type, t => t.Assembly, StringComparer.Ordinal);
			foreach (var contract in manifest.RootElement.GetProperty ("Types").EnumerateArray ().Where (t =>
				t.GetProperty ("JavaType").ValueKind == JsonValueKind.String)) {
				var name = contract.GetProperty ("ManagedType").GetString () ?? throw new InvalidDataException ("No contract type.");
				if (!owners.TryGetValue (name, out var owner) || owner != contract.GetProperty ("Assembly").GetString ())
					throw new InvalidDataException ($"A registered native contract was lost or reassigned: {name}.");
			}
			object TypeRow (TypeDefinition type, string name) => new {
				ManagedType = type.FullName, MavenGroup = groups [name], Assembly = name,
				MavenArtifact = artifacts [name],
				Public = AssemblyGroupVerifier.IsVisible (type),
				JavaType = type.CustomAttributes.FirstOrDefault (a => a.AttributeType.FullName == "Android.Runtime.RegisterAttribute")
					?.ConstructorArguments.FirstOrDefault ().Value as string,
			};
			foreach (var assembly in assemblies) {
				foreach (var type in Flatten (assembly.MainModule.Types))
					foreach (var provider in new ICustomAttributeProvider [] { type }.Concat (type.Methods).Concat (type.Properties)) {
						foreach (var attribute in provider.CustomAttributes) {
							for (var index = 0; index < attribute.ConstructorArguments.Count; index++) {
								var argument = attribute.ConstructorArguments [index];
								if (argument.Value is not string text || !text.Contains (", " + bootstrap, StringComparison.Ordinal))
									continue;
								var end = text.IndexOf (", " + bootstrap, StringComparison.Ordinal);
								var prefix = text.Substring (0, end);
								var managed = prefix.Substring (prefix.LastIndexOf (':') + 1).Replace ('+', '/');
								if (!owners.TryGetValue (managed, out var owner))
									throw new InvalidDataException ($"Unknown JNI connector type: {text}.");
								attribute.ConstructorArguments [index] = new CustomAttributeArgument (argument.Type,
									text.Replace (", " + bootstrap, ", " + owner, StringComparison.Ordinal));
							}
						}
					}
				if (assembly.MainModule.AssemblyReferences.Any (r => r.Name == bootstrap))
					throw new InvalidDataException ($"Stale bootstrap dependency in {assembly.Name.Name}.");
				var path = Path.Combine (directory, assembly.Name.Name + ".dll");
				assembly.Write (path + ".writing", new WriterParameters { DeterministicMvid = true });
				File.Move (path + ".writing", path, overwrite: true);
			}
			File.WriteAllText (Path.Combine (directory, "ownership.json"), JsonSerializer.Serialize (new {
				SchemaVersion = 1, Bootstrap = contracts,
				AssemblyGranularity = manifest.RootElement.TryGetProperty ("AssemblyGranularity", out var granularity) ? granularity.GetString () : "group",
				Policy = "Source implementations compiled against canonical cyclic reference contracts; no reference-only DLLs delivered.",
				Assemblies = assemblies.Select (a => new {
					MavenGroup = groups [a.Name.Name], Assembly = a.Name.Name, ModuleVersionId = a.MainModule.Mvid,
					MavenArtifact = artifacts [a.Name.Name],
					Types = Flatten (a.MainModule.Types).Count (t => t.Name != "<Module>"),
					References = a.MainModule.AssemblyReferences.Select (r => r.Name).OrderBy (n => n).ToArray (),
				}).ToArray (),
				Types = assemblies.SelectMany (a => Flatten (a.MainModule.Types).Where (t => t.Name != "<Module>")
					.Select (t => TypeRow (t, a.Name.Name))).ToArray (),
				ReferencedOwners = manifest.RootElement.TryGetProperty ("ReferencedOwners", out var referenced) ? referenced :
					JsonSerializer.SerializeToElement<object[]> ([]),
				OwnerRegistry = manifest.RootElement.TryGetProperty ("OwnerRegistry", out var registry) ? registry :
					JsonSerializer.SerializeToElement<object[]> ([]),
			}, new JsonSerializerOptions { WriteIndented = true }));
		} finally {
			foreach (var assembly in assemblies)
				assembly.Dispose ();
		}
	}

	static IEnumerable<TypeDefinition> Flatten (IEnumerable<TypeDefinition> types)
	{
		foreach (var type in types) {
			yield return type;
			foreach (var nested in Flatten (type.NestedTypes))
				yield return nested;
		}
	}
}
