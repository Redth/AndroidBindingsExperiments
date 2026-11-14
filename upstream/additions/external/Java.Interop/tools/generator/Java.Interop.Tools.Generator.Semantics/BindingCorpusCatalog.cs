#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xamarin.Android.Tools.Bytecode;

namespace Java.Interop.Tools.Generator.Semantics
{
	public sealed record BindingCatalogDefinition (
		string JavaType, string Coordinate, string NativeClassSha256, string Kind,
		string Classification, string Reason, string ContractSha256, string [] References);

	public sealed record BindingCanonicalOwner (string JavaType, string MavenGroup, string Assembly, string? MavenArtifact = null);
	public sealed record BindingReferencedOwner (string JavaType, string ManagedType, string Assembly);

	public sealed record BindingCatalogType (
		string JavaType, BindingCanonicalOwner? Owner, string Status, BindingCatalogDefinition [] Definitions);

	public sealed record BindingCatalogDependency (string Assembly, string [] References);

	public sealed record BindingCatalogPlan (
		BindingCanonicalOwner [] Types, BindingCanonicalOwner [] OwnerRegistry, BindingCatalogType [] Contracts, BindingCatalogDependency [] Dependencies,
		string [] RequiredInfrastructure, string [] RequiredInfrastructureConflicts, string [] PublicContractConflicts,
		string [] OwnerConflicts);

	public static class BindingCorpusCatalog
	{
		public static BindingCatalogDefinition [] Classify (XDocument facts, string coordinate,
			IReadOnlyDictionary<string, string> nativeHashes)
		{
			if (facts.Root?.Name != "api")
				throw new InvalidDataException ("Corpus classification requires an <api> document.");
			var policy = new BindingAccessibilityProjection (facts);
			var decisions = policy.Decisions.ToDictionary (d => (d.DeclaringType, d.NativeSlot));
			var result = new List<BindingCatalogDefinition> ();
			foreach (var package in facts.Root.Elements ("package")) {
				foreach (var type in package.Elements ().Where (t => t.Name == "class" || t.Name == "interface")) {
					var java = BindingTypeDefinition.GetJavaName (package, type);
					var descriptor = Required (type, "jni-signature");
					var binary = descriptor.Substring (1, descriptor.Length - 2);
					var decision = decisions [(java, "")];
					var contractMembers = type.Elements ().Where (m => m.Name == "method" || m.Name == "constructor" || m.Name == "field")
						.Where (m => decisions [(java, Slot (m))].ManagedVisibility is "public" or "protected")
						.OrderBy (Slot, StringComparer.Ordinal).ToArray ();
					object Shape (XElement element) => new {
						Kind = element.Name.LocalName, Descriptor = (string?) element.Attribute ("jni-signature"),
						Name = (string?) element.Attribute ("name"),
						Visibility = decisions [(java, Slot (element))].ManagedVisibility,
						Static = (string?) element.Attribute ("static"), Abstract = (string?) element.Attribute ("abstract"),
						Final = (string?) element.Attribute ("final"),
						Generic = (string?) element.Attribute ("type-generic-aware") ?? (string?) element.Attribute ("return") ?? (string?) element.Attribute ("type"),
						Parameters = element.Elements ("parameter").Select (p => (string?) p.Attribute ("type")).ToArray (),
						Bounds = element.Elements ("typeParameters").Select (p => p.ToString (SaveOptions.DisableFormatting)).ToArray (),
					};
					var shape = JsonSerializer.Serialize (new {
						Kind = type.Name.LocalName, Base = (string?) type.Attribute ("extends-generic-aware") ?? (string?) type.Attribute ("extends"),
						Final = (string?) type.Attribute ("final"), Abstract = (string?) type.Attribute ("abstract"),
						Interfaces = type.Elements ("implements").Select (i => (string?) i.Attribute ("name-generic-aware") ?? (string?) i.Attribute ("name"))
							.OrderBy (n => n, StringComparer.Ordinal).ToArray (),
						Bounds = type.Elements ("typeParameters").Select (p => p.ToString (SaveOptions.DisableFormatting)).ToArray (),
						Members = contractMembers.Select (Shape).ToArray (),
					});
					var references = type.Attributes ().Concat (type.Elements ("implements").Attributes ())
						.Concat (type.Elements ("typeParameters").DescendantsAndSelf ().Attributes ())
						.Concat (contractMembers.SelectMany (m => m.DescendantsAndSelf ().Attributes ()))
						.Where (a => a.Name.LocalName is "jni-signature" or "extends" or "extends-generic-aware" or "name-generic-aware" or
							"type" or "type-generic-aware" or "return" or "classBound" or "interfaceBounds" ||
							a.Name.LocalName == "name" && a.Parent?.Name == "implements")
						.SelectMany (a => Regex.Matches (a.Value, @"L([\w/.$]+)[;<]").Select (m => m.Groups [1].Value)
							.Concat (Regex.Matches (a.Value, @"[\w$]+(?:\.[\w$]+)+").Select (m => m.Value.Replace ('.', '/'))))
						.Where (n => n != binary).Distinct (StringComparer.Ordinal).OrderBy (n => n, StringComparer.Ordinal).ToArray ();
					if (!nativeHashes.TryGetValue (binary, out var hash))
						throw new InvalidDataException ($"No immutable class hash for {coordinate}: {binary}.");
					result.Add (new BindingCatalogDefinition (binary, coordinate, hash, type.Name.LocalName,
						decision.Classification, decision.Reason, Hash (Encoding.UTF8.GetBytes (shape)), references));
				}
			}
			return result.ToArray ();
		}

