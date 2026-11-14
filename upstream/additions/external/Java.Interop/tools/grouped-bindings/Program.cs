#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Java.Interop.Tools.Generator.Semantics;

if (args.Count (a => a == "--artifacts") > 1)
	throw new ArgumentException ("Assembly layout was specified more than once.");
var perArtifact = args.Contains ("--artifacts");
args = args.Where (a => a != "--artifacts").ToArray ();

if ((args.Length == 4 || args.Length == 5) && args [0] == "--catalog") {
	BindingCorpusCatalog.Write (args [1], args [2], args [3], args.Length == 5 ? args [4] : null, perArtifact);
	return;
}
if (args.Length >= 5 && args [0] == "--contracts") {
	string? owners = null, referenced = null;
	for (var index = 5; index < args.Length; index += 2) {
		if (index + 1 == args.Length)
			throw new ArgumentException ("Missing contract option value.");
		switch (args [index]) {
		case "--owners": owners = args [index + 1]; break;
		case "--referenced-owners": referenced = args [index + 1]; break;
		default: throw new ArgumentException ($"Unknown contract option: {args [index]}.");
		}
	}
	new AssemblyGrouper (args [1], args [2], args [4], args [3], owners, referenced, perArtifact).Emit ();
	return;
}
if (args.Length == 3 && args [0] == "--finalize") {
	ImplementationCatalog.Write (args [1], args [2]);
	AssemblyGroupVerifier.Verify (args [1]);
	return;
}
if (args.Length == 2 && args [0] == "--verify") {
	AssemblyGroupVerifier.Verify (args [1]);
	return;
}
if (args.Length < 3 || (args.Length - 3) % 2 != 0)
	throw new ArgumentException ("Usage: grouped-bindings [--artifacts] Bootstrap.dll native-inputs.json output-directory [--owners registry.json] [--referenced-owners references.json]");
string? implementationOwners = null, implementationReferences = null;
for (var index = 3; index < args.Length; index += 2)
	switch (args [index]) {
	case "--owners": implementationOwners = args [index + 1]; break;
	case "--referenced-owners": implementationReferences = args [index + 1]; break;
	default: throw new ArgumentException ($"Unknown implementation option: {args [index]}.");
	}
new AssemblyGrouper (args [0], args [1], args [2], canonicalOwnerFile: implementationOwners,
	referencedOwnerFile: implementationReferences, perArtifact: perArtifact).Emit ();

public sealed class AssemblyGrouper
{
	readonly string input;
	readonly string output;
	readonly Dictionary<string, string> javaGroups = new (StringComparer.Ordinal);
	readonly Dictionary<string, (string Group, string? Artifact)> nativeOwners = new (StringComparer.Ordinal);
	readonly bool perArtifact;
	readonly Dictionary<string, TypeDefinition> definitions = new (StringComparer.Ordinal);
	readonly Dictionary<TypeDefinition, string> owners = new ();
	readonly Dictionary<string, AssemblyDefinition> assemblies = new (StringComparer.Ordinal);
	readonly List<object> typeReport = new ();
	readonly Dictionary<string, string> sourceFiles = new (StringComparer.Ordinal);
	readonly string? sourceDirectory;
	readonly Dictionary<string, BindingCanonicalOwner>? canonicalOwners;
	readonly BindingReferencedOwner[] referencedOwners = [];
	readonly Dictionary<string, string> canonicalAssemblies = new (StringComparer.Ordinal);
	AssemblyDefinition? source;
	AssemblyDefinition Source => source ?? throw new InvalidOperationException ("The bootstrap assembly is not loaded.");
	string sourceName = "";
	const string shared = "__managed_support__";

