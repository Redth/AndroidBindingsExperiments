using System.Linq;
using System.Xml.Linq;
using Java.Interop.Tools.Generator.Semantics;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	public class BindingAccessibilityProjectionTests
	{
		static XDocument CreateApi () => XDocument.Parse ("""
			<api api-source="class-parse"><package name="example">
			  <interface name="Contract" visibility="public" jni-signature="Lexample/Contract;">
			    <method name="run" visibility="public" abstract="true" jni-signature="()V" return="void" />
			  </interface>
			  <class name="Service" visibility="public" jni-signature="Lexample/Service;">
			    <implements name="example.Contract" />
			    <method name="run" visibility="public" jni-signature="()V" return="void" annotated-visibility="LIBRARY" />
			    <method name="internalOperation" visibility="public" final="true" jni-signature="()V" return="void" annotated-visibility="LIBRARY_GROUP_PREFIX" />
			    <method name="hook" visibility="public" abstract="true" jni-signature="()V" return="void" annotated-visibility="SUBCLASSES" />
			    <method name="testHook" visibility="public" final="true" jni-signature="()V" return="void" api-visible-for-testing="protected" />
			    <method name="deprecatedButPublic" visibility="public" jni-signature="()V" return="void" deprecated="deprecated" />
			    <method name="bridge" visibility="public" jni-signature="()V" return="void" synthetic="true" bridge="true" />
			  </class>
			  <class name="InternalPeer" visibility="public" jni-signature="Lexample/InternalPeer;" annotated-visibility="LIBRARY">
			    <method name="value" visibility="public" jni-signature="()I" return="int" />
			  </class>
			  <class name="InternalPeer.Nested" visibility="public" jni-signature="Lexample/InternalPeer$Nested;" />
			  <class name="KotlinInternal" visibility="private" jvm-visibility="public" jni-signature="Lexample/KotlinInternal;" />
			</package></api>
			""");

		[Test]
		public void PublisherImplementationNamesAreInternalFactsNotDroppedNativeContracts ()
		{
			var facts = XDocument.Parse ("""
				<api><package name="com.google.android.gms.example">
				  <class name="zzreused" visibility="public" jni-signature="Lcom/google/android/gms/example/zzreused;">
				    <method name="run" visibility="public" abstract="true" jni-signature="()V" return="void" />
				  </class>
				  <interface name="zzcallback" visibility="public" obfuscated="false" jni-signature="Lcom/google/android/gms/example/zzcallback;">
				    <method name="zza" visibility="public" abstract="true" obfuscated="false" jni-signature="()V" return="void" />
				  </interface>
				  <class name="PublicApi" visibility="public" jni-signature="Lcom/google/android/gms/example/PublicApi;">
				    <method name="zza" visibility="public" final="true" jni-signature="()V" return="void" />
				    <method name="zzcallback" visibility="public" abstract="true" jni-signature="()V" return="void" />
				  </class>
				</package><package name="com.google.android.gms.example.internal">
				  <class name="Implementation" visibility="public" jni-signature="Lcom/google/android/gms/example/internal/Implementation;" />
				</package><package name="example.internal">
				  <class name="zzLegitimate" visibility="public" jni-signature="Lexample/internal/zzLegitimate;" />
				</package></api>
				""");
			var original = facts.ToString ();
			var classification = new BindingAccessibilityProjection (facts);
			var lowered = new XDocument (facts);
			classification.Apply (lowered);
			Assert.AreEqual (original, facts.ToString ());
			foreach (var name in new [] { "zzreused", "Implementation" }) {
				var type = lowered.Descendants ("class").Single (c => (string) c.Attribute ("name") == name);
				Assert.AreEqual ("Infrastructure", (string) type.Attribute ("api-classification"));
				Assert.AreEqual ("internal", (string) type.Attribute ("visibility"));
			}
			var callback = lowered.Descendants ("interface").Single ();
			Assert.AreEqual ("Public", (string) callback.Attribute ("api-classification"));
			Assert.AreEqual ("public", (string) callback.Attribute ("visibility"));
			Assert.AreEqual ("public", (string) callback.Element ("method").Attribute ("visibility"));
			var api = lowered.Descendants ("class").Single (c => (string) c.Attribute ("name") == "PublicApi");
			Assert.AreEqual ("private", (string) api.Elements ("method").Single (m => (string) m.Attribute ("name") == "zza").Attribute ("visibility"));
			Assert.AreEqual ("protected", (string) api.Elements ("method").Single (m => (string) m.Attribute ("name") == "zzcallback").Attribute ("visibility"));
			Assert.AreEqual ("Public", (string) lowered.Descendants ("class").Single (c => (string) c.Attribute ("name") == "zzLegitimate").Attribute ("api-classification"));
			Assert.AreEqual (3, lowered.Descendants ("class").Count (c => ((string) c.Attribute ("jni-signature")).StartsWith ("Lcom/google")));
		}

		[Test]
		public void MetadataCanRestoreAnExplicitPublisherExceptionAfterSemanticLowering ()
		{
			var facts = XDocument.Parse ("""
				<api><package name="com.google.android.gms.example">
				  <interface name="zzcallback" visibility="public" jni-signature="Lcom/google/android/gms/example/zzcallback;">
				    <method name="run" visibility="public" abstract="true" jni-signature="()V" return="void" />
				  </interface>
				</package></api>
				""");
			new BindingAccessibilityProjection (facts).Apply (facts);
			var type = facts.Descendants ("interface").Single ();
			Assert.AreEqual ("internal", (string) type.Attribute ("visibility"));
			type.SetAttributeValue ("obfuscated", "false");
			BindingPublisherPolicy.RestoreExplicitExceptions (facts);
			Assert.AreEqual ("public", (string) type.Attribute ("visibility"));
			Assert.AreEqual ("Public", (string) type.Attribute ("api-classification"));
			Assert.AreEqual ("public", (string) type.Element ("method").Attribute ("visibility"));
			StringAssert.DoesNotContain ("EditorBrowsable", (string) type.Attribute ("customAttributes"));
			type.SetAttributeValue ("visibility", "internal");
			type.SetAttributeValue ("api-classification-reason", "google-obfuscated-implementation");
			type.SetAttributeValue ("api-internal", "true");
			BindingPublisherPolicy.RestoreExplicitExceptions (facts);
			Assert.AreEqual ("internal", (string) type.Attribute ("visibility"));
		}

		[Test]
		public void GoogleObfuscationPolicyCoversTheConfiguredPlayVisionAndFirebaseFamilies ()
		{
			foreach (var package in new [] { "com.google.android.play.core", "com.google.android.libraries.vision.pipeline",
				"com.google.firebase.ml", "com.google.mlkit.vision" }) {
				var facts = XDocument.Parse ($"""
					<api><package name="{package}">
					  <interface name="zza" visibility="public" jni-signature="L{package.Replace ('.', '/')}/zza;" />
					</package></api>
					""");
				var plan = new BindingAccessibilityProjection (facts);
				Assert.AreEqual ("Infrastructure", plan.Decisions.Single ().Classification, package);
			}
		}

		[Test]
		public void PublisherExceptionsDoNotOverrideExplicitSourceRestrictions ()
		{
			var facts = XDocument.Parse ("""
				<api><package name="com.google.android.gms.example">
				  <interface name="zzcallback" visibility="public" obfuscated="false"
				    api-internal="true" jni-signature="Lcom/google/android/gms/example/zzcallback;" />
				</package></api>
				""");
			new BindingAccessibilityProjection (facts).Apply (facts);
			Assert.AreEqual ("Infrastructure", (string) facts.Descendants ("interface").Single ().Attribute ("api-classification"));
		}

		[Test]
		public void ClassificationPreservesFactsAndSeparatesPublicExtensibilityAndInfrastructure ()
		{
			var source = CreateApi ();
			var original = source.ToString ();
			var plan = new BindingAccessibilityProjection (source);
			var lowered = new XDocument (source);
			plan.Apply (lowered);

			Assert.AreEqual (original, source.ToString ());
			Assert.AreEqual ("public", Method (lowered, "run").Attribute ("visibility")?.Value);
			Assert.AreEqual ("Infrastructure", Method (lowered, "run").Attribute ("api-classification")?.Value);
			Assert.AreEqual ("private", Method (lowered, "internalOperation").Attribute ("visibility")?.Value);
			Assert.AreEqual ("protected", Method (lowered, "hook").Attribute ("visibility")?.Value);
			Assert.AreEqual ("protected", Method (lowered, "testHook").Attribute ("visibility")?.Value);
			Assert.AreEqual ("public", Method (lowered, "deprecatedButPublic").Attribute ("visibility")?.Value);
			Assert.AreEqual ("public", Method (lowered, "bridge").Attribute ("visibility")?.Value);
			foreach (var name in new [] { "InternalPeer", "InternalPeer.Nested" })
				Assert.AreEqual ("internal", lowered.Descendants ("class").Single (c => (string) c.Attribute ("name") == name).Attribute ("visibility")?.Value);
			Assert.AreEqual ("private", lowered.Descendants ("class").Single (c => (string) c.Attribute ("name") == "KotlinInternal").Attribute ("visibility")?.Value);
		}

		[TestCase ("LIBRARY")]
		[TestCase ("LIBRARY_GROUP")]
		[TestCase ("LIBRARY_GROUP_PREFIX")]
		[TestCase ("TESTS")]
		[TestCase ("LIBRARY SUBCLASSES")]
		public void LibraryAndTestScopesDoNotBecomeOrdinaryPublicOperations (string scopes)
		{
			var source = CreateApi ();
			Method (source, "internalOperation").SetAttributeValue ("annotated-visibility", scopes);
			var plan = new BindingAccessibilityProjection (source);
			plan.Apply (source);

			Assert.AreEqual ("private", Method (source, "internalOperation").Attribute ("visibility")?.Value);
		}

		[TestCase ("private")]
		[TestCase ("package")]
		[TestCase ("none")]
		public void VisibleForTestingUsesIntendedAccess (string intended)
		{
			var source = CreateApi ();
			Method (source, "testHook").SetAttributeValue ("api-visible-for-testing", intended);
			new BindingAccessibilityProjection (source).Apply (source);

			Assert.AreEqual ("private", Method (source, "testHook").Attribute ("visibility")?.Value);
		}

		[Test]
		public void NewMetadataAndUnknownScopesRemainExplicitlyUnverified ()
		{
			var source = CreateApi ();
			source.Descendants ("class").Single (c => (string) c.Attribute ("name") == "Service")
				.SetAttributeValue ("kotlin-metadata-status", "unsupported");
			Method (source, "deprecatedButPublic").SetAttributeValue ("annotated-visibility", "FUTURE_SCOPE");
			var plan = new BindingAccessibilityProjection (source);
			plan.Apply (source);

			Assert.AreEqual ("public", Method (source, "deprecatedButPublic").Attribute ("visibility")?.Value);
			Assert.AreEqual ("Unverified", Method (source, "deprecatedButPublic").Attribute ("api-classification")?.Value);
		}

		[Test]
		public void RestrictedAbstractMethodsRetainNecessaryImplementationContracts ()
		{
			var source = CreateApi ();
			Method (source, "internalOperation").SetAttributeValue ("abstract", "true");
			Method (source, "internalOperation").SetAttributeValue ("final", "false");
			var method = source.Descendants ("interface").Single ().Element ("method");
			Assert.IsNotNull (method);
			method.SetAttributeValue ("annotated-visibility", "LIBRARY");
			new BindingAccessibilityProjection (source).Apply (source);

			Assert.AreEqual ("protected", Method (source, "internalOperation").Attribute ("visibility")?.Value);
			Assert.AreEqual ("public", method.Attribute ("visibility")?.Value);
			Assert.AreEqual ("Infrastructure", method.Attribute ("api-classification")?.Value);
		}

		[Test]
		public void ReferencedPublicContractsRemainPublicButHiddenFromOrdinaryDiscovery ()
		{
			var source = CreateApi ();
			var plan = new BindingAccessibilityProjection (source, new [] { ("example.Service", "internalOperation()V") });
			plan.Apply (source);

			var method = Method (source, "internalOperation");
			Assert.AreEqual ("public", method.Attribute ("visibility")?.Value);
			Assert.AreEqual ("Infrastructure", method.Attribute ("api-classification")?.Value);
			StringAssert.Contains ("EditorBrowsable", method.Attribute ("customAttributes")?.Value);
		}

		[Test]
		public void RestrictedVirtualMethodsRetainHiddenProtectedImplementationSlots ()
		{
			var source = CreateApi ();
			Method (source, "internalOperation").SetAttributeValue ("final", "false");
			new BindingAccessibilityProjection (source).Apply (source);

			var method = Method (source, "internalOperation");
			Assert.AreEqual ("protected", method.Attribute ("visibility")?.Value);
			Assert.AreEqual ("Infrastructure", method.Attribute ("api-classification")?.Value);
			StringAssert.Contains ("EditorBrowsable", method.Attribute ("customAttributes")?.Value);
		}

		[Test]
		public void PackagePrivateNestedTypesAreNotPromotedToPublicByTheLegacyEmitter ()
		{
			var source = CreateApi ();
			source.Descendants ("package").Single ().Add (XElement.Parse ("""
				<class name="Service.Anonymous" visibility="" jni-signature="Lexample/Service$Anonymous;" />
				"""));
			new BindingAccessibilityProjection (source).Apply (source);
			var hidden = source.Descendants ("class").Single (c => (string) c.Attribute ("name") == "Service.Anonymous");
			Assert.AreEqual ("private", hidden.Attribute ("visibility")?.Value);
			Assert.AreEqual ("Excluded", hidden.Attribute ("api-classification")?.Value);
		}

		[Test]
		public void RestrictedOwnersDoNotPromoteTheirPrivateOrPackagePrivateNestedClasses ()
		{
			var source = CreateApi ();
			source.Descendants ("package").Single ().Add (
				XElement.Parse ("""<class name="InternalPeer.PrivateChild" visibility="private" jni-signature="Lexample/InternalPeer$PrivateChild;" />"""),
				XElement.Parse ("""<class name="InternalPeer.PackageChild" visibility="" jni-signature="Lexample/InternalPeer$PackageChild;" />"""));
			new BindingAccessibilityProjection (source).Apply (source);
			foreach (var child in source.Descendants ("class").Where (c => ((string) c.Attribute ("name")).EndsWith ("Child"))) {
				Assert.AreEqual ("Excluded", child.Attribute ("api-classification")?.Value);
				Assert.AreEqual ("private", child.Attribute ("visibility")?.Value);
			}
		}

		[Test]
		public void FinalNativeOverridesRetainReferencedProtectedClrContracts ()
		{
			var source = CreateApi ();
			var plan = new BindingAccessibilityProjection (source, protectedContracts: new [] { ("example.Service", "internalOperation()V") });
			plan.Apply (source);
			Assert.AreEqual ("protected", Method (source, "internalOperation").Attribute ("visibility")?.Value);
			Assert.AreEqual ("required-protected-inherited-contract", Method (source, "internalOperation").Attribute ("api-classification-reason")?.Value);
		}

		static XElement Method (XDocument document, string name) =>
			document.Descendants ("class").Single (c => (string) c.Attribute ("name") == "Service")
				.Elements ("method").Single (m => (string) m.Attribute ("name") == name);
	}
}