		public static BindingCatalogPlan Plan (IEnumerable<BindingCatalogDefinition> definitions,
			IEnumerable<BindingCanonicalOwner>? previous = null, bool perArtifact = false)
		{
			var pinned = (previous ?? []).ToDictionary (o => o.JavaType, StringComparer.Ordinal);
			if (pinned.Values.Any (o => perArtifact != (o.MavenArtifact != null)))
				throw new InvalidDataException ("The prior owner registry uses a different assembly layout. Seed a new registry for the intentional group/artifact migration.");
			var contracts = new List<BindingCatalogType> ();
			var owners = new List<BindingCanonicalOwner> ();
			var conflicts = new List<string> ();
			var ownershipConflicts = new List<string> ();
			var byName = definitions.GroupBy (d => d.JavaType, StringComparer.Ordinal)
				.ToDictionary (g => g.Key, g => g.OrderBy (d => d.Coordinate, StringComparer.Ordinal).ToArray (), StringComparer.Ordinal);
			var aliases = byName.Keys.GroupBy (n => n.Replace ('/', '.').Replace ('$', '.'), StringComparer.Ordinal)
				.ToDictionary (g => g.Key, g => g.ToArray (), StringComparer.Ordinal);
			foreach (var pair in byName.OrderBy (p => p.Key, StringComparer.Ordinal)) {
				var visible = pair.Value.Where (d => IsPublic (d.Classification)).ToArray ();
				BindingCanonicalOwner? owner = null;
				if (visible.Length > 0) {
					if (!pinned.TryGetValue (pair.Key, out owner)) {
						var groups = visible.Select (d => d.Coordinate.Split (':') [0]).Distinct (StringComparer.Ordinal).ToArray ();
						var matching = groups.Where (g => pair.Key.StartsWith (g.Replace ('.', '/') + "/", StringComparison.Ordinal))
							.OrderByDescending (g => g.Length).ToArray ();
						var group = groups.Length == 1 ? groups [0] : matching.FirstOrDefault ();
						if (group != null) {
							var modules = visible.Where (d => d.Coordinate.Split (':') [0] == group)
								.Select (d => d.Coordinate.Split (':') [1]).Distinct (StringComparer.Ordinal).ToArray ();
							if (!perArtifact || modules.Length == 1)
								owner = new BindingCanonicalOwner (pair.Key, group,
									perArtifact ? ArtifactAssemblyName (group, modules [0]) : AssemblyName (group),
									perArtifact ? modules [0] : null);
						}
					}
					if (owner == null)
						ownershipConflicts.Add (pair.Key);
					else
						owners.Add (owner);
				}
				var different = visible.Select (d => d.ContractSha256).Distinct (StringComparer.Ordinal).Count () > 1;
				if (different)
					conflicts.Add (pair.Key);
				contracts.Add (new BindingCatalogType (pair.Key, owner,
					visible.Length == 0 ? pair.Value.Any (d => d.Classification == "Infrastructure") ? "infrastructure-only" : "excluded" :
					owner == null ? "owner-review-required" : different ? "native-contract-selection-required" :
					visible.Length > 1 ? "shared-public-contract" : "single-public-contract", pair.Value));
			}
			var assemblies = owners.GroupBy (o => o.Assembly, StringComparer.Ordinal);
			foreach (var assembly in assemblies)
				if (assembly.Select (o => (o.MavenGroup, o.MavenArtifact)).Distinct ().Count () > 1)
					throw new InvalidDataException ($"Canonical assembly-name collision: {assembly.Key}.");
			var byOwner = owners.ToDictionary (o => o.JavaType, StringComparer.Ordinal);
			var hidden = new HashSet<string> (StringComparer.Ordinal);
			var graph = owners.Select (o => o.Assembly).Distinct ().ToDictionary (a => a, _ => new HashSet<string> (StringComparer.Ordinal));
			foreach (var type in owners) {
				var visited = new HashSet<string> (StringComparer.Ordinal);
				var pending = new Stack<string> (byName [type.JavaType].Where (d => IsPublic (d.Classification)).SelectMany (d => d.References));
				while (pending.Count > 0) {
					var name = pending.Pop ();
					if (!byName.ContainsKey (name) && aliases.TryGetValue (name.Replace ('/', '.'), out var candidates)) {
						if (candidates.Length != 1)
							throw new InvalidDataException ($"Ambiguous source type reference {name}.");
						name = candidates [0];
					}
					if (!visited.Add (name) || !byName.TryGetValue (name, out var declarations))
						continue;
					if (byOwner.TryGetValue (name, out var target)) {
						if (target.Assembly != type.Assembly)
							graph [type.Assembly].Add (target.Assembly);
						continue;
					}
					hidden.Add (name);
					foreach (var reference in declarations.SelectMany (d => d.References))
						pending.Push (reference);
				}
			}
			var registry = pinned.Values.Concat (owners).GroupBy (o => o.JavaType, StringComparer.Ordinal)
				.Select (g => g.First ()).OrderBy (o => o.JavaType, StringComparer.Ordinal).ToArray ();
			return new BindingCatalogPlan (owners.ToArray (), registry, contracts.ToArray (),
				graph.OrderBy (p => p.Key, StringComparer.Ordinal).Select (p => new BindingCatalogDependency (p.Key, p.Value.OrderBy (a => a, StringComparer.Ordinal).ToArray ())).ToArray (),
				hidden.OrderBy (n => n, StringComparer.Ordinal).ToArray (),
				hidden.Where (n => byName [n].Select (d => d.ContractSha256).Distinct ().Count () > 1).OrderBy (n => n, StringComparer.Ordinal).ToArray (),
				conflicts.ToArray (), ownershipConflicts.ToArray ());
		}

