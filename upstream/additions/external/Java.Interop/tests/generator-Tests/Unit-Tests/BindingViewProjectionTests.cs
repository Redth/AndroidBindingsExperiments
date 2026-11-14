using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using Java.Interop.Tools.Cecil;
using Java.Interop.Tools.Diagnostics;
using Java.Interop.Tools.Generator.Semantics;
using Mono.Cecil;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class BindingViewProjectionTests
	{
		string directory;
		string peerAssembly;
		string api;
		BindingViewContract contract;

		[SetUp]
		public void SetUp ()
		{
			directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "binding-views-" + Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
			peerAssembly = Path.Combine (directory, "Canonical.dll");
			api = Path.Combine (directory, "api.xml");
			File.WriteAllText (api, """
				<api api-source="class-parse"><package name="example">
				  <class name="Peer" visibility="public" jni-signature="Lexample/Peer;">
				    <method name="read" visibility="public" return="int" jni-signature="()I" />
				  </class>
				</package></api>
				""");
			using var assembly = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("Canonical", new Version (1, 0)), "Canonical", ModuleKind.Dll);
			var module = assembly.MainModule;
			var peer = new TypeDefinition ("Example", "Peer", TypeAttributes.Public | TypeAttributes.Class, module.TypeSystem.Object);
			module.Types.Add (peer);
			peer.CustomAttributes.Add (Registration (module, "example/Peer", "", ""));
			var method = new MethodDefinition ("Read", MethodAttributes.Public, module.TypeSystem.Int32);
			method.Body.Instructions.Add (Mono.Cecil.Cil.Instruction.Create (Mono.Cecil.Cil.OpCodes.Ldc_I4_0));
			method.Body.Instructions.Add (Mono.Cecil.Cil.Instruction.Create (Mono.Cecil.Cil.OpCodes.Ret));
			method.CustomAttributes.Add (Registration (module, "read", "()I", ""));
			peer.Methods.Add (method);
			assembly.Write (peerAssembly);
			contract = new BindingViewContract {
				SchemaVersion = 1,
				Views = [new BindingViewDefinition {
					JavaType = "example/Peer", PeerType = "Example.Peer", OwnerAssembly = "Canonical",
					Namespace = "Consumer.Local", Name = "PeerView",
					Members = [new BindingViewMember { JavaName = "read", JniSignature = "()I", ManagedMember = "Read", Name = "Count" }],
				}],
			};
		}

		[TearDown]
		public void TearDown () => Directory.Delete (directory, recursive: true);

		static CustomAttribute Registration (ModuleDefinition module, params string [] values)
		{
			var type = new TypeReference ("Android.Runtime", "RegisterAttribute", module, module.TypeSystem.CoreLibrary);
			var ctor = new MethodReference (".ctor", module.TypeSystem.Void, type) { HasThis = true };
			foreach (var value in values)
				ctor.Parameters.Add (new ParameterDefinition (module.TypeSystem.String));
			var attribute = new CustomAttribute (ctor);
			foreach (var value in values)
				attribute.ConstructorArguments.Add (new CustomAttributeArgument (module.TypeSystem.String, value));
			return attribute;
		}

		void Generate ()
		{
			var file = Path.Combine (directory, "view.json");
			File.WriteAllText (file, JsonSerializer.Serialize (contract));
			using var resolver = new DirectoryAssemblyResolver (Diagnostic.CreateConsoleLogger (), loadDebugSymbols: false);
			BindingViewProjection.Generate (file, api, directory, [peerAssembly], resolver);
		}

		[Test]
		public void ViewsKeepCanonicalOwnershipWithoutRegisteringAnotherPeer ()
		{
			Generate ();
			var text = File.ReadAllText (Path.Combine (directory, "Consumer.Local.PeerView.cs"));
			StringAssert.Contains ("readonly global::Example.Peer peer", text);
			StringAssert.Contains ("return peer.@Read ();", text);
			StringAssert.DoesNotContain ("Register", text);
			StringAssert.DoesNotContain ("IDisposable", text);
			using var requirements = JsonDocument.Parse (File.ReadAllText (Path.Combine (directory, "native-requirements.json")));
			Assert.AreEqual (1, requirements.RootElement.GetProperty ("Requirements").GetArrayLength ());
		}

		[Test]
		public void MissingNativeSlotsFailBeforeEmittingSources ()
		{
			contract.Views [0].Members [0].JniSignature = "(I)I";
			StringAssert.Contains ("missing native member", Assert.Throws<InvalidDataException> (Generate).Message);
			Assert.IsFalse (File.Exists (Path.Combine (directory, "Consumer.Local.PeerView.cs")));
		}

		[Test]
		public void WrongCanonicalAssemblyFailsExplicitly ()
		{
			contract.Views [0].OwnerAssembly = "Wrong";
			StringAssert.Contains ("canonical CLR owner", Assert.Throws<InvalidDataException> (Generate).Message);
		}

		[Test]
		public void UnambiguousMembersDoNotRequireHandwrittenDescriptors ()
		{
			contract.Views [0].Members [0].JniSignature = "";
			Generate ();
			using var requirements = JsonDocument.Parse (File.ReadAllText (Path.Combine (directory, "native-requirements.json")));
			Assert.AreEqual ("()I", requirements.RootElement.GetProperty ("Requirements") [0].GetProperty ("JniSignature").GetString ());
		}

		[Test]
		public void InvalidMemberNamesAreRejected ()
		{
			contract.Views [0].Members [0].Name = "Bad;Name";
			Assert.Throws<InvalidDataException> (Generate);
		}

		[Test]
		public void ResponseFileCanContainTheWholeInvocation ()
		{
			var response = Path.Combine (directory, "generator.rsp");
			File.WriteAllText (response, "--experimental-semantic-model\n\"" + api + "\"\n");
			var options = Xamarin.Android.Binder.CodeGeneratorOptions.Parse (["@" + response]);
			Assert.IsNotNull (options);
			Assert.AreEqual (api, options.ApiDescriptionFile);
		}

		[Test]
		public void FinalApplicationRejectsRemovedNativeRequirementsAndOwnerChanges ()
		{
			Generate ();
			using (var assembly = AssemblyDefinition.ReadAssembly (peerAssembly)) {
				assembly.MainModule.Resources.Add (new EmbeddedResource ("Java.Interop.NativeRequirements.json",
					ManifestResourceAttributes.Public, File.ReadAllBytes (Path.Combine (directory, "native-requirements.json"))));
				assembly.Write (Path.Combine (directory, "Consumer.dll"));
			}
			var ownerFile = Path.Combine (directory, "owners.json");
			File.WriteAllText (ownerFile, """
				{"SchemaVersion":1,"Types":[{"JavaType":"example/Peer","ManagedType":"Example.Peer","Assembly":"Canonical"}]}
				""");
			var result = Path.Combine (directory, "validated.json");
			void Validate ()
			{
				using var resolver = new DirectoryAssemblyResolver (Diagnostic.CreateConsoleLogger (), loadDebugSymbols: false);
				BindingViewProjection.ValidateRequirements (api, ownerFile, [Path.Combine (directory, "Consumer.dll")], result, resolver);
			}
			Validate ();
			Assert.IsTrue (File.Exists (result));
			File.WriteAllText (api, File.ReadAllText (api).Replace ("name=\"read\"", "name=\"removed\""));
			StringAssert.Contains ("requires missing native member", Assert.Throws<InvalidDataException> (Validate).Message);
			File.WriteAllText (ownerFile, File.ReadAllText (ownerFile).Replace ("Canonical", "ChangedOwner"));
			StringAssert.Contains ("canonical CLR owner", Assert.Throws<InvalidDataException> (Validate).Message);
		}

		[Test]
		public void MavenGroupsCollapseOnlyActualDirectedCycles ()
		{
			var graph = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.HashSet<string>> {
				["activity"] = ["lifecycle"], ["lifecycle"] = ["savedstate"],
				["savedstate"] = ["lifecycle"], ["datastore"] = ["kotlin"], ["kotlin"] = [],
			};
			var components = BindingOwnershipPlanner.CollapseCycles (graph);
			Assert.AreEqual (4, components.Count);
			Assert.IsTrue (components.Any (c => c.SequenceEqual (new [] { "lifecycle", "savedstate" })));
		}

		[Test]
		public void ArtifactMovementDoesNotChangeTheExplicitCanonicalOwner ()
		{
			var archiveFile = Path.Combine (directory, "native.jar");
			using (var zip = ZipFile.Open (archiveFile, ZipArchiveMode.Create))
			using (var entry = zip.CreateEntry ("example/Peer.class").Open ())
				entry.WriteByte (0);
			var manifest = Path.Combine (directory, "native.json");
			var report = Path.Combine (directory, "ownership.json");
			foreach (var group in new [] { "old.maven.group", "new.maven.group" }) {
				File.WriteAllText (manifest, JsonSerializer.Serialize (new [] { new { group, file = archiveFile } }));
				BindingOwnershipPlanner.WriteReport (report, api, manifest, [("example/Peer", "Example.Peer", "Canonical")]);
				using var result = JsonDocument.Parse (File.ReadAllText (report));
				var owner = result.RootElement.GetProperty ("Types") [0];
				Assert.AreEqual ("Canonical", owner.GetProperty ("Assembly").GetString ());
				Assert.AreEqual ("Example.Peer", owner.GetProperty ("ManagedType").GetString ());
				Assert.AreEqual (group, owner.GetProperty ("MavenGroup").GetString ());
			}
		}
	}
}
