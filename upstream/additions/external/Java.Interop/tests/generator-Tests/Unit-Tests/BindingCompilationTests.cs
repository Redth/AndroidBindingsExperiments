using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Xml.Linq;
using Java.Interop.Tools.Generator.Semantics;
using Java.Interop.Tools.JavaTypeSystem;
using NUnit.Framework;
using Xamarin.Android.Binder;

namespace generatortests
{
	[TestFixture]
	public class BindingCompilationTests
	{
		const string api = """
			<api api-source="class-parse">
			  <package name="java.lang" jni-name="java/lang">
			    <class name="Object" visibility="public" jni-signature="Ljava/lang/Object;" />
			    <class name="String" visibility="public" extends="java.lang.Object" extends-generic-aware="java.lang.Object" jni-signature="Ljava/lang/String;" />
			  </package>
			  <package name="example" jni-name="example">
			    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object" extends-generic-aware="java.lang.Object" jni-signature="Lexample/Base;">
			      <typeParameters>
			        <typeParameter name="T"><genericConstraints><genericConstraint type="java.lang.Object" /></genericConstraints></typeParameter>
			      </typeParameters>
			      <method name="convert" visibility="public" abstract="true" final="false" return="T" jni-return="TT;" jni-signature="(Ljava/lang/Object;)Ljava/lang/Object;">
			        <parameter name="value" type="T" jni-type="TT;" />
			      </method>
			    </class>
			    <class name="Derived" visibility="public" extends="example.Base" extends-generic-aware="example.Base&lt;java.lang.String&gt;" jni-signature="Lexample/Derived;" source-file-name="Example.kt">
			      <method name="convert" visibility="public" final="false" return="java.lang.String" return-not-null="true" jni-return="Ljava/lang/String;" jni-signature="(Ljava/lang/String;)Ljava/lang/String;">
			        <parameter name="value" type="java.lang.String" jni-type="Ljava/lang/String;" not-null="true" />
			      </method>
			      <method name="convert" visibility="public" final="false" bridge="true" synthetic="true" return="java.lang.Object" jni-return="Ljava/lang/Object;" jni-signature="(Ljava/lang/Object;)Ljava/lang/Object;">
			        <parameter name="value" type="java.lang.Object" jni-type="Ljava/lang/Object;" />
			      </method>
			      <field name="Companion" type="example.Derived.Companion" type-generic-aware="example.Derived.Companion" visibility="public" static="true" final="true" jni-signature="Lexample/Derived$Companion;" />
			      <field name="CompanionInstance" type="int" type-generic-aware="int" visibility="public" static="true" final="true" jni-signature="I" />
			      <field name="Hidden" type="example.Derived.Hidden" type-generic-aware="example.Derived.Hidden" visibility="public" static="true" final="true" jni-signature="Lexample/Derived$Hidden;" />
			    </class>
			    <class name="Derived.Companion" visibility="public" extends="java.lang.Object" extends-generic-aware="java.lang.Object" static="true" jni-signature="Lexample/Derived$Companion;" />
			    <class name="Derived.Hidden" visibility="private" extends="java.lang.Object" extends-generic-aware="java.lang.Object" static="true" jni-signature="Lexample/Derived$Hidden;" />
			    <class name="ArrayDerived" visibility="public" extends="example.Base" extends-generic-aware="example.Base&lt;java.lang.String[]&gt;" jni-signature="Lexample/ArrayDerived;">
			      <method name="convert" visibility="public" final="false" return="java.lang.String[]" jni-return="[Ljava/lang/String;" jni-signature="([Ljava/lang/String;)[Ljava/lang/String;">
			        <parameter name="value" type="java.lang.String[]" jni-type="[Ljava/lang/String;" />
			      </method>
			      <method name="convert" visibility="public" final="false" bridge="true" synthetic="true" return="java.lang.Object" jni-return="Ljava/lang/Object;" jni-signature="(Ljava/lang/Object;)Ljava/lang/Object;">
			        <parameter name="value" type="java.lang.Object" jni-type="Ljava/lang/Object;" />
			      </method>
			    </class>
			  </package>
			</api>
			""";

		[Test]
		public void FactGraphIsIndependentOfInputAndDestructiveResolution ()
		{
			var source = XDocument.Parse (api);
			var compilation = new BindingCompilation (source);
			var original = compilation.ExportFacts ().ToString ();
			source.Descendants ("method").Remove ();
			var resolved = JavaXmlApiImporter.ParseString (original);
			resolved.ResolveCollection ();
			foreach (var type in resolved.TypesFlattened.Values)
				type.Methods.Clear ();

			Assert.AreEqual (original, compilation.ExportFacts ().ToString ());
			Assert.AreEqual (2, compilation.Types.Single (t => t.JavaName == "example.Derived").Methods.Count);
		}