	public AssemblyGrouper (string input, string manifest, string output, string? sourceDirectory = null, string? canonicalOwnerFile = null,
		string? referencedOwnerFile = null, bool perArtifact = false)
	{
		this.input = Path.GetFullPath (input);
		this.output = Path.GetFullPath (output);
		this.sourceDirectory = sourceDirectory;
		this.perArtifact = perArtifact;
		if (referencedOwnerFile != null) {
			using var referenced = JsonDocument.Parse (File.ReadAllText (referencedOwnerFile));
			if (referenced.RootElement.GetProperty ("SchemaVersion").GetInt32 () != 1)
				throw new InvalidDataException ("Unsupported referenced ownership schema.");
			referencedOwners = JsonSerializer.Deserialize<BindingReferencedOwner[]> (referenced.RootElement.GetProperty ("Types").GetRawText ()) ??
				throw new InvalidDataException ("No referenced owners.");
		}
		if (canonicalOwnerFile != null) {
			using var canonical = JsonDocument.Parse (File.ReadAllText (canonicalOwnerFile));
			if (canonical.RootElement.GetProperty ("SchemaVersion").GetInt32 () != 1)
				throw new InvalidDataException ("Unsupported canonical ownership schema.");
			var registry = canonical.RootElement.TryGetProperty ("OwnerRegistry", out var established) ? established : canonical.RootElement.GetProperty ("Types");
			var values = JsonSerializer.Deserialize<BindingCanonicalOwner[]> (registry.GetRawText ()) ??
				throw new InvalidDataException ("No canonical owners.");
			canonicalOwners = values.ToDictionary (o => o.JavaType, StringComparer.Ordinal);
			if (values.Any (o => perArtifact != (o.MavenArtifact != null)))
				throw new InvalidDataException ("The canonical owners use a different group/artifact layout.");
			foreach (var group in values.GroupBy (o => Key (o.MavenGroup, o.MavenArtifact), StringComparer.Ordinal)) {
				var first = group.First ();
				nativeOwners.Add (group.Key, (first.MavenGroup, first.MavenArtifact));
				var names = group.Select (o => o.Assembly).Distinct ().ToArray ();
				if (names.Length != 1 || string.IsNullOrEmpty (names [0]) || names [0] != Path.GetFileName (names [0]) ||
					names [0].IndexOfAny (['/', '\\', ',']) >= 0)
					throw new InvalidDataException ($"Invalid or inconsistent canonical assembly for {group.Key}.");
				canonicalAssemblies.Add (group.Key, names [0]);
			}
		}
		using var document = JsonDocument.Parse (File.ReadAllText (manifest));
		foreach (var artifact in document.RootElement.EnumerateArray ()) {
			var group = artifact.GetProperty ("group").GetString () ?? throw new InvalidDataException ("Missing Maven group.");
			var module = perArtifact ? artifact.GetProperty ("artifact").GetString () ??
				throw new InvalidDataException ("Missing Maven artifact.") : null;
			var key = Key (group, module);
			nativeOwners [key] = (group, module);
			var file = artifact.GetProperty ("file").GetString () ?? throw new InvalidDataException ("Missing native file.");
			using var archive = ZipFile.OpenRead (file);
			if (Path.GetExtension (file) == ".jar")
				AddClasses (archive, key);
			else
				foreach (var entry in archive.Entries.Where (e => e.FullName == "classes.jar" ||
					e.FullName.StartsWith ("libs/", StringComparison.Ordinal) && e.FullName.EndsWith (".jar", StringComparison.Ordinal))) {
					using var stream = entry.Open ();
					using var jar = new ZipArchive (stream, ZipArchiveMode.Read);
					AddClasses (jar, key);
				}
		}
	}

	void AddClasses (ZipArchive archive, string group)
	{
		foreach (var entry in archive.Entries.Where (e => e.FullName.EndsWith (".class", StringComparison.Ordinal) &&
			!e.FullName.StartsWith ("META-INF/", StringComparison.Ordinal) && e.FullName != "module-info.class")) {
			var name = entry.FullName.Substring (0, entry.FullName.Length - 6);
			if (javaGroups.TryGetValue (name, out var previous) && previous != group)
				throw new InvalidDataException ($"Conflicting owners for {name}: {previous}, {group}.");
			if ((canonicalOwners != null || perArtifact) && javaGroups.ContainsKey (name))
				throw new InvalidDataException ($"The canonical native graph contains duplicate definitions of {name}.");
			javaGroups [name] = group;
		}
	}

