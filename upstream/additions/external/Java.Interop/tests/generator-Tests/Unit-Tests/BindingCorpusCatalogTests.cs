using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Java.Interop.Tools.Generator.Semantics;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	public class BindingCorpusCatalogTests
	{
		[Test]
		public void ArtifactsInOneMavenGroupKeepDistinctAssembliesAndDependencies ()
		{
			var plan = BindingCorpusCatalog.Plan (new [] {
				Definition ("example/Core", "example:core:1"),
				Definition ("example/Ui", "example:ui:1", references: ["example/Core"]),
			}, perArtifact: true);
			Assert.AreEqual (2, plan.Types.Select (o => o.Assembly).Distinct ().Count ());
			Assert.AreEqual ("core", plan.Types.Single (o => o.JavaType == "example/Core").MavenArtifact);
			CollectionAssert.AreEqual (new [] { BindingCorpusCatalog.ArtifactAssemblyName ("example", "core") },
				plan.Dependencies.Single (d => d.Assembly == BindingCorpusCatalog.ArtifactAssemblyName ("example", "ui")).References);
		}

		[Test]
		public void ArtifactOwnersAndReservationsSurviveVersionAndContainerMoves ()
		{
			var first = BindingCorpusCatalog.Plan (new [] { Definition ("example/Value", "example:original:1") }, perArtifact: true);
			var absent = BindingCorpusCatalog.Plan ([], first.OwnerRegistry, perArtifact: true);
			var moved = BindingCorpusCatalog.Plan (new [] { Definition ("example/Value", "example:new-module:3") },
				absent.OwnerRegistry, perArtifact: true);
			Assert.AreEqual (first.Types.Single (), moved.Types.Single ());
			Assert.AreEqual ("original", moved.Types.Single ().MavenArtifact);
			Assert.Throws<InvalidDataException> (() => BindingCorpusCatalog.Plan ([],
				[new BindingCanonicalOwner ("example/Value", "example", "Group")], perArtifact: true));
		}

		[Test]
		public void DuplicateNamesAcrossArtifactsRequireReviewInsteadOfASecondProjection ()
		{
			var plan = BindingCorpusCatalog.Plan (new [] {
				Definition ("example/Value", "example:first:1"),
				Definition ("example/Value", "example:second:1"),
			}, perArtifact: true);
			Assert.IsEmpty (plan.Types);
			CollectionAssert.AreEqual (new [] { "example/Value" }, plan.OwnerConflicts);
		}

		[TestCase ("", "library")]
		[TestCase ("example", "../library")]
		[TestCase ("example", "")]
		public void InvalidArtifactOwnerNamesAreRejected (string group, string artifact)
		{
			Assert.Throws<InvalidDataException> (() => BindingCorpusCatalog.ArtifactAssemblyName (group, artifact));
		}

		[Test]
		public void IncompleteDescriptorCoverageCannotLeaveASuccessReport ()
		{
			var directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "catalog-coverage-" + Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
			try {
				var manifest = Path.Combine (directory, "inputs.json");
				File.WriteAllText (manifest, """
					[{"group":"com.google.firebase","artifact":"example","version":"1","file":"unused.jar","sha256":"native"}]
					""");
				File.WriteAllText (Path.Combine (directory, "index.json"), """{"schemaVersion":1,"status":"complete","artifacts":[]}""");
				var output = Path.Combine (directory, "catalog.json");
				File.WriteAllText (output, """{"Status":"old-success"}""");
				var error = Assert.Throws<InvalidDataException> (() => BindingCorpusCatalog.Write (manifest, directory, output));
				StringAssert.Contains ("exactly cover", error?.Message);
				Assert.IsFalse (File.Exists (output));
				File.WriteAllText (output, """{"Types":[]}""");
				Assert.Throws<InvalidOperationException> (() => BindingCorpusCatalog.Write (manifest, directory, output, output));
				Assert.IsTrue (File.Exists (output), "A failed invocation must not destroy its established owner input.");
			} finally {
				Directory.Delete (directory, recursive: true);
			}
		}

		static BindingCatalogDefinition Definition (string java, string coordinate, string shape = "one",
			string classification = "Public", params string [] references) =>
			new BindingCatalogDefinition (java, coordinate, coordinate + "-bytes", "class", classification, classification, shape, references);

		[Test]
		public void SharedTypesHaveOneOwnerAndEveryConsumerReferencesIt ()
		{
			var plan = BindingCorpusCatalog.Plan (new [] {
				Definition ("androidx/lifecycle/LifecycleOwner", "androidx.lifecycle:lifecycle-common:1"),
				Definition ("androidx/lifecycle/LifecycleOwner", "androidx.lifecycle:lifecycle-runtime:1"),
				Definition ("androidx/activity/Activity", "androidx.activity:activity:1", references: ["androidx/lifecycle/LifecycleOwner"]),
				Definition ("androidx/navigation/Navigation", "androidx.navigation:navigation:1", references: ["androidx/lifecycle/LifecycleOwner"]),
			});
			Assert.AreEqual (3, plan.Types.Length);
			Assert.AreEqual (1, plan.Types.Count (o => o.JavaType == "androidx/lifecycle/LifecycleOwner"));
			foreach (var assembly in new [] { "Android.Maven.Androidx.Activity", "Android.Maven.Androidx.Navigation" })
				CollectionAssert.AreEqual (new [] { "Android.Maven.Androidx.Lifecycle" },
					plan.Dependencies.Single (d => d.Assembly == assembly).References);
			Assert.AreEqual ("shared-public-contract", plan.Contracts.Single (c => c.JavaType == "androidx/lifecycle/LifecycleOwner").Status);
			Assert.IsEmpty (plan.PublicContractConflicts);
		}

		[Test]
		public void AnEstablishedOwnerSurvivesAChangeOfMavenContainer ()
		{
			var pinned = new BindingCanonicalOwner ("example/Value", "example.original", "Foundation");
			var plan = BindingCorpusCatalog.Plan (new [] { Definition ("example/Value", "example.new:library:2") }, [pinned]);
			Assert.AreEqual (pinned, plan.Types.Single ());
		}

		[Test]
		public void OwnerReservationsSurviveAnApiTemporarilyDisappearing ()
		{
			var first = BindingCorpusCatalog.Plan (new [] { Definition ("example/Value", "example.original:library:1") });
			var absent = BindingCorpusCatalog.Plan ([], first.OwnerRegistry);
			Assert.IsEmpty (absent.Types, "An absent API must not be re-advertised as available.");
			Assert.AreEqual (1, absent.OwnerRegistry.Length);
			var returned = BindingCorpusCatalog.Plan (new [] { Definition ("example/Value", "example.new:library:3") }, absent.OwnerRegistry);
			Assert.AreEqual (first.Types.Single (), returned.Types.Single ());
		}

		[Test]
		public void DifferentContractsNeverAllocateAlternativePublicProjections ()
		{
			var plan = BindingCorpusCatalog.Plan (new [] {
				Definition ("example/Value", "example:library:1", "class-contract"),
				Definition ("example/Value", "example:library:2", "interface-contract") with { Kind = "interface" },
			});
			Assert.AreEqual (1, plan.Types.Length);
			CollectionAssert.AreEqual (new [] { "example/Value" }, plan.PublicContractConflicts);
			Assert.AreEqual ("native-contract-selection-required", plan.Contracts.Single ().Status);
		}

		[Test]
		public void UnrelatedInfrastructureDoesNotPollutePublicIdentityButRequiredClosureIsPreserved ()
		{
			var plan = BindingCorpusCatalog.Plan (new [] {
				Definition ("com/google/android/gms/example/Api", "com.google.android.gms:api:1", references: ["com/google/android/gms/example/zzcallback"]),
				Definition ("com/google/android/gms/example/zzcallback", "com.google.android.gms:api:1", classification: "Infrastructure",
					references: ["com/google/android/gms/example/zzhelper"]),
				Definition ("com/google/android/gms/example/zzcallback", "com.google.android.gms:other:2", "different", "Infrastructure"),
				Definition ("com/google/android/gms/example/zzhelper", "com.google.android.gms:api:1", classification: "Infrastructure"),
				Definition ("com/google/android/gms/example/zzunused", "com.google.android.gms:other:2", classification: "Infrastructure"),
			});
			Assert.AreEqual (1, plan.Types.Length);
			Assert.IsEmpty (plan.PublicContractConflicts);
			CollectionAssert.AreEquivalent (new [] { "com/google/android/gms/example/zzcallback", "com/google/android/gms/example/zzhelper" },
				plan.RequiredInfrastructure);
			CollectionAssert.AreEqual (new [] { "com/google/android/gms/example/zzcallback" }, plan.RequiredInfrastructureConflicts);
		}

		[Test]
		public void CrossPublisherOwnershipRequiresAnEstablishedOwnerOrMatchingFamily ()
		{
			var definitions = new [] {
				Definition ("example/Value", "first:library:1"),
				Definition ("example/Value", "second:library:1"),
			};
			var unresolved = BindingCorpusCatalog.Plan (definitions);
			Assert.IsEmpty (unresolved.Types);
			CollectionAssert.AreEqual (new [] { "example/Value" }, unresolved.OwnerConflicts);
			var resolved = BindingCorpusCatalog.Plan (definitions, [new BindingCanonicalOwner ("example/Value", "stable", "Stable")]);
			Assert.AreEqual ("Stable", resolved.Types.Single ().Assembly);
		}

		[Test]
		public void ClassificationPreservesDescriptorsAndDoesNotPromoteObfuscatedImplementationTypes ()
		{
			var document = XDocument.Parse ("""
				<api><package name="com.google.android.gms.example">
				  <class name="Api" visibility="public" jni-signature="Lcom/google/android/gms/example/Api;">
				    <method name="getCallback" visibility="public" return="com.google.android.gms.example.zzcallback"
				      jni-signature="()Lcom/google/android/gms/example/zzcallback;" />
				  </class>
				  <interface name="zzcallback" visibility="public" jni-signature="Lcom/google/android/gms/example/zzcallback;">
				    <method name="run" visibility="public" abstract="true" jni-signature="()V" return="void" />
				  </interface>
				</package></api>
				""");
			var before = document.ToString ();
			var hashes = new Dictionary<string, string> {
				["com/google/android/gms/example/Api"] = "api",
				["com/google/android/gms/example/zzcallback"] = "callback",
			};
			var definitions = BindingCorpusCatalog.Classify (document, "com.google.android.gms:example:1", hashes);
			Assert.AreEqual (before, document.ToString ());
			Assert.AreEqual ("Infrastructure", definitions.Single (d => d.JavaType.EndsWith ("/zzcallback")).Classification);
			CollectionAssert.Contains (definitions.Single (d => d.JavaType.EndsWith ("/Api")).References, "com/google/android/gms/example/zzcallback");
			var plan = BindingCorpusCatalog.Plan (definitions);
			Assert.AreEqual (1, plan.Types.Length);
			CollectionAssert.AreEqual (new [] { "com/google/android/gms/example/zzcallback" }, plan.RequiredInfrastructure);
		}

		[Test]
		public void NongenericInterfaceAndNestedSourceNamesContributeDependencies ()
		{
			var facts = XDocument.Parse ("""
				<api><package name="example">
				  <class name="Api" visibility="public" jni-signature="Lexample/Api;">
				    <implements name="example.Outer.Contract" />
				  </class>
				</package></api>
				""");
			var definitions = BindingCorpusCatalog.Classify (facts, "example.api:api:1",
				new Dictionary<string, string> { ["example/Api"] = "native" }).Concat (new [] {
					Definition ("example/Outer$Contract", "example.contract:contract:1") with { Kind = "interface" },
				});
			var plan = BindingCorpusCatalog.Plan (definitions);
			CollectionAssert.AreEqual (new [] { "Android.Maven.Example.Contract" },
				plan.Dependencies.Single (d => d.Assembly == "Android.Maven.Example.Api").References);
		}

		[Test]
		public void ParameterNamesDoNotInventDifferentNativeContracts ()
		{
			var original = XDocument.Parse ("""
				<api><package name="example"><class name="Api" visibility="public" jni-signature="Lexample/Api;">
				  <method name="call" visibility="public" return="void" jni-signature="(I)V">
				    <parameter name="first" type="int" />
				  </method>
				</class></package></api>
				""");
			var renamed = XDocument.Parse (original.ToString ().Replace ("name=\"first\"", "name=\"second\""));
			var hashes = new Dictionary<string, string> { ["example/Api"] = "native" };
			Assert.AreEqual (BindingCorpusCatalog.Classify (original, "example:api:1", hashes).Single ().ContractSha256,
				BindingCorpusCatalog.Classify (renamed, "example:api:2", hashes).Single ().ContractSha256);
		}
	}
}