		[Test]
		public void OverridesKeepTypedJvmDescriptorsButImplementErasedManagedSlots ()
		{
			var plan = CreatePlan (XDocument.Parse (api), out var adjusted);
			var lowered = plan.Lower (adjusted);
			var method = FindType (lowered, "Derived").Elements ("method").Single ();

			Assert.AreEqual (2, plan.Overrides.Count, "Both scalar and array generic instantiations need overrides.");
			Assert.AreEqual ("java.lang.Object", method.Attribute ("return")?.Value);
			Assert.AreEqual ("java.lang.Object", method.Element ("parameter")?.Attribute ("type")?.Value);
			Assert.AreEqual ("(Ljava/lang/String;)Ljava/lang/String;", method.Attribute ("managed-jni-signature")?.Value);
			Assert.AreEqual ("Ljava/lang/String;", method.Element ("parameter")?.Attribute ("managed-jni-type")?.Value);
			Assert.AreEqual ("false", method.Element ("parameter")?.Attribute ("not-null")?.Value, "The managed override must accept the base contract's nullability.");
			Assert.AreEqual ("([Ljava/lang/String;)[Ljava/lang/String;", plan.Overrides.Single (p => p.DeclaringType == "example.ArrayDerived").Descriptor);
		}

		[Test]
		public void BridgeInvocationFactsDisambiguateSameArityOverloads ()
		{
			var source = XDocument.Parse (api);
			source.Descendants ("package").Single (p => (string) p.Attribute ("name") == "java.lang").Add (XElement.Parse ("""
				<class name="Integer" visibility="public" extends="java.lang.Object" extends-generic-aware="java.lang.Object" jni-signature="Ljava/lang/Integer;" />
				"""));
			var derived = FindType (source, "Derived");
			derived.Elements ("method").Single (m => (string) m.Attribute ("bridge") == "true")
				.SetAttributeValue ("jvm-bridge-targets",
					"""[{"owner":"example/Derived","name":"convert","descriptor":"(Ljava/lang/String;)Ljava/lang/String;"}]""");
			derived.Add (XElement.Parse ("""
				<method name="convert" visibility="public" return="java.lang.Integer" jni-return="Ljava/lang/Integer;" jni-signature="(Ljava/lang/Integer;)Ljava/lang/Integer;">
				  <parameter name="value" type="java.lang.Integer" jni-type="Ljava/lang/Integer;" />
				</method>
				"""));
			var plan = CreatePlan (source, out var adjusted);
			var methods = FindType (plan.Lower (adjusted), "Derived").Elements ("method").ToArray ();
			Assert.AreEqual ("(Ljava/lang/Object;)Ljava/lang/Object;", methods.Single (m =>
				(string) m.Attribute ("jni-signature") == "(Ljava/lang/String;)Ljava/lang/String;").Attribute ("managed-bridge-signatures")?.Value);
			Assert.IsNull (methods.Single (m => (string) m.Attribute ("jni-signature") ==
				"(Ljava/lang/Integer;)Ljava/lang/Integer;").Attribute ("managed-bridge-signatures"));
		}

		[Test]
		public void SemanticReportStoresNativeFactsAsAnAdjacentXmlDocument ()
		{
			var plan = CreatePlan (XDocument.Parse (api), out _);
			var directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "native-facts-" + Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
			try {
				var path = Path.Combine (directory, "semantics.json");
				plan.WriteReport (path);
				using var report = JsonDocument.Parse (File.ReadAllText (path));
				Assert.AreEqual (2, report.RootElement.GetProperty ("SchemaVersion").GetInt32 ());
				var relative = report.RootElement.GetProperty ("BinaryFactsFile").GetString ();
				Assert.IsNotNull (relative);
				Assert.IsFalse (Path.IsPathRooted (relative));
				var facts = XDocument.Load (Path.Combine (directory, relative));
				Assert.IsTrue (facts.Descendants ("class").Any (c => (string) c.Attribute ("name") == "Derived"));
			} finally {
				Directory.Delete (directory, recursive: true);
			}
		}