	public void Emit ()
	{
		Directory.CreateDirectory (output);
		File.Delete (Path.Combine (output, "ownership.json"));
		File.Delete (Path.Combine (output, "verification.json"));
		using var resolver = new GroupResolver ();
		resolver.AddSearchDirectory (Path.GetDirectoryName (input));
		source = AssemblyDefinition.ReadAssembly (input, new ReaderParameters {
			AssemblyResolver = resolver, ReadSymbols = File.Exists (Path.ChangeExtension (input, ".pdb")),
			ReadingMode = ReadingMode.Immediate,
		});
		sourceName = Source.Name.Name;
		var roots = Source.MainModule.Types.Where (t => t.Name != "<Module>").ToArray ();
		foreach (var type in Flatten (roots))
			definitions.Add (type.FullName, type);
		foreach (var type in definitions.Values) {
			foreach (var method in type.Methods) {
				if (method.HasBody) {
					_ = method.Body.Instructions.Count;
					_ = method.Body.Variables.Count;
					_ = method.Body.ExceptionHandlers.Count;
				}
				_ = method.Overrides.Count;
			}
			foreach (var field in type.Fields) {
				_ = field.Constant;
				_ = field.InitialValue;
			}
		}
		foreach (var root in roots) {
			var group = NativeGroup (root) ?? PeerGroup (root) ?? DocumentGroup (root) ?? shared;
			foreach (var type in Flatten (new [] { root }))
				owners.Add (type, group);
		}
		if (sourceDirectory != null)
			PlanSources ();
		if (canonicalOwners != null)
			foreach (var type in definitions.Values.Where (AssemblyGroupVerifier.IsVisible)) {
				var java = Registered (type);
				if (java == null || java.StartsWith ("mono/", StringComparison.Ordinal))
					continue;
				if (!canonicalOwners.TryGetValue (java, out var owner))
					throw new InvalidDataException ($"Public Java type has no canonical owner: {java}.");
				if (AssemblyName (owners [type]) != owner.Assembly)
					throw new InvalidDataException ($"Nested or helper ownership disagrees with the canonical owner of {java}.");
			}
		foreach (var group in owners.Values.Distinct ().OrderBy (v => v, StringComparer.Ordinal)) {
			var assembly = AssemblyDefinition.CreateAssembly (
				new AssemblyNameDefinition (AssemblyName (group), Source.Name.Version), AssemblyName (group) + ".dll",
				new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver });
			assembly.MainModule.RuntimeVersion = Source.MainModule.RuntimeVersion;
			assembly.MainModule.Architecture = Source.MainModule.Architecture;
			foreach (var reference in Source.MainModule.AssemblyReferences.Where (r => r.Name != sourceName))
				assembly.MainModule.AssemblyReferences.Add (Clone (reference));
			assemblies.Add (group, assembly);
		}
		if (assemblies.Values.Select (a => a.Name.Name).Distinct (StringComparer.OrdinalIgnoreCase).Count () != assemblies.Count)
			throw new InvalidDataException ("Maven groups collide after assembly name allocation.");
		resolver.Template = Source;
		resolver.GroupNames.UnionWith (assemblies.Values.Select (a => a.Name.Name));
		var assemblyReport = new List<object> ();
		foreach (var pair in assemblies) {
			using var copy = AssemblyDefinition.ReadAssembly (input, new ReaderParameters {
				AssemblyResolver = resolver, ReadingMode = sourceDirectory == null ? ReadingMode.Immediate : ReadingMode.Deferred,
			});
			var module = copy.MainModule;
			copy.Name.Name = pair.Value.Name.Name;
			module.Name = pair.Value.Name.Name + ".dll";
			var retained = Flatten (module.Types.Where (t => t.Name != "<Module>" &&
				owners [definitions [t.FullName]] == pair.Key)).ToArray ();
			foreach (var type in retained)
				foreach (var method in type.Methods) {
					_ = method.Overrides.Count;
					if (method.HasBody) {
						_ = method.Body.Instructions.Count;
						_ = method.Body.Variables.Count;
						_ = method.Body.ExceptionHandlers.Count;
					}
				}
			var relink = new Relinker (this, module);
			var attributes = copy.CustomAttributes.Select (relink.Attribute).ToArray ();
			copy.CustomAttributes.Clear ();
			foreach (var attribute in attributes)
				copy.CustomAttributes.Add (attribute);
			var moduleAttributes = module.CustomAttributes.Select (relink.Attribute).ToArray ();
			module.CustomAttributes.Clear ();
			foreach (var attribute in moduleAttributes)
				module.CustomAttributes.Add (attribute);
			foreach (var type in retained) {
				relink.Rewrite (type);
				typeReport.Add (new { ManagedType = type.FullName, MavenGroup = Group (pair.Key), MavenArtifact = Artifact (pair.Key),
					Assembly = copy.Name.Name, JavaType = Registered (type),
					Public = AssemblyGroupVerifier.IsVisible (type) });
			}
			for (var index = module.Types.Count - 1; index >= 0; index--) {
				var root = module.Types [index];
				if (root.Name != "<Module>" && owners [definitions [root.FullName]] != pair.Key)
					module.Types.RemoveAt (index);
			}
			foreach (var friend in assemblies.Values.Where (a => a.Name.Name != copy.Name.Name)) {
				var type = new TypeReference ("System.Runtime.CompilerServices", "InternalsVisibleToAttribute",
					module, module.TypeSystem.CoreLibrary);
				var ctor = new MethodReference (".ctor", module.TypeSystem.Void, type) { HasThis = true };
				ctor.Parameters.Add (new ParameterDefinition (module.TypeSystem.String));
				var attribute = new CustomAttribute (ctor);
				attribute.ConstructorArguments.Add (new CustomAttributeArgument (module.TypeSystem.String, friend.Name.Name));
				copy.CustomAttributes.Add (attribute);
			}
			if (pair.Key != shared)
				module.Resources.Clear ();
			if (module.AssemblyReferences.Any (r => r.Name == sourceName))
				throw new InvalidDataException ($"Stale bootstrap reference in {copy.Name.Name}.");
			copy.Write (Path.Combine (output, copy.Name.Name + ".dll"), new WriterParameters { WriteSymbols = false, DeterministicMvid = true });
			assemblyReport.Add (new { MavenGroup = Group (pair.Key), MavenArtifact = Artifact (pair.Key), Assembly = copy.Name.Name,
				Version = copy.Name.Version.ToString (), ModuleVersionId = module.Mvid,
				Types = Flatten (module.Types).Count (t => t.Name != "<Module>"),
				SourceFiles = sourceFiles.Where (p => p.Value == pair.Key).Select (p => p.Key).OrderBy (p => p, StringComparer.Ordinal).ToArray (),
				References = module.AssemblyReferences.Select (r => r.Name).OrderBy (n => n).ToArray () });
			Console.WriteLine ($"Emitted {copy.Name.Name}.");
		}
		var manifest = new {
			SchemaVersion = 1, Bootstrap = input, BootstrapAssembly = sourceName,
			AssemblyGranularity = perArtifact ? "artifact" : "group",
			Policy = "One canonical native peer owner per selected Maven container. Ordinary managed-only helpers use BindingSupport. CLR cycles are retained, not collapsed.",
			Assemblies = assemblyReport,
			Types = typeReport,
			ReferencedOwners = referencedOwners,
			OwnerRegistry = (canonicalOwners?.Values ?? Enumerable.Empty<BindingCanonicalOwner> ()).Concat (
				definitions.Values.Where (AssemblyGroupVerifier.IsVisible).Where (t => t.BaseType?.FullName != "System.Attribute")
					.Select (t => (Type: t, Java: Registered (t))).Where (p => p.Java != null &&
						!p.Java.StartsWith ("mono/", StringComparison.Ordinal)).Select (p => {
							var key = owners [p.Type];
							return new BindingCanonicalOwner (p.Java ?? throw new InvalidDataException ("No Java owner."),
								Group (key), AssemblyName (key), Artifact (key));
						})).GroupBy (o => o.JavaType, StringComparer.Ordinal).Select (g => g.First ())
				.OrderBy (o => o.JavaType, StringComparer.Ordinal).ToArray (),
		};
		File.WriteAllText (Path.Combine (output, "ownership.json"), JsonSerializer.Serialize (manifest, new JsonSerializerOptions { WriteIndented = true }));
		Console.WriteLine ($"Emitted {assemblies.Count} assemblies containing {owners.Count} types.");
		foreach (var assembly in assemblies.Values)
			assembly.Dispose ();
		Source.Dispose ();
	}

	void PlanSources ()
	{
		if (sourceDirectory == null)
			throw new InvalidOperationException ("No generated source directory.");
		foreach (var file in Directory.EnumerateFiles (sourceDirectory, "*.cs").OrderBy (p => p, StringComparer.Ordinal)) {
			var syntax = CSharpSyntaxTree.ParseText (File.ReadAllText (file)).GetRoot ();
			var declared = new List<TypeDefinition> ();
			foreach (var node in syntax.DescendantNodes ().OfType<MemberDeclarationSyntax> ().Where (n =>
				n.Parent is BaseNamespaceDeclarationSyntax || n.Parent is CompilationUnitSyntax)) {
				var name = node switch {
					BaseTypeDeclarationSyntax type => type.Identifier.ValueText +
						(type is TypeDeclarationSyntax { TypeParameterList: { } parameters } ? "`" + parameters.Parameters.Count : ""),
					DelegateDeclarationSyntax type => type.Identifier.ValueText +
						(type.TypeParameterList is { } parameters ? "`" + parameters.Parameters.Count : ""),
					_ => null,
				};
				if (name == null)
					continue;
				var ns = string.Join (".", node.Ancestors ().OfType<BaseNamespaceDeclarationSyntax> ().Reverse ().Select (n => n.Name.ToString ()));
				if (definitions.TryGetValue (ns.Length == 0 ? name : ns + "." + name, out var definition))
					declared.Add (definition);
			}
			var groups = declared.Select (type => NativeGroup (type) ?? PeerGroup (type)).Where (g => g != null).Distinct ().ToArray ();
			if (groups.Length > 1)
				throw new InvalidDataException ($"Generated source spans native owners: {file}.");
			var group = groups.FirstOrDefault () ?? shared;
			sourceFiles.Add (Path.GetFullPath (file), group);
			foreach (var type in Flatten (declared))
				owners [type] = group;
		}
	}

	string? NativeGroup (TypeDefinition type)
	{
		var name = Registered (type);
		if (name == null)
			return null;
		if (name.StartsWith ("mono/", StringComparison.Ordinal))
			return null;
		if (canonicalOwners?.TryGetValue (name, out var canonical) == true)
			return Key (canonical.MavenGroup, canonical.MavenArtifact);
		return javaGroups.TryGetValue (name, out var group) ? group :
			throw new InvalidDataException ($"No Maven owner for registered type {type.FullName}: {name}.");
	}

	string? PeerGroup (TypeDefinition type)
	{
		var peer = type.Properties.FirstOrDefault (p => p.Name == "Peer")?.PropertyType;
		if (peer != null && definitions.TryGetValue (peer.GetElementType ().FullName, out var canonical))
			return NativeGroup (canonical);
		var interfaceGroups = type.Interfaces.Select (i => i.InterfaceType.GetElementType ().FullName)
			.Where (definitions.ContainsKey).Select (name => NativeGroup (definitions [name]))
			.Where (group => group != null).Distinct ().ToArray ();
		if (interfaceGroups.Length == 1)
			return interfaceGroups [0];
		foreach (var method in type.Methods.Where (m => m.IsStatic && m.Name == "Box"))
			if (definitions.TryGetValue (method.ReturnType.GetElementType ().FullName, out var boxed))
				return NativeGroup (boxed);
		return null;
	}

	string? DocumentGroup (TypeDefinition type)
	{
		foreach (var point in type.Methods.SelectMany (m => m.DebugInformation.SequencePoints)) {
			var name = Path.GetFileNameWithoutExtension (point.Document.Url);
			if (definitions.TryGetValue (name, out var canonical)) {
				var group = NativeGroup (canonical);
				if (group != null)
					return group;
			}
		}
		return null;
	}

	string AssemblyName (string group) => group == shared ? "Android.Maven.BindingSupport" :
		canonicalAssemblies.TryGetValue (group, out var name) ? name : perArtifact ?
			BindingCorpusCatalog.ArtifactAssemblyName (nativeOwners [group].Group,
				nativeOwners [group].Artifact ?? throw new InvalidDataException ("No artifact owner.")) :
			BindingCorpusCatalog.AssemblyName (group);

	static string Key (string group, string? artifact) => artifact == null ? group : group + ":" + artifact;
	string Group (string key) => key == shared ? shared : nativeOwners [key].Group;
	string? Artifact (string key) => key == shared ? null : nativeOwners [key].Artifact;

	static string? Registered (TypeDefinition type) => type.CustomAttributes.FirstOrDefault (a =>
		a.AttributeType.FullName == "Android.Runtime.RegisterAttribute")?.ConstructorArguments.FirstOrDefault ().Value as string;

	static IEnumerable<TypeDefinition> Flatten (IEnumerable<TypeDefinition> types)
	{
		foreach (var type in types) {
			yield return type;
			foreach (var nested in Flatten (type.NestedTypes))
				yield return nested;
		}
	}

	static AssemblyNameReference Clone (AssemblyNameReference value) => new (value.Name, value.Version) {
		Culture = value.Culture, PublicKeyToken = value.PublicKeyToken, Attributes = value.Attributes,
	};

	sealed class GroupResolver : DefaultAssemblyResolver
	{
		public AssemblyDefinition? Template { get; set; }
		public HashSet<string> GroupNames { get; } = new (StringComparer.Ordinal);
		public override AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters) =>
			GroupNames.Contains (name.Name) ? Template ?? throw new InvalidOperationException ("No template.") : base.Resolve (name, parameters);
	}

	sealed class Relinker
	{
		readonly AssemblyGrouper owner;
		readonly ModuleDefinition module;
		readonly Dictionary<TypeReference, TypeReference> types = new ();
		readonly Dictionary<MethodReference, MethodReference> methods = new ();
		readonly Dictionary<FieldReference, FieldReference> fields = new ();

		public Relinker (AssemblyGrouper owner, ModuleDefinition module)
		{
			this.owner = owner;
			this.module = module;
		}

		bool Owned (TypeReference value) => value is TypeDefinition && owner.definitions.ContainsKey (value.FullName) || value.Scope == module ||
			value.Scope?.Name == owner.sourceName || value.Scope?.Name == owner.Source.MainModule.Name;

		AssemblyNameReference Scope (AssemblyNameReference assembly)
		{
			var existing = module.AssemblyReferences.FirstOrDefault (r => r.FullName == assembly.FullName);
			if (existing != null)
				return existing;
			var result = Clone (assembly);
			module.AssemblyReferences.Add (result);
			return result;
		}

		public TypeReference Type (TypeReference value)
		{
			if (value is GenericParameter)
				return value;
			var primitive = value.MetadataType switch {
				MetadataType.Void => module.TypeSystem.Void,
				MetadataType.Boolean => module.TypeSystem.Boolean,
				MetadataType.Char => module.TypeSystem.Char,
				MetadataType.SByte => module.TypeSystem.SByte,
				MetadataType.Byte => module.TypeSystem.Byte,
				MetadataType.Int16 => module.TypeSystem.Int16,
				MetadataType.UInt16 => module.TypeSystem.UInt16,
				MetadataType.Int32 => module.TypeSystem.Int32,
				MetadataType.UInt32 => module.TypeSystem.UInt32,
				MetadataType.Int64 => module.TypeSystem.Int64,
				MetadataType.UInt64 => module.TypeSystem.UInt64,
				MetadataType.Single => module.TypeSystem.Single,
				MetadataType.Double => module.TypeSystem.Double,
				MetadataType.String => module.TypeSystem.String,
				MetadataType.Object => module.TypeSystem.Object,
				MetadataType.IntPtr => module.TypeSystem.IntPtr,
				MetadataType.UIntPtr => module.TypeSystem.UIntPtr,
				MetadataType.TypedByReference => module.TypeSystem.TypedReference,
				_ => null,
			};
			if (primitive != null)
				return primitive;
			if (types.TryGetValue (value, out var cached))
				return cached;
			TypeReference result;
			switch (value) {
			case GenericInstanceType generic:
				var instance = new GenericInstanceType (Type (generic.ElementType));
				types.Add (value, instance);
				foreach (var argument in generic.GenericArguments)
					instance.GenericArguments.Add (Type (argument));
				return instance;
			case ArrayType array:
				var converted = new ArrayType (Type (array.ElementType), array.Rank);
				for (var i = 0; i < array.Dimensions.Count; i++)
					converted.Dimensions [i] = new ArrayDimension (array.Dimensions [i].LowerBound, array.Dimensions [i].UpperBound);
				result = converted;
				break;
			case ByReferenceType reference:
				result = new ByReferenceType (Type (reference.ElementType));
				break;
			case PointerType pointer:
				result = new PointerType (Type (pointer.ElementType));
				break;
			case OptionalModifierType modifier:
				result = new OptionalModifierType (Type (modifier.ModifierType), Type (modifier.ElementType));
				break;
			case RequiredModifierType modifier:
				result = new RequiredModifierType (Type (modifier.ModifierType), Type (modifier.ElementType));
				break;
			case PinnedType pinned:
				result = new PinnedType (Type (pinned.ElementType));
				break;
			case SentinelType sentinel:
				result = new SentinelType (Type (sentinel.ElementType));
				break;
			case FunctionPointerType function:
				var signature = new FunctionPointerType { CallingConvention = function.CallingConvention,
					HasThis = function.HasThis, ExplicitThis = function.ExplicitThis, ReturnType = Type (function.ReturnType) };
				foreach (var parameter in function.Parameters)
					signature.Parameters.Add (new ParameterDefinition (Type (parameter.ParameterType)));
				result = signature;
				break;
			default:
				if (Owned (value)) {
					if (!owner.definitions.TryGetValue (value.FullName, out var definition))
						throw new InvalidDataException ($"Unresolved owned type {value.FullName}.");
					var target = owner.assemblies [owner.owners [definition]];
					if (target.Name.Name == module.Assembly.Name.Name) {
						result = module.GetType (value.FullName) ?? throw new InvalidDataException ($"Missing local type {value.FullName}.");
						break;
					}
					result = new TypeReference (value.Namespace, value.Name, module, Scope (target.Name), value.IsValueType);
				} else {
					var scope = value.Scope as AssemblyNameReference;
					result = new TypeReference (value.Namespace, value.Name, module,
						scope == null ? module.TypeSystem.CoreLibrary : Scope (scope), value.IsValueType);
				}
				if (value.DeclaringType != null)
					result.DeclaringType = Type (value.DeclaringType);
				if (result is not TypeDefinition)
					foreach (var parameter in value.GenericParameters)
						result.GenericParameters.Add (new GenericParameter (parameter.Name, result));
				break;
			}
			types [value] = result;
			return result;
		}

		MethodReference Method (MethodReference value)
		{
			if (methods.TryGetValue (value, out var cached))
				return cached;
			if (value is GenericInstanceMethod generic) {
				var instance = new GenericInstanceMethod (Method (generic.ElementMethod));
				foreach (var argument in generic.GenericArguments)
					instance.GenericArguments.Add (Type (argument));
				methods.Add (value, instance);
				return instance;
			}
			if (value is MethodDefinition definition && definition.Module == module &&
				owner.assemblies [owner.owners [owner.definitions [definition.DeclaringType.FullName]]].Name.Name == module.Assembly.Name.Name)
				return definition;
			var result = new MethodReference (value.Name, Type (value.ReturnType), Type (value.DeclaringType)) {
				HasThis = value.HasThis, ExplicitThis = value.ExplicitThis, CallingConvention = value.CallingConvention,
			};
			methods.Add (value, result);
			foreach (var parameter in value.GenericParameters)
				result.GenericParameters.Add (new GenericParameter (parameter.Name, result));
			foreach (var parameter in value.Parameters)
				result.Parameters.Add (new ParameterDefinition (Type (parameter.ParameterType)));
			return result;
		}

		FieldReference Field (FieldReference value)
		{
			if (value is FieldDefinition definition && definition.Module == module &&
				owner.assemblies [owner.owners [owner.definitions [definition.DeclaringType.FullName]]].Name.Name == module.Assembly.Name.Name)
				return definition;
			if (fields.TryGetValue (value, out var cached))
				return cached;
			var result = new FieldReference (value.Name, Type (value.FieldType), Type (value.DeclaringType));
			fields.Add (value, result);
			return result;
		}

		string Text (string value)
		{
			if (!value.Contains (", " + owner.sourceName, StringComparison.Ordinal))
				return value;
			var name = module.Assembly.Name.Name;
			var prefix = value.Substring (0, value.IndexOf (", " + owner.sourceName, StringComparison.Ordinal));
			var typeName = prefix.Substring (prefix.LastIndexOf (':') + 1).Replace ('+', '/');
			if (owner.definitions.TryGetValue (typeName, out var type))
				name = owner.assemblies [owner.owners [type]].Name.Name;
			return value.Replace (", " + owner.sourceName, ", " + name, StringComparison.Ordinal);
		}

		CustomAttributeArgument Argument (CustomAttributeArgument value)
		{
			object? content = value.Value;
			if (content is TypeReference type)
				content = Type (type);
			else if (content is CustomAttributeArgument[] array)
				content = array.Select (Argument).ToArray ();
			else if (content is CustomAttributeArgument nested)
				content = Argument (nested);
			else if (content is string text)
				content = Text (text);
			return new CustomAttributeArgument (Type (value.Type), content);
		}

		public CustomAttribute Attribute (CustomAttribute value)
		{
			var result = new CustomAttribute (Method (value.Constructor));
			foreach (var argument in value.ConstructorArguments)
				result.ConstructorArguments.Add (Argument (argument));
			foreach (var property in value.Properties)
				result.Properties.Add (new CustomAttributeNamedArgument (property.Name, Argument (property.Argument)));
			foreach (var field in value.Fields)
				result.Fields.Add (new CustomAttributeNamedArgument (field.Name, Argument (field.Argument)));
			return result;
		}

		void Attributes (ICustomAttributeProvider provider)
		{
			var rewritten = provider.CustomAttributes.Select (Attribute).ToArray ();
			provider.CustomAttributes.Clear ();
			foreach (var attribute in rewritten)
				provider.CustomAttributes.Add (attribute);
		}

		void Generics (IGenericParameterProvider provider)
		{
			foreach (var parameter in provider.GenericParameters) {
				Attributes (parameter);
				foreach (var constraint in parameter.Constraints) {
					constraint.ConstraintType = Type (constraint.ConstraintType);
					Attributes (constraint);
				}
			}
		}

		public void Rewrite (TypeDefinition type)
		{
			if (type.BaseType != null)
				type.BaseType = Type (type.BaseType);
			Attributes (type);
			Generics (type);
			foreach (var contract in type.Interfaces) {
				contract.InterfaceType = Type (contract.InterfaceType);
				Attributes (contract);
			}
			foreach (var field in type.Fields) {
				field.FieldType = Type (field.FieldType);
				Attributes (field);
			}
			foreach (var property in type.Properties) {
				property.PropertyType = Type (property.PropertyType);
				Attributes (property);
				foreach (var parameter in property.Parameters)
					parameter.ParameterType = Type (parameter.ParameterType);
			}
			foreach (var @event in type.Events) {
				@event.EventType = Type (@event.EventType);
				Attributes (@event);
			}
			foreach (var method in type.Methods) {
				method.ReturnType = Type (method.ReturnType);
				Attributes (method);
				Attributes (method.MethodReturnType);
				Generics (method);
				foreach (var parameter in method.Parameters) {
					parameter.ParameterType = Type (parameter.ParameterType);
					Attributes (parameter);
				}
				for (var i = 0; i < method.Overrides.Count; i++)
					method.Overrides [i] = Method (method.Overrides [i]);
				if (!method.HasBody)
					continue;
				foreach (var variable in method.Body.Variables)
					variable.VariableType = Type (variable.VariableType);
				foreach (var handler in method.Body.ExceptionHandlers)
					if (handler.CatchType != null)
						handler.CatchType = Type (handler.CatchType);
				foreach (var instruction in method.Body.Instructions)
					instruction.Operand = instruction.Operand switch {
						TypeReference operand => Type (operand),
						MethodReference operand => Method (operand),
						FieldReference operand => Field (operand),
						string text => Text (text),
						CallSite site => Site (site),
						_ => instruction.Operand,
					};
			}
		}

		CallSite Site (CallSite value)
		{
			var result = new CallSite (Type (value.ReturnType)) {
				CallingConvention = value.CallingConvention, HasThis = value.HasThis, ExplicitThis = value.ExplicitThis,
			};
			foreach (var parameter in value.Parameters)
				result.Parameters.Add (new ParameterDefinition (Type (parameter.ParameterType)));
			return result;
		}
	}
}