		public static void Write (string nativeManifest, string descriptorDirectory, string output, string? previousOwners = null, bool perArtifact = false)
		{
			if (previousOwners != null && Path.GetFullPath (previousOwners) == Path.GetFullPath (output))
				throw new InvalidOperationException ("Write a new catalog snapshot; the established owner registry must not be overwritten in place.");
			File.Delete (output);
			using var manifest = JsonDocument.Parse (File.ReadAllText (nativeManifest));
			using var index = JsonDocument.Parse (File.ReadAllText (Path.Combine (descriptorDirectory, "index.json")));
			if (index.RootElement.GetProperty ("schemaVersion").GetInt32 () != 1 ||
				index.RootElement.GetProperty ("status").GetString () != "complete")
				throw new InvalidDataException ("Incomplete descriptor index.");
			var descriptors = index.RootElement.GetProperty ("artifacts").EnumerateArray ().ToDictionary (
				a => Text (a, "coordinate") + ":" + Text (a, "archiveSha256"), StringComparer.Ordinal);
			var expected = manifest.RootElement.EnumerateArray ().Select (a => Coordinate (a) + ":" + Text (a, "sha256")).ToHashSet (StringComparer.Ordinal);
			if (!expected.SetEquals (descriptors.Keys))
				throw new InvalidDataException ("Descriptor index does not exactly cover the native input manifest.");
			var definitions = new List<BindingCatalogDefinition> ();
			var artifacts = new List<object> ();
			foreach (var artifact in manifest.RootElement.EnumerateArray ().OrderBy (Coordinate, StringComparer.Ordinal)) {
				var coordinate = Coordinate (artifact);
				var hash = Text (artifact, "sha256");
				var archive = Text (artifact, "file");
				if (FileHash (archive) != hash)
					throw new InvalidDataException ($"Native input changed after resolution: {coordinate}.");
				var descriptor = descriptors [coordinate + ":" + hash];
				var filename = Text (descriptor, "descriptor");
				if (filename != Path.GetFileName (filename))
					throw new InvalidDataException ("Descriptor paths must be local file names.");
				var path = Path.Combine (descriptorDirectory, filename);
				if (FileHash (path) != Text (descriptor, "descriptorSha256"))
					throw new InvalidDataException ($"Descriptor changed after extraction: {coordinate}.");
				var classes = new ClassPath { ApiSource = "class-parse" };
				using (var zip = ZipFile.OpenRead (archive)) {
					if (Path.GetExtension (archive) == ".jar")
						LoadClasses (zip, classes);
					else if (Path.GetExtension (archive) == ".aar") {
						foreach (var entry in zip.Entries.Where (e => e.FullName == "classes.jar" ||
							e.FullName.StartsWith ("libs/", StringComparison.Ordinal) && e.FullName.EndsWith (".jar", StringComparison.Ordinal))) {
							using var stream = entry.Open ();
							using var jar = new ZipArchive (stream, ZipArchiveMode.Read);
							LoadClasses (jar, classes);
						}
					} else
						throw new InvalidDataException ($"Unsupported native archive {archive}.");
				}
				using var data = JsonDocument.Parse (File.ReadAllText (path));
				var origins = data.RootElement.GetProperty ("inputs").EnumerateArray ().ToArray ();
				if (origins.Length != 1 || Text (origins [0], "coordinate") != coordinate || Text (origins [0], "sha256") != hash)
					throw new InvalidDataException ($"The official descriptor belongs to a different native artifact: {coordinate}.");
				var hashes = data.RootElement.GetProperty ("classes").EnumerateArray ().ToDictionary (
					c => Text (c, "binaryName"), c => Text (c.GetProperty ("origin"), "classSha256"), StringComparer.Ordinal);
				using var official = new JvmApiDescriptor (path);
				var facts = official.Apply (new XDocument (classes.ToXElement ()));
				var classified = Classify (facts, coordinate, hashes);
				var unclassified = hashes.Keys.Except (classified.Select (d => d.JavaType), StringComparer.Ordinal)
					.Where (name => !name.EndsWith ("/package-info", StringComparison.Ordinal)).ToArray ();
				if (unclassified.Length > 0)
					throw new InvalidDataException ($"Unclassified native definitions in {coordinate}: {string.Join (", ", unclassified)}");
				definitions.AddRange (classified);
				artifacts.Add (new { Coordinate = coordinate, ArchiveSha256 = hash, DescriptorSha256 = Text (descriptor, "descriptorSha256"),
					NativeClasses = hashes.Count, ClassifiedTypes = classified.Length, PackageMetadataClasses = hashes.Count - classified.Length });
				Console.WriteLine ($"Classified {classified.Length} types: {coordinate}");
			}
			BindingCanonicalOwner [] previous = [];
			if (previousOwners != null) {
				using var old = JsonDocument.Parse (File.ReadAllText (previousOwners));
				var registry = old.RootElement.TryGetProperty ("OwnerRegistry", out var established) ? established : old.RootElement.GetProperty ("Types");
				previous = JsonSerializer.Deserialize<BindingCanonicalOwner []> (registry.GetRawText ()) ??
					throw new InvalidDataException ("Missing prior canonical owners.");
			}
			var plan = Plan (definitions, previous, perArtifact);
			Directory.CreateDirectory (Path.GetDirectoryName (Path.GetFullPath (output)) ?? ".");
			using var result = File.Create (output);
			JsonSerializer.Serialize (result, new {
				SchemaVersion = 1, Status = "audited-not-generated",
				AssemblyGranularity = perArtifact ? "artifact" : "group",
				Policy = "One canonical managed owner per public Java type; conflicting native contracts require selection, never duplicate projections.",
				NativeManifestSha256 = FileHash (nativeManifest), DescriptorIndexSha256 = FileHash (Path.Combine (descriptorDirectory, "index.json")),
				Artifacts = artifacts, plan.Types, plan.OwnerRegistry, plan.Contracts, plan.Dependencies, plan.RequiredInfrastructure,
				plan.RequiredInfrastructureConflicts, plan.PublicContractConflicts, plan.OwnerConflicts,
			}, new JsonSerializerOptions { WriteIndented = true });
		}