		[Test]
		public void HiddenFluentBasesDoNotReplaceAccessibleTypedReturns ()
		{
			var source = XDocument.Parse ("""
				<api api-source="class-parse"><package name="java.lang">
				  <class name="Object" visibility="public" jni-signature="Ljava/lang/Object;" />
				</package><package name="example">
				  <class name="Base" visibility="" extends="java.lang.Object" extends-generic-aware="java.lang.Object" jni-signature="Lexample/Base;">
				    <typeParameters><typeParameter name="T"><genericConstraints><genericConstraint type="example.Base" /></genericConstraints></typeParameter></typeParameters>
				    <method name="self" visibility="public" return="T" jni-return="TT;" jni-signature="()Lexample/Base;" />
				  </class>
				  <class name="Derived" visibility="public" extends="example.Base" extends-generic-aware="example.Base&lt;example.Derived&gt;" jni-signature="Lexample/Derived;">
				    <method name="self" visibility="public" return="example.Derived" jni-return="Lexample/Derived;" jni-signature="()Lexample/Derived;" />
				    <method name="self" visibility="public" bridge="true" synthetic="true" return="example.Base" jni-return="Lexample/Base;" jni-signature="()Lexample/Base;" />
				  </class>
				</package></api>
				""");
			var plan = CreatePlan (source, out var adjusted);
			var method = FindType (plan.Lower (adjusted), "Derived").Elements ("method")
				.Single (m => m.Attribute ("jni-signature")?.Value == "()Lexample/Derived;");

			Assert.AreEqual ("example.Derived", method.Attribute ("return")?.Value);
			Assert.AreEqual ("()Lexample/Derived;", method.Attribute ("managed-jni-signature")?.Value);
		}

		[Test]
		public void OverrideProjectionRequiresActualBridgeEvidence ()
		{
			var source = XDocument.Parse (api);
			FindType (source, "Derived").Elements ("method").Where (m => m.Attribute ("bridge")?.Value == "true").Remove ();
			var plan = CreatePlan (source, out _);

			Assert.IsFalse (plan.Overrides.Any (p => p.DeclaringType == "example.Derived"));
		}

		[Test]
		public void UnitReturningOverridesInvokeTheRealObjectBridgeWithoutReinterpretingVoid ()
		{
			var source = XDocument.Parse ("""
				<api api-source="class-parse"><package name="java.lang">
				  <class name="Object" visibility="public" jni-signature="Ljava/lang/Object;" />
				</package><package name="kotlin">
				  <class name="Unit" visibility="public" extends="java.lang.Object" extends-generic-aware="java.lang.Object" jni-signature="Lkotlin/Unit;" />
				</package><package name="example">
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object" extends-generic-aware="java.lang.Object" jni-signature="Lexample/Base;">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <method name="parse" visibility="public" abstract="true" return="T" jni-signature="()Ljava/lang/Object;" />
				  </class>
				  <class name="Derived" visibility="public" extends="example.Base" extends-generic-aware="example.Base&lt;kotlin.Unit&gt;" jni-signature="Lexample/Derived;">
				    <method name="parse" visibility="public" return="void" jni-signature="()V" />
				    <method name="parse" visibility="public" return="java.lang.Object" bridge="true" synthetic="true"
				      jni-signature="()Ljava/lang/Object;"
				      jvm-bridge-targets='[{"owner":"example/Derived","name":"parse","descriptor":"()V"}]' />
				  </class>
				</package></api>
				""");
			var plan = CreatePlan (source, out var adjusted);
			var methods = FindType (plan.Lower (adjusted), "Derived").Elements ("method").ToArray ();
			Assert.IsEmpty (plan.Overrides);
			Assert.IsTrue (plan.RestoredMethods.Any (m => m.Name == "parse" && m.Descriptor == "()Ljava/lang/Object;"));
			var typed = methods.Single (m => (string) m.Attribute ("jni-signature") == "()V");
			StringAssert.StartsWith ("parseNative", (string) typed.Attribute ("managedName"));
			Assert.AreEqual ("void", (string) typed.Attribute ("return"));
			Assert.IsTrue (methods.Any (m => (string) m.Attribute ("jni-signature") == "()Ljava/lang/Object;" &&
				(string) m.Attribute ("managed-jni-signature") == "()Ljava/lang/Object;"));
		}

