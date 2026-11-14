#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Java.Interop.Tools.Generator.Semantics
{
	public static class BindingOwnershipPlanner
	{
		public static void WriteReport (string output, string apiFile, string nativeManifest,
			IEnumerable<(string JavaType, string ManagedType, string Assembly)> types)
		{
			using var manifest = JsonDocument.Parse (File.ReadAllText (nativeManifest));
			var provenance = new Dictionary<string, string> (StringComparer.Ordinal);
			foreach (var artifact in manifest.RootElement.EnumerateArray ()) {
				var group = artifact.GetProperty ("group").GetString () ?? throw new InvalidDataException ("Missing Maven group.");
				var file = artifact.GetProperty ("file").GetString () ?? throw new InvalidDataException ("Missing native archive.");
				using var archive = ZipFile.OpenRead (file);
				if (Path.GetExtension (file) == ".aar") {
					foreach (var jar in archive.Entries.Where (e => e.FullName == "classes.jar" ||
						(e.FullName.StartsWith ("libs/", StringComparison.Ordinal) && e.FullName.EndsWith (".jar", StringComparison.Ordinal)))) {
						using var stream = jar.Open ();
						using var nested = new ZipArchive (stream, ZipArchiveMode.Read);
						AddClasses (nested, group, provenance);
					}
				} else {
					AddClasses (archive, group, provenance);
				}
			}
			var projected = types.OrderBy (t => t.JavaType, StringComparer.Ordinal).ToArray ();
			var groups = projected.ToDictionary (t => t.JavaType,
				t => provenance.TryGetValue (t.JavaType, out var group) ? group : "local.project", StringComparer.Ordinal);
			var graph = groups.Values.Distinct (StringComparer.Ordinal).ToDictionary (
				g => g, _ => new HashSet<string> (StringComparer.Ordinal), StringComparer.Ordinal);
			var api = XDocument.Load (apiFile);
			var nativeTypes = api.Descendants ().Where (e => e.Name == "class" || e.Name == "interface").ToArray ();
			var javaNames = nativeTypes.ToDictionary (
				t => (((string?) t.Parent?.Attribute ("name") ?? "") + "." + (string?) t.Attribute ("name")).TrimStart ('.'),
				t => {
					var descriptor = (string?) t.Attribute ("jni-signature") ?? throw new InvalidDataException ("A native type has no JVM descriptor.");
					return descriptor.Substring (1, descriptor.Length - 2);
				}, StringComparer.Ordinal);
			foreach (var type in nativeTypes) {
				var descriptor = (string?) type.Attribute ("jni-signature");
				if (descriptor == null || !groups.TryGetValue (descriptor.Substring (1, descriptor.Length - 2), out var sourceGroup))
					continue;
				var attributes = type.Attributes ().Concat (type.Elements ().Where (e =>
					e.Name == "implements" || e.Name == "typeParameters" ||
					(string?) e.Attribute ("visibility") is "public" or "protected")
					.SelectMany (e => e.DescendantsAndSelf ().Attributes ()));
				foreach (var attribute in attributes) {
					var references = Regex.Matches (attribute.Value, @"L([\w/.$]+)[;<]")
						.Select (m => m.Groups [1].Value).Concat (
						Regex.Matches (attribute.Value, @"[\w$]+(?:\.[\w$]+)+").Select (m => javaNames.TryGetValue (m.Value, out var name) ? name : ""));
					foreach (var reference in references)
						if (groups.TryGetValue (reference, out var targetGroup) && targetGroup != sourceGroup)
							graph [sourceGroup].Add (targetGroup);
				}
			}
			var components = CollapseCycles (graph);
			File.WriteAllText (output, JsonSerializer.Serialize (new {
				SchemaVersion = 1,
				OwnerPolicy = "Explicit project owners override Maven-group defaults; public-contract cycles are indivisible default partitions.",
				Partitions = components.Select (c => new { MavenGroups = c, DefaultOwner = c [0] }),
				Types = projected.Select (t => new { t.JavaType, t.ManagedType, t.Assembly, MavenGroup = groups [t.JavaType] }),
			}, new JsonSerializerOptions { WriteIndented = true }));
		}

		static void AddClasses (ZipArchive archive, string group, IDictionary<string, string> provenance)
		{
			foreach (var entry in archive.Entries.Where (e => e.FullName.EndsWith (".class", StringComparison.Ordinal) &&
				!e.FullName.StartsWith ("META-INF/", StringComparison.Ordinal) && e.FullName != "module-info.class")) {
				var name = entry.FullName.Substring (0, entry.FullName.Length - ".class".Length);
				if (provenance.TryGetValue (name, out var previous) && previous != group)
					throw new InvalidDataException ($"Conflicting Maven groups '{previous}' and '{group}' both own JVM type '{name}'.");
				provenance [name] = group;
			}
		}

		public static IReadOnlyList<string []> CollapseCycles (IReadOnlyDictionary<string, HashSet<string>> graph)
		{
			var indices = new Dictionary<string, int> (StringComparer.Ordinal);
			var low = new Dictionary<string, int> (StringComparer.Ordinal);
			var stack = new Stack<string> ();
			var active = new HashSet<string> (StringComparer.Ordinal);
			var result = new List<string []> ();
			void Visit (string group)
			{
				indices [group] = low [group] = indices.Count;
				stack.Push (group);
				active.Add (group);
				foreach (var target in graph [group].OrderBy (v => v, StringComparer.Ordinal)) {
					if (!graph.ContainsKey (target))
						throw new InvalidDataException ($"Unknown Maven contract group '{target}'.");
					if (!indices.ContainsKey (target)) {
						Visit (target);
						low [group] = Math.Min (low [group], low [target]);
					} else if (active.Contains (target)) {
						low [group] = Math.Min (low [group], indices [target]);
					}
				}
				if (low [group] != indices [group])
					return;
				var component = new List<string> ();
				string item;
				do {
					item = stack.Pop ();
					active.Remove (item);
					component.Add (item);
				} while (item != group);
				result.Add (component.OrderBy (v => v, StringComparer.Ordinal).ToArray ());
			}
			foreach (var group in graph.Keys.OrderBy (v => v, StringComparer.Ordinal))
				if (!indices.ContainsKey (group))
					Visit (group);
			return result;
		}
	}
}
