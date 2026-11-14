using System;
using System.Linq;
using generator.SourceWriters;
using Java.Interop.Tools.Generator.Semantics;
using Mono.Cecil;
using MonoDroid.Generation;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class BindingSupportContractTests : CodeGeneratorTestBase
	{
		[Test]
		public void IndirectInterfaceMappingsKeepAlreadyConcreteArguments ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" /><class name="String" visibility="public" extends="java.lang.Object" />
				</package><package name="example">
				  <interface name="Parent" visibility="public"><typeParameters><typeParameter name="T" /></typeParameters></interface>
				  <interface name="Child" visibility="public"><typeParameters><typeParameter name="U" /></typeParameters>
				    <implements name="example.Parent" name-generic-aware="example.Parent&lt;java.lang.String&gt;" />
				  </interface>
				  <class name="Peer" visibility="public" extends="java.lang.Object">
				    <implements name="example.Child" name-generic-aware="example.Child&lt;java.lang.Object&gt;" />
				  </class>
				</package></api>
				""");
			var map = new System.Collections.Generic.Dictionary<string, string> ();
			Assert.IsTrue (types.Single (t => t.Name == "Peer").GetGenericMappings ((InterfaceGen) types.Single (t => t.Name == "IParent"), map));
			Assert.AreEqual ("string", map ["T"]);
		}

		[Test]
		public void SourceGenericAbstractPropertiesKeepPublicContracts ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example"><class name="Provider" visibility="public" abstract="true" extends="java.lang.Object">
				  <typeParameters><typeParameter name="T"><genericConstraints><genericConstraint type="java.lang.Object" /></genericConstraints></typeParameter></typeParameters>
				  <method name="getValue" return="T" abstract="true" visibility="public" />
				</class></package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var type = types.Single (t => t.Name == "Provider");
			var property = new BoundAbstractProperty (type, type.Properties.Single (), options);
			Assert.IsTrue (property.IsPublic);
		}

		[Test]
		public void CovariantInheritedInterfaceSlotsGetSeparateInvocations ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Node" visibility="public" extends="java.lang.Object" />
				  <interface name="General" visibility="public">
				    <method name="find" return="java.lang.Object" visibility="public" abstract="true" />
				  </interface>
				  <interface name="Specific" visibility="public">
				    <implements name="example.General" name-generic-aware="example.General" />
				    <method name="find" return="example.Node" visibility="public" abstract="true" />
				  </interface>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "ISpecific"));
			StringAssert.Contains ("Example.IGeneral.Find ()", output);
			StringAssert.Contains ("find.()Ljava/lang/Object;", output);
			StringAssert.Contains ("find.()Lexample/Node;", output);
		}

		[TestCase (true, "sbyte")]
		[TestCase (false, "byte")]
		public void ImportedSignedBytesPreserveActualClrContracts (bool semantic, string expected)
		{
			options.ExperimentalSemanticModel = semantic;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example"><interface name="Data" visibility="public" /></package></api>
				""");
			using var module = ModuleDefinition.CreateModule ("SignedByte", ModuleKind.Dll);
			var type = new TypeDefinition ("Example", "Data", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
			module.Types.Add (type);
			var method = new MethodDefinition ("ReadByte", MethodAttributes.Public | MethodAttributes.Abstract | MethodAttributes.Virtual, module.TypeSystem.SByte);
			type.Methods.Add (method);
			var constructor = typeof (Android.Runtime.RegisterAttribute).GetConstructor (new [] { typeof (string), typeof (string), typeof (string) })
				?? throw new InvalidOperationException ("Missing RegisterAttribute constructor.");
			var registration = new CustomAttribute (module.ImportReference (constructor));
			foreach (var value in new [] { "readByte", "()B", "" })
				registration.ConstructorArguments.Add (new CustomAttributeArgument (module.TypeSystem.String, value));
			method.CustomAttributes.Add (registration);
			var imported = CecilApiImporter.CreateMethod (types.Single (t => t.Name == "IData"), method, options);
			Assert.AreEqual (expected, imported.ManagedReturn);
		}

		[Test]
		public void DuplicateStringConveniencesKeepBothNativeOperations ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <class name="String" visibility="public" extends="java.lang.Object" />
				  <interface name="CharSequence" visibility="public" />
				</package><package name="example"><class name="Strings" visibility="public" extends="java.lang.Object">
				  <method name="strip" return="java.lang.CharSequence" visibility="public" static="true">
				    <parameter name="value" type="java.lang.CharSequence" />
				    <parameter name="prefix" type="java.lang.CharSequence" />
				  </method>
				  <method name="strip" return="java.lang.String" visibility="public" static="true">
				    <parameter name="value" type="java.lang.String" />
				    <parameter name="prefix" type="java.lang.CharSequence" />
				  </method>
				</class></package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Strings"));
			Assert.AreEqual (1, output.Split ("Strip (string value, string prefix)").Length - 1);
			StringAssert.Contains ("strip.(Ljava/lang/CharSequence;Ljava/lang/CharSequence;)Ljava/lang/CharSequence;", output);
			StringAssert.Contains ("strip.(Ljava/lang/String;Ljava/lang/CharSequence;)Ljava/lang/String;", output);
		}

		[TestCase (false, true)]
		[TestCase (true, false)]
		public void OnlyNonStaticNestedClassesCaptureEnclosingTypeParameters (bool isStatic, bool retained)
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ($$"""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Outer" visibility="public" extends="java.lang.Object">
				    <typeParameters><typeParameter name="T"><genericConstraints><genericConstraint type="java.lang.Object" /></genericConstraints></typeParameter></typeParameters>
				  </class>
				  <class name="Outer.Inner" visibility="public" static="{{isStatic.ToString ().ToLowerInvariant ()}}" extends="java.lang.Object">
				    <method name="getValue" return="T" visibility="public" />
				  </class>
				</package></api>
				""");
			var inner = types.Single (t => t.Name == "Outer").NestedTypes.Single ();
			Assert.AreEqual (retained, inner.GetAllMethods ().Any (m => m.JavaName == "getValue"));
		}

		[Test]
		public void InheritedContractsUseFinalBaseBridgeNamesRegardlessOfInputOrder ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Result" visibility="public" extends="java.lang.Object" />
				  <interface name="Provider" visibility="public">
				    <method name="create" return="java.lang.Object" visibility="public" abstract="true" />
				  </interface>
				  <class name="Derived" visibility="public" extends="example.Base">
				    <implements name="example.Provider" name-generic-aware="example.Provider" />
				  </class>
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				    <method name="create" return="example.Result" visibility="public" final="false" managed-bridge-signatures="()Ljava/lang/Object;" />
				    <method name="create" return="java.lang.Object" visibility="public" final="false" managed-bridge-signatures="()Ljava/lang/Object;" />
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Derived"));
			StringAssert.Contains ("Example.IProvider.Create ()", output);
			StringAssert.Contains ("create.()Ljava/lang/Object;", output);
		}
	}
}
