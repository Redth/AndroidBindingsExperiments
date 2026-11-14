#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Java.Interop.Tools.Generator.Semantics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	public class GroupedAssemblyTests
	{
		[Test]
		public void ReferencedCanonicalOwnersCannotBeRedefinedInTheDeliveredTree ()
		{
			var directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "grouped-framework-" + Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
			try {
				using var assembly = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("Example", new Version (1, 0)),
					"Example.dll", ModuleKind.Dll);
				assembly.MainModule.Types.Add (new TypeDefinition ("Example", "Peer", Mono.Cecil.TypeAttributes.Public,
					assembly.MainModule.TypeSystem.Object));
				assembly.Write (Path.Combine (directory, "Example.dll"));
				File.WriteAllText (Path.Combine (directory, "ownership.json"), JsonSerializer.Serialize (new {
					Assemblies = new [] { new { Assembly = "Example" } },
					Types = new [] { new { ManagedType = "Example.Peer", Assembly = "Example" } },
					ReferencedOwners = new [] { new BindingReferencedOwner ("example/Peer", "Example.Peer", "Mono.Android") },
				}));
				var error = Assert.Throws<InvalidDataException> (() => AssemblyGroupVerifier.Verify (directory));
				StringAssert.Contains ("Referenced canonical type was regenerated", error?.Message);
				Assert.IsFalse (File.Exists (Path.Combine (directory, "verification.json")));
			} finally {
				Directory.Delete (directory, recursive: true);
			}
		}

		[Test]
		public void CanonicalOwnershipDoesNotMakeDuplicateNativeDefinitionsAcceptable ()
		{
			var directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "canonical-duplicate-" + Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
			try {
				foreach (var name in new [] { "first.jar", "second.jar" }) {
					using var archive = ZipFile.Open (Path.Combine (directory, name), ZipArchiveMode.Create);
					archive.CreateEntry ("example/Value.class");
				}
				var manifest = Path.Combine (directory, "inputs.json");
				File.WriteAllText (manifest, JsonSerializer.Serialize (new [] {
					new { group = "example", file = Path.Combine (directory, "first.jar") },
					new { group = "example", file = Path.Combine (directory, "second.jar") },
				}));
				var owners = Path.Combine (directory, "owners.json");
				File.WriteAllText (owners, JsonSerializer.Serialize (new {
					SchemaVersion = 1, Types = new [] { new BindingCanonicalOwner ("example/Value", "example", "Canonical") },
				}));
				var error = Assert.Throws<InvalidDataException> (() => new AssemblyGrouper (
					Path.Combine (directory, "Unused.dll"), manifest, Path.Combine (directory, "groups"), canonicalOwnerFile: owners));
				StringAssert.Contains ("duplicate definitions", error?.Message);
			} finally {
				Directory.Delete (directory, recursive: true);
			}
		}

		[TestCase (false)]
		[TestCase (true)]
		public void SourceImplementationsCompileAcrossCyclicCanonicalContracts (bool perArtifact)
		{
			var directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "grouped-source-" + Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
			try {
				var files = new [] {
					("Registration.cs", """
						namespace Android.Runtime {
							public sealed class RegisterAttribute : System.Attribute {
								public RegisterAttribute (string name) {}
							}
						}
						"""),
					("First.cs", """
						namespace Example {
							[Android.Runtime.Register ("first/First")]
							public class First {
								public Second Other;
								public static int Run () => Second.Value () + new int [] { 1, 2, 3, 4 } [0];
							}
						}
						"""),
					("Second.cs", """
						namespace Example {
							[Android.Runtime.Register ("second/Second")]
							public class Second {
								public First Other;
								public static int Value () => new int [] { 41, 2, 3, 4 } [0];
							}
						}
						"""),
				};
				foreach (var (name, content) in files)
					File.WriteAllText (Path.Combine (directory, name), content);
				var trusted = AppContext.GetData ("TRUSTED_PLATFORM_ASSEMBLIES") as string
					?? throw new InvalidOperationException ("No platform compiler references.");
				var platform = trusted.Split (Path.PathSeparator)
					.Where (p => Path.GetDirectoryName (p) == Path.GetDirectoryName (typeof (object).Assembly.Location))
					.Select (p => MetadataReference.CreateFromFile (p)).ToArray ();
				void Compile (string name, string[] sources, MetadataReference[] references, string path, bool metadataOnly)
				{
					var compilation = CSharpCompilation.Create (name,
						sources.Select (p => CSharpSyntaxTree.ParseText (File.ReadAllText (p), path: p)), references,
						new CSharpCompilationOptions (OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
					using var stream = File.Create (path);
					var result = compilation.Emit (stream, options: new EmitOptions (metadataOnly: metadataOnly, includePrivateMembers: true));
					Assert.IsTrue (result.Success, string.Join ("\n", result.Diagnostics));
				}
				var bootstrap = Path.Combine (directory, "Android.BindingCorpus.Bootstrap.dll");
				Compile ("Android.BindingCorpus.Bootstrap", files.Select (f => Path.Combine (directory, f.Item1)).ToArray (),
					platform, bootstrap, true);
				string Jar (string name)
				{
					var file = Path.Combine (directory, name + ".jar");
					using var archive = ZipFile.Open (file, ZipArchiveMode.Create);
					archive.CreateEntry (name.ToLowerInvariant () + "/" + name + ".class");
					return file;
				}
				var native = Path.Combine (directory, "native.json");
				File.WriteAllText (native, JsonSerializer.Serialize (new [] {
					new { group = perArtifact ? "example" : "example.first", artifact = "first", file = Jar ("First") },
					new { group = perArtifact ? "example" : "example.second", artifact = "second", file = Jar ("Second") },
				}));
				var contracts = Path.Combine (directory, "contracts");
				new AssemblyGrouper (bootstrap, native, contracts, directory, perArtifact: perArtifact).Emit ();
				using var manifest = JsonDocument.Parse (File.ReadAllText (Path.Combine (contracts, "ownership.json")));
				var groups = manifest.RootElement.GetProperty ("Assemblies").EnumerateArray ().ToArray ();
				if (perArtifact) {
					var registry = manifest.RootElement.GetProperty ("OwnerRegistry").EnumerateArray ().ToArray ();
					Assert.AreEqual (2, registry.Length);
					CollectionAssert.AreEquivalent (new [] { "first", "second" },
						registry.Select (o => o.GetProperty ("MavenArtifact").GetString ()));
					var established = Path.Combine (directory, "established.json");
					File.Copy (Path.Combine (contracts, "ownership.json"), established);
					using var original = JsonDocument.Parse (File.ReadAllText (native));
					File.WriteAllText (native, JsonSerializer.Serialize (original.RootElement.EnumerateArray ().Select (item => new {
						group = "example", artifact = item.GetProperty ("artifact").GetString () == "first" ? "moved-first" : "second",
						file = item.GetProperty ("file").GetString (),
					})));
					new AssemblyGrouper (bootstrap, native, contracts, directory, established, perArtifact: true).Emit ();
					using var moved = JsonDocument.Parse (File.ReadAllText (Path.Combine (contracts, "ownership.json")));
					var firstOwner = moved.RootElement.GetProperty ("OwnerRegistry").EnumerateArray ()
						.Single (o => o.GetProperty ("JavaType").GetString () == "first/First");
					Assert.AreEqual ("first", firstOwner.GetProperty ("MavenArtifact").GetString ());
				}
				var implementations = Path.Combine (directory, "implementations");
				Directory.CreateDirectory (implementations);
				foreach (var group in groups) {
					var name = group.GetProperty ("Assembly").GetString () ?? throw new InvalidOperationException ("No group name.");
					var sources = group.GetProperty ("SourceFiles").EnumerateArray ()
						.Select (p => p.GetString () ?? throw new InvalidOperationException ("No group source.")).ToArray ();
					var references = platform.Concat (groups.Where (g => g.GetProperty ("Assembly").GetString () != name)
						.Select (g => MetadataReference.CreateFromFile (Path.Combine (contracts, g.GetProperty ("Assembly").GetString () + ".dll")))).ToArray ();
					Compile (name, sources, references, Path.Combine (implementations, name + ".dll"), false);
				}
				ImplementationCatalog.Write (implementations, Path.Combine (contracts, "ownership.json"));
				AssemblyGroupVerifier.Verify (implementations);
				var context = new AssemblyLoadContext ("source-groups-" + Guid.NewGuid (), isCollectible: true);
				context.Resolving += (_, name) => context.LoadFromAssemblyPath (Path.Combine (implementations, name.Name + ".dll"));
				try {
					var first = perArtifact ? BindingCorpusCatalog.ArtifactAssemblyName ("example", "first") : "Android.Maven.Example.First";
					var type = context.LoadFromAssemblyPath (Path.Combine (implementations, first + ".dll"))
						.GetType ("Example.First") ?? throw new InvalidOperationException ("No source implementation.");
					var method = type.GetMethod ("Run") ?? throw new InvalidOperationException ("No source implementation method.");
					Assert.AreEqual (42, method.Invoke (null, null));
				} finally {
					context.Unload ();
				}
			} finally {
				Directory.Delete (directory, recursive: true);
			}
		}

		[TestCase (false)]
		[TestCase (true)]
		public void GroupedImplementationsPreserveCyclesNestedIlAndTypeIdentity (bool canonical)
		{
			var directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "grouped-" + Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
			try {
				var path = Path.Combine (directory, "Bootstrap.dll");
				using (var assembly = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("Bootstrap", new Version (1, 0)), "Bootstrap.dll", ModuleKind.Dll)) {
					var module = assembly.MainModule;
					var attribute = new TypeDefinition ("Android.Runtime", "RegisterAttribute",
						Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, module.ImportReference (typeof (Attribute)));
					module.Types.Add (attribute);
					var constructor = new MethodDefinition (".ctor", Mono.Cecil.MethodAttributes.Public |
						Mono.Cecil.MethodAttributes.SpecialName | Mono.Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
					constructor.Parameters.Add (new ParameterDefinition (module.TypeSystem.String));
					attribute.Methods.Add (constructor);
					var attributeConstructor = typeof (Attribute).GetConstructor (BindingFlags.Instance | BindingFlags.NonPublic, null, [], null)
						?? throw new InvalidOperationException ("Attribute has no default constructor.");
					constructor.Body.Instructions.Add (Instruction.Create (OpCodes.Ldarg_0));
					constructor.Body.Instructions.Add (Instruction.Create (OpCodes.Call, module.ImportReference (attributeConstructor)));
					constructor.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));

					TypeDefinition Peer (string name)
					{
						var type = new TypeDefinition ("Example", name, Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
							module.TypeSystem.Object);
						var registration = new CustomAttribute (constructor);
						registration.ConstructorArguments.Add (new CustomAttributeArgument (module.TypeSystem.String, name.ToLowerInvariant () + "/" + name));
						type.CustomAttributes.Add (registration);
						module.Types.Add (type);
						return type;
					}
					var first = Peer ("First");
					var second = Peer ("Second");
					var nested = new TypeDefinition ("", "Nested", Mono.Cecil.TypeAttributes.NestedPrivate | Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
					first.NestedTypes.Add (nested);
					var value = new FieldDefinition ("Value", Mono.Cecil.FieldAttributes.Assembly | Mono.Cecil.FieldAttributes.Static, module.TypeSystem.Int32);
					nested.Fields.Add (value);
					var initialize = new MethodDefinition (".cctor", Mono.Cecil.MethodAttributes.Static | Mono.Cecil.MethodAttributes.SpecialName |
						Mono.Cecil.MethodAttributes.RTSpecialName, module.TypeSystem.Void);
					initialize.Body.Instructions.Add (Instruction.Create (OpCodes.Ldc_I4, 42));
					initialize.Body.Instructions.Add (Instruction.Create (OpCodes.Stsfld, value));
					initialize.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
					nested.Methods.Add (initialize);
					var get = new MethodDefinition ("GetValue", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
					get.Body.Instructions.Add (Instruction.Create (OpCodes.Ldsfld, value));
					get.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
					first.Methods.Add (get);
					var forward = new MethodDefinition ("Forward", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
					forward.Body.Instructions.Add (Instruction.Create (OpCodes.Call, get));
					forward.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
					second.Methods.Add (forward);
					var run = new MethodDefinition ("Run", Mono.Cecil.MethodAttributes.Public | Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Int32);
					run.Body.Instructions.Add (Instruction.Create (OpCodes.Call, forward));
					run.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
					first.Methods.Add (run);
					first.Fields.Add (new FieldDefinition ("Other", Mono.Cecil.FieldAttributes.Public, second));
					second.Fields.Add (new FieldDefinition ("Other", Mono.Cecil.FieldAttributes.Public, first));
					assembly.Write (path);
				}
				string Jar (string name)
				{
					var file = Path.Combine (directory, name + ".jar");
					using var archive = ZipFile.Open (file, ZipArchiveMode.Create);
					archive.CreateEntry (name.ToLowerInvariant () + "/" + name + ".class");
					return file;
				}
				var manifest = Path.Combine (directory, "native.json");
				File.WriteAllText (manifest, JsonSerializer.Serialize (new [] {
					new { group = "example.first", file = Jar ("First") },
					new { group = "example.second", file = Jar ("Second") },
				}));
				var output = Path.Combine (directory, "groups");
				string? canonicalFile = null;
				var firstAssembly = canonical ? "Canonical.First" : "Android.Maven.Example.First";
				var secondAssembly = canonical ? "Canonical.Second" : "Android.Maven.Example.Second";
				if (canonical) {
					canonicalFile = Path.Combine (directory, "canonical.json");
					var ownerTypes = new [] {
						new BindingCanonicalOwner ("first/First", "stable.first", firstAssembly),
						new BindingCanonicalOwner ("second/Second", "stable.second", secondAssembly),
					};
					File.WriteAllText (canonicalFile, JsonSerializer.Serialize (new { SchemaVersion = 1, Types = ownerTypes.Take (1).ToArray () }));
					Assert.Throws<InvalidDataException> (() => new AssemblyGrouper (path, manifest, output, canonicalOwnerFile: canonicalFile).Emit ());
					File.WriteAllText (canonicalFile, JsonSerializer.Serialize (new { SchemaVersion = 1, Types = ownerTypes }));
				}
				new AssemblyGrouper (path, manifest, output, canonicalOwnerFile: canonicalFile).Emit ();
				AssemblyGroupVerifier.Verify (output);
				var stale = Path.Combine (output, "Android.Maven.Stale.dll");
				File.Copy (Path.Combine (output, firstAssembly + ".dll"), stale);
				Assert.Throws<InvalidDataException> (() => AssemblyGroupVerifier.Verify (output));
				File.Delete (stale);
				var firstPath = Path.Combine (output, firstAssembly + ".dll");
				using (var mutated = AssemblyDefinition.ReadAssembly (firstPath)) {
					var original = mutated.MainModule.Types.Single (t => t.FullName == "Example.First");
					var registration = original.CustomAttributes.Single (a => a.AttributeType.FullName == "Android.Runtime.RegisterAttribute");
					var alias = new TypeDefinition ("Example", "AnotherFirst", Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class,
						mutated.MainModule.TypeSystem.Object);
					var attribute = new CustomAttribute (registration.Constructor);
					foreach (var argument in registration.ConstructorArguments)
						attribute.ConstructorArguments.Add (argument);
					alias.CustomAttributes.Add (attribute);
					mutated.MainModule.Types.Add (alias);
					mutated.Write (firstPath + ".writing");
				}
				File.Move (firstPath + ".writing", firstPath, overwrite: true);
				var duplicate = Assert.Throws<InvalidDataException> (() => AssemblyGroupVerifier.Verify (output));
				StringAssert.Contains ("Multiple public projections", duplicate?.Message);
				Assert.IsFalse (File.Exists (Path.Combine (output, "verification.json")));
				var context = new AssemblyLoadContext ("groups-" + Guid.NewGuid (), isCollectible: true);
				context.Resolving += (_, name) => {
					var candidate = Path.Combine (output, name.Name + ".dll");
					return File.Exists (candidate) ? context.LoadFromAssemblyPath (candidate) : null;
				};
				try {
					var loaded = context.LoadFromAssemblyPath (Path.Combine (output, firstAssembly + ".dll"));
					var type = loaded.GetType ("Example.First") ?? throw new InvalidOperationException ("First type not emitted.");
					var method = type.GetMethod ("Run") ?? throw new InvalidOperationException ("Method not emitted.");
					Assert.AreEqual (42, method.Invoke (null, null));
					var field = type.GetField ("Other") ?? throw new InvalidOperationException ("Field not emitted.");
					Assert.AreEqual (secondAssembly, field.FieldType.Assembly.GetName ().Name);
					Assert.AreNotEqual ("Bootstrap", type.Assembly.GetName ().Name);
				} finally {
					context.Unload ();
				}
			} finally {
				Directory.Delete (directory, recursive: true);
			}
		}
	}
}