		static void LoadClasses (ZipArchive archive, ClassPath classes)
		{
			foreach (var entry in archive.Entries.Where (e => e.FullName.EndsWith (".class", StringComparison.Ordinal) &&
				!e.FullName.StartsWith ("META-INF/", StringComparison.Ordinal) && e.FullName != "module-info.class")) {
				using var stream = entry.Open ();
				classes.Add (new ClassFile (stream));
			}
		}

		static bool IsPublic (string classification) => classification is "Public" or "Protected" or "Unverified";
		static string Coordinate (JsonElement artifact) => Text (artifact, "group") + ":" + Text (artifact, "artifact") + ":" + Text (artifact, "version");
		static string Text (JsonElement element, string name) => element.GetProperty (name).GetString () ?? throw new InvalidDataException ($"Missing {name}.");
		static string Required (XElement element, string name) => (string?) element.Attribute (name) ?? throw new InvalidDataException ($"Missing {name}.");
		static string Slot (XElement element) => (element.Name == "constructor" ? "<init>" : Required (element, "name")) +
			(element.Name == "field" ? ":" : "") + Required (element, "jni-signature");
		static string Hash (byte [] bytes) => Convert.ToHexString (SHA256.HashData (bytes)).ToLowerInvariant ();
		static string FileHash (string path)
		{
			using var stream = File.OpenRead (path);
			return Convert.ToHexString (SHA256.HashData (stream)).ToLowerInvariant ();
		}
		public static string AssemblyName (string group) =>
			"Android.Maven." + string.Join (".", group.Split ('.').Select (p => char.ToUpperInvariant (p [0]) + p.Substring (1).Replace ('-', '_')));
		public static string ArtifactAssemblyName (string group, string artifact)
		{
			if (new [] { group, artifact }.Any (value => string.IsNullOrEmpty (value) ||
				value.Split ('.').Any (p => p.Length == 0) || value.Any (c => !char.IsLetterOrDigit (c) && c is not '.' and not '-' and not '_')))
				throw new InvalidDataException ("Artifact assembly owners require valid Maven group and artifact identifiers.");
			return AssemblyName (group) + ".Artifacts." + string.Join (".", artifact.Split ('.').Select (
				p => char.ToUpperInvariant (p [0]) + p.Substring (1).Replace ('-', '_')));
		}
	}
}