		[Test]
		public void StandaloneSyntheticImplementationsSurviveButRedundantBridgesDoNot ()
		{
			var source = XDocument.Parse (api);
			FindType (source, "Derived").Add (XElement.Parse ("""
				<method name="create" visibility="public" synthetic="true" bridge="true" return="java.lang.Object" jni-return="Ljava/lang/Object;" jni-signature="()Ljava/lang/Object;" />
				"""));
			FindType (source, "Derived").Add (XElement.Parse ("""
				<method name="newArray" visibility="public" synthetic="true" bridge="false" return="java.lang.Object[]" jni-return="[Ljava/lang/Object;" jni-signature="(I)[Ljava/lang/Object;">
				  <parameter name="size" type="int" jni-type="I" />
				</method>
				"""));

			var plan = CreatePlan (source, out var adjusted);
			Assert.IsFalse (FindType (adjusted, "Derived").Elements ("method").Any (m => m.Attribute ("name")?.Value == "create"));
			var lowered = FindType (plan.Lower (adjusted), "Derived");

			Assert.AreEqual (2, plan.RestoredMethods.Count);
			Assert.AreEqual ("()Ljava/lang/Object;", lowered.Elements ("method").Single (m => m.Attribute ("name")?.Value == "create").Attribute ("managed-jni-signature")?.Value);
			Assert.IsNotNull (lowered.Elements ("method").Single (m => m.Attribute ("name")?.Value == "newArray"));
			Assert.AreEqual (1, lowered.Elements ("method").Count (m => m.Attribute ("name")?.Value == "convert"));
		}

		[TestCase (true, true, true)]
		[TestCase (false, true, true)]
		[TestCase (true, false, true)]
		[TestCase (false, false, true)]
		[TestCase (true, true, false)]
		[TestCase (true, false, false)]
		public void ErasedAbstractSlotsRetainOnlyRequiredNativeBridges (bool abstractBase, bool importedBase, bool bridgeFlag)
		{
			var source = XDocument.Parse (api);
			FindType (source, "Derived").Elements ("method").Single (m => m.Attribute ("synthetic")?.Value == "true")
				.SetAttributeValue ("bridge", bridgeFlag ? "true" : "false");
			var base_type = FindType (source, "Base");
			base_type.Element ("typeParameters").Remove ();
			var method = base_type.Element ("method");
			method.SetAttributeValue ("abstract", abstractBase ? "true" : "false");
			method.SetAttributeValue ("return", "java.lang.Object");
			method.SetAttributeValue ("jni-return", "Ljava/lang/Object;");
			method.Element ("parameter").SetAttributeValue ("type", "java.lang.Object");
			method.Element ("parameter").SetAttributeValue ("jni-type", "Ljava/lang/Object;");
			var references = JavaXmlApiImporter.ParseString (source.ToString ());
			var reference = references.TypesFlattened ["example.Base"];
			if (importedBase)
				base_type.Remove ();
			var compilation = new BindingCompilation (source);
			var resolved = JavaXmlApiImporter.ParseString (source.ToString ());
			if (importedBase)
				resolved.AddReferencedType (reference);
			var resolution = resolved.ResolveCollection ();
			Assert.AreEqual (0, resolution.Count);
			var plan = compilation.Plan (resolved);
			XDocument adjusted;
			using (var writer = new StringWriter ()) {
				using (var xml = System.Xml.XmlWriter.Create (writer))
					JavaXmlApiExporter.Save (resolved, xml);
				adjusted = XDocument.Parse (writer.ToString ());
			}
			var lowered = FindType (plan.Lower (adjusted), "Derived").Elements ("method").ToArray ();

			Assert.AreEqual (abstractBase ? 2 : 1, lowered.Length);
			Assert.AreEqual (abstractBase, lowered.Any (m => m.Attribute ("managed-jni-signature")?.Value ==
				"(Ljava/lang/Object;)Ljava/lang/Object;"));
			Assert.IsTrue (lowered.Any (m => m.Attribute ("managed-jni-signature")?.Value ==
				"(Ljava/lang/String;)Ljava/lang/String;"));
			Assert.IsEmpty (plan.Overrides);
		}

