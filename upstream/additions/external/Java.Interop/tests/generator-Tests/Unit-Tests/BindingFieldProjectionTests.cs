using System.Linq;
using System.Xml.Linq;
using MonoDroid.Generation;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class BindingFieldProjectionTests : CodeGeneratorTestBase
	{
		const string api = """
			<api>
			  <package name="java.lang">
			    <class name="Object" visibility="public" />
			  </package>
			  <package name="example">
			    <class name="Owner" visibility="public" extends="java.lang.Object">
			      <field name="Hidden" type="java.lang.Object" visibility="public" static="true" final="false" managed-jni-signature="Lexample/PrivateType;" />
			    </class>
			  </package>
			</api>
			""";

		[Test]
		public void GetterAndSetterUseTheBinaryFieldDescriptor ()
		{
			var owner = ParseApiDefinition (api).Single (g => g.JavaName == "example.Owner");
			var output = GetGeneratedTypeOutput (owner);

			Assert.AreEqual (2, output.Split ("Hidden.Lexample/PrivateType;").Length - 1);
			StringAssert.DoesNotContain ("Hidden.Ljava/lang/Object;", output);
			StringAssert.Contains ("Java.Lang.Object Hidden", output);
		}

		[Test]
		public void SemanticArrayFieldQualifiesItsElementType ()
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var document = XDocument.Parse (api);
			var field = document.Descendants ("field").Single ();
			field.SetAttributeValue ("type", "java.lang.Object[]");
			field.SetAttributeValue ("managed-jni-signature", "[Ljava/lang/Object;");
			var owner = ParseApiDefinition (document.ToString ()).Single (g => g.JavaName == "example.Owner");

			StringAssert.Contains ("IList<global::Java.Lang.Object>", GetGeneratedTypeOutput (owner));
		}

		[TestCase ("I")]
		[TestCase ("Ljava.lang.Object;")]
		[TestCase ("TT;")]
		[TestCase ("V")]
		public void AbiIncompatibleOrInvalidDescriptorsAreRejected (string descriptor)
		{
			var document = XDocument.Parse (api);
			document.Descendants ("field").Single ().SetAttributeValue ("managed-jni-signature", descriptor);
			var package = document.Descendants ("package").First ();
			var object_type = XmlApiImporter.CreateClass (package, package.Element ("class"), options);
			options.SymbolTable.AddType (object_type);
			var owner_element = document.Descendants ("class").Single (c => c.Attribute ("name")?.Value == "Owner");
			var owner = XmlApiImporter.CreateClass (owner_element.Parent, owner_element, options);
			var field = owner.Fields.Single ();

			Assert.IsFalse (field.Validate (options, new GenericParameterDefinitionList (), generator.Context));
		}
	}
}
