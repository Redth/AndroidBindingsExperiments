using System.Linq;
using generator.SourceWriters;
using Java.Interop.Tools.Generator.Semantics;
using MonoDroid.Generation;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class ConsolidatedContractTests : CodeGeneratorTestBase
	{
		[Test]
		public void DeepNestedTypesRebaseAfterInterfaceOwnerNamesAreAllocated ()
		{
			options.ExperimentalSemanticModel = true;
			options.SupportDefaultInterfaceMethods = options.SupportNestedInterfaceTypes = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Contract" visibility="public">
				    <method name="getData" visibility="public" abstract="true" return="example.Contract.Data" />
				  </interface>
				  <class name="Contract.Data" visibility="public" extends="java.lang.Object" />
				  <class name="Contract.Data.SdkData" visibility="public" extends="java.lang.Object" />
				  <class name="Contract.Data.SdkData.Detail" visibility="public" extends="java.lang.Object" />
				</package></api>
				""");
			ManagedContractProjection.AllocateTypeNames (types);
			var owner = types.Single (t => t.JavaName == "example.Contract");
			var data = owner.NestedTypes.Single ();
			var sdk = data.NestedTypes.Single ();
			Assert.AreEqual (data.FullName + "." + sdk.Name, sdk.FullName);
			var detail = sdk.NestedTypes.Single ();
			Assert.AreEqual (sdk.FullName + "." + detail.Name, detail.FullName);
			StringAssert.Contains (".DataType.SdkData.", detail.FullName);
		}

		[Test]
		public void ConcreteErasedSlotsDoNotCollapseTheirTypedSiblingOverloads ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <class name="String" visibility="public" extends="java.lang.Object" />
				</package><package name="example">
				  <class name="Value" visibility="public" extends="java.lang.Object" />
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				    <method name="get" visibility="public" abstract="true" return="java.lang.Object" jni-signature="(Ljava/lang/Object;)Ljava/lang/Object;">
				      <parameter name="key" type="java.lang.Object" />
				    </method>
				  </class>
				  <class name="Leaf" visibility="public" extends="example.Base">
				    <method name="get" visibility="public" return="example.Value" jni-signature="(Ljava/lang/Object;)Lexample/Value;">
				      <parameter name="key" type="java.lang.Object" />
				    </method>
				    <method name="get" visibility="public" return="example.Value" jni-signature="(Ljava/lang/String;)Lexample/Value;"
				      managed-bridge-signatures="(Ljava/lang/Object;)Ljava/lang/Object;">
				      <parameter name="key" type="java.lang.String" />
				    </method>
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var methods = types.Single (t => t.JavaName == "example.Leaf").Methods.Where (m => m.JavaName == "get").ToArray ();
			Assert.AreEqual (2, methods.Length);
			Assert.AreEqual (2, methods.Select (m => m.Parameters [0].Type).Distinct ().Count ());
			Assert.IsTrue (methods.Any (m => m.Parameters [0].Type == "string"));
		}

		[Test]
		public void PhysicalObjectBridgesKeepInheritedAbstractSlotsInsteadOfUnitMethods ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				    <method name="parse" visibility="public" abstract="true" return="java.lang.Object" jni-signature="()Ljava/lang/Object;" />
				  </class>
				  <class name="Leaf" visibility="public" extends="example.Base">
				    <method name="parse" visibility="public" return="void" jni-signature="()V"
				      managed-bridge-signatures="()Ljava/lang/Object;" />
				    <method name="parse" visibility="public" return="java.lang.Object" jni-signature="()Ljava/lang/Object;"
				      managed-bridge-signatures="()Ljava/lang/Object;" />
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var methods = types.Single (t => t.JavaName == "example.Leaf").Methods.ToArray ();
			Assert.AreEqual ("Parse", methods.Single (m => m.JniSignature == "()Ljava/lang/Object;").Name);
			StringAssert.StartsWith ("ParseNative", methods.Single (m => m.JniSignature == "()V").Name);
		}

		[Test]
		public void ReferencedCanonicalPeersAreNotRegeneratedOrErasedFromGenericSignatures ()
		{
			options.ExperimentalSemanticModel = true;
			var source = System.Xml.Linq.XDocument.Parse ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="org.json">
				  <class name="JSONObject" visibility="public" extends="java.lang.Object" />
				</package><package name="example">
				  <class name="Container" visibility="public" extends="java.lang.Object">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <method name="read" visibility="public" return="org.json.JSONObject" jni-signature="()Lorg/json/JSONObject;" />
				  </class>
				</package></api>
				""");
			var canonical = new ClassGen (new GenBaseSupport {
				Name = "JSONObject", FullName = "Org.Json.JSONObject", Namespace = "Org.Json",
				PackageName = "org.json", JavaSimpleName = "JSONObject",
				Visibility = "public", IsAcw = true, IsGeneratable = false,
				ReferenceAssemblyName = "Mono.Android",
			}) { BaseType = "java.lang.Object" };
			options.SymbolTable.AddType (canonical);
			var types = XmlApiImporter.Parse (source, options);
			var owners = new System.Collections.Generic.List<BindingReferencedOwner> ();
			foreach (var type in types)
				Xamarin.Android.Binder.CodeGenerator.AddTypeToTable (options, type, owners);
			Assert.AreSame (canonical, options.SymbolTable.Lookup ("org.json.JSONObject"));
			Assert.IsFalse (types.Single (t => t.JavaName == "org.json.JSONObject").IsGeneratable);
			Assert.AreEqual (new BindingReferencedOwner ("org/json/JSONObject", "Org.Json.JSONObject", "Mono.Android"), owners.Single ());
			foreach (var type in types) {
				type.Validate (options, new GenericParameterDefinitionList (), generator.Context);
				type.FillProperties ();
			}
			var projection = new GenericFacadeProjection (source, types, options);
			var output = projection.GenerateType ("example.Container");
			StringAssert.Contains ("Org.Json.JSONObject", output);
			StringAssert.DoesNotContain ("unavailable-or-inaccessible-peer", string.Join ("\n", projection.Decisions));
		}

		[Test]
		public void ClrHidingBaseMembersCannotBecomeOverrideTargets ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Value" visibility="public" extends="java.lang.Object" />
				    <class name="Base" visibility="public" extends="java.lang.Object">
				      <method name="read" visibility="public" final="true" return="example.Value" />
				    </class>
				    <class name="Middle" visibility="public" abstract="true" extends="example.Base">
				      <method name="read" visibility="public" return="java.lang.Object" />
				    </class>
				    <class name="Leaf" visibility="public" extends="example.Middle">
				      <method name="read" visibility="public" return="java.lang.Object" />
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			Assert.AreEqual ("none", types.Single (t => t.Name == "Leaf").Methods.Single (m => m.JavaName == "read").ManagedOverride);
		}

		[Test]
		public void CovariantNativeGettersRetainTheBasePropertyBridge ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Value" visibility="public" extends="java.lang.Object" />
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="getValue" visibility="public" abstract="true" return="java.lang.Object" jni-signature="()Ljava/lang/Object;" />
				    </class>
				    <class name="Middle" visibility="public" abstract="true" extends="example.Base">
				      <method name="getValue" visibility="public" return="example.Value" jni-signature="()Lexample/Value;"
				        managed-bridge-signatures="()Ljava/lang/Object;" />
				    </class>
				  </package>
				</api>
				""");
			var middle = types.Single (t => t.Name == "Middle");
			var getter = middle.Properties.Single ().Getter;
			middle.Properties.Clear ();
			middle.Methods.Add (getter);
			ManagedContractProjection.Apply (types, options);
			Assert.IsTrue (middle.Properties.Any (p => p.Name == "Value" && !p.Getter.IsAbstract &&
				p.Getter.JniSignature == "()Ljava/lang/Object;"));
		}

		[Test]
		public void InterfaceMethodAndPropertyProjectionsShareOneNativeCallback ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="Parent" visibility="public">
				    <method name="getValue" visibility="public" abstract="true" return="int" jni-signature="()I" />
				  </interface>
				  <interface name="Child" visibility="public">
				    <implements name="example.Parent" name-generic-aware="example.Parent" />
				    <method name="getValue" visibility="public" abstract="true" return="int" jni-signature="()I" />
				  </interface>
				</package></api>
				""");
			var child = types.Single (t => t.Name == "IChild");
			var method = child.Properties.Single ().Getter;
			child.Properties.Clear ();
			child.Methods.Add (method);
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (child);
			var fields = System.Text.RegularExpressions.Regex.Matches (output,
				@"static (?:global::)?(?:System\.)?Delegate\?? " + method.GetEscapedCallbackName (options) + @";");
			Assert.AreEqual (1, fields.Count, output);
		}

		[Test]
		public void CovariantGenericImplementationsDoNotCreateReturnOnlyOverloads ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <typeParameters><typeParameter name="T" /></typeParameters>
				      <method name="copy" visibility="public" abstract="true" return="example.Base&lt;T&gt;" />
				    </class>
				    <class name="Middle" visibility="public" abstract="true" extends="example.Base" extends-generic-aware="example.Base&lt;T&gt;">
				      <typeParameters><typeParameter name="T" /></typeParameters>
				      <method name="copy" visibility="public" abstract="true" return="example.Middle&lt;T&gt;" />
				    </class>
				    <class name="Leaf" visibility="public" extends="example.Middle" extends-generic-aware="example.Middle&lt;java.lang.Object&gt;">
				      <method name="copy" visibility="public" return="example.Leaf" />
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			Assert.AreEqual (1, types.Single (t => t.Name == "Leaf").Methods.Count (m => m.JavaName == "copy"));
		}

		[Test]
		public void OrdinaryNativeGettersRetainInheritedAbstractPropertyAliases ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="getValue" visibility="public" abstract="true" return="int" jni-signature="()I" />
				    </class>
				    <class name="Middle" visibility="public" abstract="true" extends="example.Base">
				      <method name="getValue" visibility="public" return="int" jni-signature="()I" />
				    </class>
				    <class name="Leaf" visibility="public" extends="example.Middle" />
				  </package>
				</api>
				""");
			var middle = types.Single (t => t.Name == "Middle");
			var getter = middle.Properties.Single ().Getter;
			middle.Properties.Clear ();
			middle.Methods.Add (getter);
			ManagedContractProjection.Apply (types, options);
			Assert.IsFalse (getter.IsOverride);
			Assert.IsTrue (middle.Properties.Any (p => p.Name == "Value" && !p.Getter.IsAbstract));
		}

		[Test]
		public void IncompatibleGenericPropertyAccessorsStaySeparateCallableMethods ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" extends="java.lang.Object" />
				    <class name="Child" visibility="public" extends="example.Base" />
				    <interface name="Contract" visibility="public">
				      <method name="getValues" visibility="public" abstract="true" return="java.util.Collection&lt;example.Child&gt;" />
				      <method name="setValues" visibility="public" abstract="true" return="void">
				        <parameter name="values" type="java.util.Collection&lt;example.Child&gt;" />
				      </method>
				    </interface>
				  </package>
				</api>
				""");
			var contract = types.Single (t => t.Name == "IContract");
			var symbol = options.SymbolTable.Lookup ("java.util.Collection<example.Base>", contract.TypeParameters);
			Assert.IsNotNull (symbol);
			Assert.IsTrue (symbol.Validate (options, contract.TypeParameters, generator.Context));
			contract.Properties.Single ().Getter.RetVal.SetManagedReferenceType (symbol);
			ManagedContractProjection.Apply (types, options);
			Assert.IsEmpty (contract.Properties);
			Assert.AreEqual (2, contract.Methods.Count);
		}

		[Test]
		public void ErasedGenericBoundsCannotLeakInternalPeerTypes ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Internal" visibility="internal" />
				    <class name="Owner" visibility="public" extends="java.lang.Object">
				      <typeParameters><typeParameter name="T" classBound="example.Internal" /></typeParameters>
				      <method name="getValue" visibility="public" return="T" jni-signature="()Lexample/Internal;" />
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			Assert.AreEqual ("Java.Lang.Object", types.Single (t => t.Name == "Owner").Properties.Single ().Getter.RetVal.FullName);
		}

		[Test]
		public void SealedClassesCannotAcquireVirtualPropertiesFromProjectionOverrides ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example"><class name="Owner" visibility="public" final="true" extends="java.lang.Object">
				    <method name="getValue" visibility="public" return="int" jni-signature="()I" />
				  </class></package>
				</api>
				""");
			var owner = types.Single (t => t.Name == "Owner");
			owner.Properties.Single ().Getter.ManagedOverride = "virtual";
			Assert.IsFalse (new BoundProperty (owner, owner.Properties.Single (), options).IsVirtual);
		}

		[Test]
		public void ErasedNativeOverloadsKeepDistinctNamesAcrossOverrideFamilies ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="parse" visibility="public" abstract="true" return="int" jni-signature="(Ljava/io/InputStream;)I">
				        <parameter name="value" type="java.io.InputStream" />
				      </method>
				      <method name="parse" visibility="public" abstract="true" return="int" jni-signature="(Ljava/io/OutputStream;)I">
				        <parameter name="value" type="java.io.OutputStream" />
				      </method>
				    </class>
				    <class name="Child" visibility="public" extends="example.Base">
				      <method name="parse" visibility="public" return="int" jni-signature="(Ljava/io/InputStream;)I">
				        <parameter name="value" type="java.io.InputStream" />
				      </method>
				      <method name="parse" visibility="public" return="int" jni-signature="(Ljava/io/OutputStream;)I">
				        <parameter name="value" type="java.io.OutputStream" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var basis = types.Single (t => t.Name == "Base");
			var child = types.Single (t => t.Name == "Child");
			Assert.AreEqual (2, basis.Methods.Select (m => m.Name).Distinct ().Count ());
			foreach (var method in child.Methods)
				Assert.AreEqual (basis.Methods.Single (m => m.JniSignature == method.JniSignature).Name, method.Name);
		}

		[Test]
		public void CollidingOpaqueConstructorsRetainEveryNativeEntryThroughFactories ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="HiddenOne" visibility="internal" extends="java.lang.Object" />
				    <class name="HiddenTwo" visibility="internal" extends="java.lang.Object" />
				    <class name="Owner" visibility="public" extends="java.lang.Object">
				      <constructor name="Owner" visibility="public"><parameter name="value" type="example.HiddenOne" /></constructor>
				      <constructor name="Owner" visibility="public"><parameter name="value" type="example.HiddenTwo" /></constructor>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Owner"));
			StringAssert.Contains ("CreateNative", output);
			StringAssert.Contains ("__Constructor", output);
			StringAssert.Contains ("(Lexample/HiddenOne;)V", output);
			StringAssert.Contains ("(Lexample/HiddenTwo;)V", output);
		}

		[Test]
		public void ConcreteNativeClassesRetainMissingAbstractClrDeclarations ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="read" visibility="public" abstract="true" return="int" jni-signature="()I" />
				    </class>
				    <class name="Child" visibility="public" extends="example.Base" />
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			StringAssert.Contains ("override unsafe int Read", GetGeneratedTypeOutput (types.Single (t => t.Name == "Child")));
		}

		[Test]
		public void SyntheticVoidBridgesCanImplementAbstractVoidContracts ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" />
				  <class name="Void" visibility="public" extends="java.lang.Object" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="report" visibility="protected" abstract="true" return="void" jni-signature="(I)V">
				        <parameter name="value" type="int" />
				      </method>
				    </class>
				    <class name="Child" visibility="public" extends="example.Base">
				      <method name="report" visibility="protected" return="java.lang.Void" jni-signature="(I)Ljava/lang/Void;"
				        managed-bridge-signatures="(I)V"><parameter name="value" type="int" /></method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var child = types.Single (t => t.Name == "Child");
			Assert.IsTrue (child.Methods.Any (m => m.IsOverride && m.IsVoid));
			Assert.AreEqual (2, child.Methods.Select (m => m.Name).Distinct ().Count ());
		}
	}
}