		[Test]
		public void SyntheticNonBridgeInterfaceImplementationsRetainTheirOwnNativeSlots ()
		{
			var source = XDocument.Parse (api);
			var package = source.Root.Elements ("package").Single (p => p.Attribute ("name")?.Value == "example");
			package.Add (XElement.Parse ("""
				<interface name="Contract" visibility="public" jni-signature="Lexample/Contract;">
				  <method name="convert" visibility="public" abstract="true" return="java.lang.Object" jni-return="Ljava/lang/Object;" jni-signature="(Ljava/lang/Object;)Ljava/lang/Object;">
				    <parameter name="value" type="java.lang.Object" jni-type="Ljava/lang/Object;" />
				  </method>
				</interface>
				"""));
			var derived = FindType (source, "Derived");
			derived.SetAttributeValue ("extends", "java.lang.Object");
			derived.SetAttributeValue ("extends-generic-aware", "java.lang.Object");
			derived.Add (XElement.Parse ("""<implements name="example.Contract" name-generic-aware="example.Contract" />"""));
			derived.Elements ("method").Single (m => m.Attribute ("synthetic")?.Value == "true").SetAttributeValue ("bridge", "false");
			var plan = CreatePlan (source, out var adjusted);
			var methods = FindType (plan.Lower (adjusted), "Derived").Elements ("method").ToArray ();

			Assert.IsTrue (plan.RestoredMethods.Any (m => m.DeclaringType == "example.Derived" &&
				m.Descriptor == "(Ljava/lang/Object;)Ljava/lang/Object;"));
			Assert.AreEqual (2, methods.Length);
			Assert.IsTrue (methods.Any (m => m.Attribute ("managed-jni-signature")?.Value == "(Ljava/lang/String;)Ljava/lang/String;"));
			Assert.IsTrue (methods.Any (m => m.Attribute ("managed-jni-signature")?.Value == "(Ljava/lang/Object;)Ljava/lang/Object;"));
		}

		[Test]
		public void JavaFinalizerIsNotProjectedAsTheClrFinalizer ()
		{
			var source = XDocument.Parse (api);
			FindType (source, "Derived").Add (XElement.Parse ("""
				<method name="finalize" visibility="protected" return="void" jni-return="V" jni-signature="()V" />
				"""));
			var plan = CreatePlan (source, out var adjusted);
			var method = FindType (plan.Lower (adjusted), "Derived").Elements ("method").Single (m => m.Attribute ("name")?.Value == "finalize");

			Assert.AreEqual ("JavaFinalize", method.Attribute ("managedName")?.Value);
			Assert.AreEqual ("()V", method.Attribute ("managed-jni-signature")?.Value);
		}

		[Test]
		public void PublicNestedTypeAndSingletonFieldBothSurviveWithStableNames ()
		{
			var plan = CreatePlan (XDocument.Parse (api), out var adjusted);
			var lowered = plan.Lower (adjusted);
			var rename = plan.FieldRenames.Single ();
			var field = FindType (lowered, "Derived").Elements ("field").Single (f => f.Attribute ("name")?.Value == "Companion");

			Assert.AreEqual ("CompanionInstance2", rename.ManagedName);
			Assert.AreEqual (rename.ManagedName, field.Attribute ("managedName")?.Value);
			Assert.AreEqual ("Lexample/Derived$Companion;", field.Attribute ("jni-signature")?.Value);
			Assert.IsNotNull (FindType (lowered, "Derived.Companion"));
		}

		[Test]
		public void InaccessibleFieldTypeIsOpaqueWithoutChangingItsJvmDescriptor ()
		{
			var plan = CreatePlan (XDocument.Parse (api), out var adjusted);
			var field = FindType (plan.Lower (adjusted), "Derived").Elements ("field").Single (f => f.Attribute ("name")?.Value == "Hidden");

			Assert.AreEqual ("java.lang.Object", field.Attribute ("type")?.Value);
			Assert.AreEqual ("Lexample/Derived$Hidden;", field.Attribute ("managed-jni-signature")?.Value);
			Assert.AreEqual (1, plan.OpaqueFields.Count);
		}

		[Test]
		public void PackagePrivateFieldTypeIsOpaqueWithoutChangingItsJvmDescriptor ()
		{
			var source = XDocument.Parse (api);
			FindType (source, "Derived.Hidden").SetAttributeValue ("visibility", "");
			var plan = CreatePlan (source, out var adjusted);
			var field = FindType (plan.Lower (adjusted), "Derived").Elements ("field").Single (f => f.Attribute ("name")?.Value == "Hidden");

			Assert.AreEqual ("java.lang.Object", field.Attribute ("type")?.Value);
			Assert.AreEqual ("Lexample/Derived$Hidden;", field.Attribute ("managed-jni-signature")?.Value);
		}

		[Test]
		public void KotlinProjectionFactsSurviveXmlAdjustment ()
		{
			var source = XDocument.Parse (api);
			var type = FindType (source, "Derived");
			type.SetAttributeValue ("kotlin-inline-class", "true");
			type.SetAttributeValue ("kotlin-inline-class-underlying-jni-type", "J");
			var method = type.Elements ("method").First ();
			method.SetAttributeValue ("kotlin-inline-class-return-jni-type", "Lexample/Value;");
			method.Element ("parameter").SetAttributeValue ("kotlin-inline-class-jni-type", "Lexample/Value;");

			var plan = CreatePlan (source, out var adjusted);
			Assert.IsNull (FindType (adjusted, "Derived").Attribute ("kotlin-inline-class"));
			var lowered = FindType (plan.Lower (adjusted), "Derived");
			Assert.AreEqual ("true", lowered.Attribute ("kotlin-inline-class")?.Value);
			Assert.AreEqual ("Example.kt", lowered.Attribute ("source-file-name")?.Value);
			Assert.AreEqual ("Lexample/Value;", lowered.Elements ("method").Single ().Attribute ("kotlin-inline-class-return-jni-type")?.Value);
		}

		[TestCase ("(Ljava/lang/String;)")]
		[TestCase ("(V)Ljava/lang/Object;")]
		[TestCase ("(I)Vtrailing")]
		[TestCase ("()V")]
		public void MalformedOrMismatchedDescriptorsAreRejected (string descriptor)
		{
			var source = XDocument.Parse (api);
			FindType (source, "Derived").Elements ("method").First ().SetAttributeValue ("jni-signature", descriptor);

			Assert.Throws<InvalidDataException> (() => new BindingCompilation (source));
		}

		[Test]
		public void KotlinConstructorDefaultsSurviveDestructiveXmlAdjustment ()
		{
			var source = XDocument.Parse (api);
			FindType (source, "Derived").Add (XElement.Parse ("""
				<constructor name="Derived" visibility="public" jni-signature="(I)V"
				  kotlin-default-mask-count="1" kotlin-default-jni-signature="(IILkotlin/jvm/internal/DefaultConstructorMarker;)V">
				  <parameter name="value" type="int" jni-type="I" kotlin-default-argument-index="0" />
				</constructor>
				"""));
			var plan = CreatePlan (source, out var adjusted);
			var adjustedConstructor = FindType (adjusted, "Derived").Element ("constructor");
			Assert.IsNotNull (adjustedConstructor);
			Assert.IsNull (adjustedConstructor.Attribute ("kotlin-default-jni-signature"));
			var restored = FindType (plan.Lower (adjusted), "Derived").Element ("constructor");
			Assert.IsNotNull (restored);
			Assert.AreEqual ("(IILkotlin/jvm/internal/DefaultConstructorMarker;)V", restored.Attribute ("kotlin-default-jni-signature")?.Value);
			Assert.AreEqual ("0", restored.Element ("parameter")?.Attribute ("kotlin-default-argument-index")?.Value);
		}

		[Test]
		public void DuplicateJvmIdentitiesAreRejected ()
		{
			var source = XDocument.Parse (api);
			var type = FindType (source, "Derived");
			type.Add (new XElement (type.Elements ("method").First ()));

			Assert.Throws<InvalidDataException> (() => new BindingCompilation (source));
		}

		[Test]
		public void SemanticModeIsExplicit ()
		{
			var options = CodeGeneratorOptions.Parse (["--experimental-semantic-model", "api.xml"]);
			Assert.IsTrue (options.ExperimentalSemanticModel);
			Assert.IsFalse (new CodeGeneratorOptions ().ExperimentalSemanticModel);
		}

		static BindingProjectionPlan CreatePlan (XDocument source, out XDocument adjusted)
		{
			var compilation = new BindingCompilation (source);
			var resolved = JavaXmlApiImporter.ParseString (source.ToString ());
			var resolution = resolved.ResolveCollection ();
			Assert.AreEqual (0, resolution.Count, "The fixture must resolve without pruning: " +
				string.Join (", ", resolution.SelectMany (r => r.Unresolvables).Select (u => $"{u.MissingType} ({u.Unresolvable})")));
			using (var writer = new StringWriter ()) {
				using (var xml = System.Xml.XmlWriter.Create (writer))
					JavaXmlApiExporter.Save (resolved, xml);
				adjusted = XDocument.Parse (writer.ToString ());
			}
			return compilation.Plan (resolved);
		}

		static XElement FindType (XDocument source, string name) =>
			source.Descendants ("class").Single (t => t.Attribute ("name")?.Value == name);
	}
}
