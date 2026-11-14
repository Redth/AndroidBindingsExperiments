using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using generator.SourceWriters;
using Java.Interop.Tools.Generator.Semantics;
using MonoDroid.Generation;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class ManagedContractProjectionTests : CodeGeneratorTestBase
	{
		[Test]
		public void PublisherInfrastructureKeepsNativeDescriptorsAndExplicitCallbackContracts ()
		{
			options.ExperimentalSemanticModel = true;
			var facts = XDocument.Parse ("""
				<api><package name="java.lang"><class name="Object" visibility="public" jni-signature="Ljava/lang/Object;" /></package>
				  <package name="com.google.android.gms.example">
				    <class name="zzValue" visibility="public" extends="java.lang.Object" jni-signature="Lcom/google/android/gms/example/zzValue;" />
				    <interface name="zzCallback" visibility="public" obfuscated="false" jni-signature="Lcom/google/android/gms/example/zzCallback;">
				      <method name="run" visibility="public" abstract="true" jni-signature="()V" return="void" />
				    </interface>
				    <class name="Api" visibility="public" extends="java.lang.Object" jni-signature="Lcom/google/android/gms/example/Api;">
				      <method name="getValue" visibility="public" jni-signature="()Lcom/google/android/gms/example/zzValue;"
				        return="com.google.android.gms.example.zzValue" />
				      <method name="setCallback" visibility="public" jni-signature="(Lcom/google/android/gms/example/zzCallback;)V" return="void">
				        <parameter name="callback" type="com.google.android.gms.example.zzCallback" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			new BindingAccessibilityProjection (facts).Apply (facts);
			var types = ParseApiDefinition (facts.ToString ());
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.JavaName == "com.google.android.gms.example.Api"));
			StringAssert.Contains ("Java.Lang.Object", output);
			StringAssert.Contains ("()Lcom/google/android/gms/example/zzValue;", output);
			StringAssert.Contains ("IZzCallback", output);
			StringAssert.DoesNotContain ("Com.Google.Android.Gms.Example.ZzValue", output);
		}

		const string api = """
			<api>
			  <package name="java.lang">
			    <class name="Object" visibility="public" />
			  </package>
			  <package name="example">
			    <class name="Result" visibility="public" extends="java.lang.Object" />
			    <class name="ChildResult" visibility="public" extends="example.Result" />
			    <class name="Base" visibility="public" extends="java.lang.Object">
			      <method name="run" managedName="Derived" return="java.lang.Object" visibility="public" final="false" />
			      <method name="getState" managedName="ReadState" return="java.lang.Object" visibility="public" final="false" />
			      <method name="read" return="example.Result" visibility="public" final="false" />
			      <method name="mode" return="int" visibility="public" final="false" />
			    </class>
			    <class name="Derived" visibility="public" extends="example.Base">
			      <method name="run" managedName="Derived" return="java.lang.Object" visibility="public" final="false" />
			      <method name="getState" return="java.lang.Object" visibility="public" final="false" />
			      <method name="read" return="example.ChildResult" visibility="public" final="false" />
			      <method name="mode" return="int" visibility="public" final="false" />
			    </class>
			  </package>
			</api>
			""";

		[Test]
		public void OverrideChainUsesOneCollisionFreeManagedName ()
		{
			var types = ParseApiDefinition (api);
			ManagedContractProjection.Apply (types, options);
			var methods = types.SelectMany (t => t.Methods).Where (m => m.JavaName == "run").ToArray ();

			Assert.AreEqual (2, methods.Length);
			Assert.IsTrue (methods.All (m => m.Name == "InvokeDerived"));
			Assert.IsTrue (methods.Single (m => m.DeclaringType.Name == "Derived").IsOverride);
			Assert.IsTrue (methods.All (m => m.JniSignature == "()Ljava/lang/Object;"));
		}

		[Test]
		public void InheritedMethodContractDoesNotBecomeAProperty ()
		{
			var types = ParseApiDefinition (api);
			var derived = types.Single (t => t.Name == "Derived");
			Assert.IsTrue (derived.Properties.Any (p => p.Name == "State"));

			ManagedContractProjection.Apply (types, options);

			Assert.IsFalse (derived.Properties.Any (p => p.Name == "State"));
			var method = derived.Methods.Single (m => m.JavaName == "getState");
			Assert.AreEqual ("ReadState", method.Name);
			Assert.IsTrue (method.IsOverride);
		}

		[Test]
		public void RealManagedCovarianceRetainsTheMoreSpecificReturn ()
		{
			var types = ParseApiDefinition (api);
			ManagedContractProjection.Apply (types, options);
			var method = types.Single (t => t.Name == "Derived").Methods.Single (m => m.JavaName == "read");

			Assert.IsTrue (method.IsOverride);
			Assert.AreEqual ("Example.ChildResult", method.RetVal.FullName);
			Assert.AreEqual ("()Lexample/ChildResult;", method.JniSignature);
		}

		[Test]
		public void ImportedEnumReturnIsMatchedByNativeSlot ()
		{
			var types = ParseApiDefinition (api);
			types.Single (t => t.Name == "Base").Methods.Single (m => m.JavaName == "mode")
				.RetVal.SetGeneratedEnumType ("Example.Mode");

			ManagedContractProjection.Apply (types, options);
			var method = types.Single (t => t.Name == "Derived").Methods.Single (m => m.JavaName == "mode");

			Assert.AreEqual ("Example.Mode", method.RetVal.FullName);
			Assert.AreEqual ("()I", method.JniSignature);
		}

		[Test]
		public void MethodAndPropertyNativeSlotsKeepDistinctManagedNames ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example"><class name="Container" visibility="public" extends="java.lang.Object">
				  <method name="size" return="int" visibility="public" />
				  <method name="getSize" return="int" visibility="public" />
				</class></package></api>
				""");
			var type = types.Single (t => t.Name == "Container");
			type.Methods.Single ().Name = "Size";

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Size", type.Methods.Single ().Name);
			Assert.AreEqual ("SizeValue", type.Properties.Single ().Name);
			Assert.AreEqual ("()I", type.Properties.Single ().Getter.JniSignature);
		}

		[TestCase (false)]
		[TestCase (true)]
		public void InterfaceFamiliesRetainReferencedEnumParameters (bool defaultMethod)
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="Base" visibility="public">
				    <method name="apply" return="void" visibility="public" abstract="true"><parameter name="mode" type="int" /></method>
				  </interface>
				  <interface name="Derived" visibility="public">
				    <implements name="example.Base" name-generic-aware="example.Base" />
				    <method name="apply" return="void" visibility="public" abstract="true"><parameter name="mode" type="int" /></method>
				  </interface>
				</package></api>
				""");
			var basis = types.Single (t => t.Name == "IBase");
			basis.Methods.Single ().Parameters [0].SetGeneratedEnumType ("Example.Mode");
			var derived = types.Single (t => t.Name == "IDerived");
			var method = derived.Methods.Single ();
			method.IsInterfaceDefaultMethod = defaultMethod;
			method.IsAbstract = !defaultMethod;
			method.OverriddenInterfaceMethod = null;

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Example.Mode", method.Parameters [0].Type);
			Assert.AreEqual ("(I)V", method.JniSignature);
			if (defaultMethod) {
				options.SupportDefaultInterfaceMethods = true;
				Assert.AreSame (basis.Methods.Single (), method.OverriddenInterfaceMethod);
				Assert.AreEqual (options.GetOutputName (basis.FullName),
					new BoundMethod (derived, method, options, false).ExplicitInterfaceImplementation);
			}
		}

		[TestCase (false)]
		[TestCase (true)]
		public void PublicGenericBoundsRetainDistinctConstructorAndReturnTypes (bool semantic)
		{
			options.ExperimentalSemanticModel = semantic;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Vector" visibility="public" extends="java.lang.Object" />
				  <class name="Animation" visibility="public" extends="java.lang.Object">
				    <typeParameters>
				      <typeParameter name="T" />
				      <typeParameter name="V"><genericConstraints><genericConstraint type="example.Vector" /></genericConstraints></typeParameter>
				    </typeParameters>
				    <constructor name="Animation" visibility="public"><parameter name="value" type="T" /></constructor>
				    <constructor name="Animation" visibility="public"><parameter name="vector" type="V" /></constructor>
				    <method name="read" return="V" visibility="public" />
				  </class>
				</package></api>
				""");
			var type = types.OfType<ClassGen> ().Single (t => t.Name == "Animation");
			var vector = type.Ctors.Single (c => c.Parameters [0].Name == "vector");
			var method = type.Methods.Single ();

			Assert.AreEqual (semantic ? "Example.Vector" : "Java.Lang.Object", vector.Parameters [0].Type);
			Assert.AreEqual (semantic ? "Example.Vector" : "Java.Lang.Object", method.RetVal.FullName);
			Assert.AreEqual ("(Lexample/Vector;)V", vector.JniSignature);
			Assert.AreEqual ("()Lexample/Vector;", method.JniSignature);
			Assert.AreEqual ("Java.Lang.Object", type.Ctors.Single (c => c.Parameters [0].Name == "value").Parameters [0].Type);
			Assert.IsTrue (new BoundMethod (type, method, options, false).Body.Any (s =>
				s.Contains ("GetObject<" + options.GetOutputName (semantic ? "Example.Vector" : "Java.Lang.Object") + ">")));
			if (semantic) {
				var bound = types.Single (t => t.Name == "Vector");
				bound.FullName = "Example.VectorType";
				bound.Name = "VectorType";
				Assert.AreEqual ("Example.VectorType", vector.Parameters [0].Type);
				Assert.AreEqual ("Example.VectorType", method.RetVal.FullName);
				Assert.AreEqual ("()Lexample/Vector;", method.JniSignature);
			}
		}

		[Test]
		public void ReferencedPrimitiveParametersRetainTheActualManagedMarshaler ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Base" visibility="public" extends="java.lang.Object">
				    <method name="apply" return="void" visibility="public" final="false"><parameter name="color" type="int" /></method>
				  </class>
				  <class name="Derived" visibility="public" extends="example.Base">
				    <method name="apply" return="void" visibility="public" final="false"><parameter name="color" type="int" /></method>
				  </class>
				</package></api>
				""");
			types.Single (t => t.Name == "Base").Methods.Single ().Parameters [0].SetManagedReferenceType (new ColorSymbol ());

			ManagedContractProjection.Apply (types, options);

			var type = types.Single (t => t.Name == "Derived");
			var method = type.Methods.Single ();
			Assert.AreEqual ("Android.Graphics.Color", method.Parameters [0].Type);
			Assert.AreEqual ("(I)V", method.JniSignature);
			Assert.IsTrue (new BoundMethod (type, method, options, false).Body.Any (s => s.Contains ("color.ToArgb ()")));
		}

		[TestCase (false)]
		[TestCase (true)]
		public void InheritedCharSequenceContractsRetainManagedEnumeration (bool semantic)
		{
			options.ExperimentalSemanticModel = semantic;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <interface name="CharSequence" visibility="public" />
				</package><package name="example">
				  <interface name="Text" visibility="public">
				    <implements name="java.lang.CharSequence" name-generic-aware="java.lang.CharSequence" />
				  </interface>
				  <class name="TextValue" visibility="public" extends="java.lang.Object">
				    <implements name="example.Text" name-generic-aware="example.Text" />
				    <method name="length" return="int" visibility="public" />
				    <method name="charAt" return="char" visibility="public"><parameter name="index" type="int" /></method>
				  </class>
				</package></api>
				""");
			var type = types.OfType<ClassGen> ().Single (t => t.Name == "TextValue");
			var output = new BoundClass (type, options, generator.Context, new GenerationInfo ("", "", "Test"));

			Assert.AreEqual (semantic ? 1 : 0, output.Methods.OfType<CharSequenceEnumeratorMethod> ().Count ());
			Assert.AreEqual (semantic ? 1 : 0, output.Methods.OfType<CharSequenceGenericEnumeratorMethod> ().Count ());
		}

		[Test]
		public void StaticFactoriesAndFluentInstanceMethodsKeepDistinctNativeSlots ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example"><class name="Dimension" visibility="public" extends="java.lang.Object">
				  <method name="Fixed" return="example.Dimension" visibility="public" static="true"><parameter name="value" type="int" /></method>
				  <method name="fixed" return="example.Dimension" visibility="public"><parameter name="value" type="int" /></method>
				</class></package></api>
				""");
			var type = types.Single (t => t.Name == "Dimension");

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("CreateFixed", type.Methods.Single (m => m.IsStatic).Name);
			Assert.AreEqual ("Fixed", type.Methods.Single (m => !m.IsStatic).Name);
			Assert.IsTrue (type.Methods.All (m => m.JniSignature == "(I)Lexample/Dimension;"));
		}

		[Test]
		public void NativeStringOverloadsTakePriorityOverDuplicateConveniences ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <class name="String" visibility="public" extends="java.lang.Object" />
				  <interface name="CharSequence" visibility="public" />
				</package><package name="example">
				  <class name="Formatter" visibility="public" extends="java.lang.Object">
				    <method name="wrap" return="java.lang.CharSequence" visibility="public"><parameter name="text" type="java.lang.CharSequence" /></method>
				    <method name="wrap" return="java.lang.String" visibility="public"><parameter name="text" type="java.lang.String" /></method>
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var type = types.OfType<ClassGen> ().Single (t => t.Name == "Formatter");
			var output = new BoundClass (type, options, generator.Context, new GenerationInfo ("", "", "Test"));

			Assert.AreEqual (1, output.Methods.Count (m => m.Name == "Wrap"));
			Assert.IsEmpty (output.Methods.OfType<BoundMethodStringOverload> ());
			Assert.AreEqual (2, type.Methods.Count);
		}

		[Test]
		public void ErasedCharSequenceParametersDoNotGenerateIdenticalConveniences ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <interface name="CharSequence" visibility="public" />
				</package><package name="example">
				  <class name="Consumer" visibility="public" extends="java.lang.Object">
				    <method name="consume" return="void" visibility="public"><parameter name="text" type="java.lang.CharSequence" managedType="Java.Lang.Object" /></method>
				  </class>
				</package></api>
				""");
			var type = types.OfType<ClassGen> ().Single (t => t.Name == "Consumer");
			var method = type.Methods.Single ();

			var output = new BoundClass (type, options, generator.Context, new GenerationInfo ("", "", "Test"));

			Assert.IsTrue (method.Parameters.HasCharSequence);
			Assert.AreEqual ("(Ljava/lang/CharSequence;)V", method.JniSignature);
			Assert.AreEqual (1, output.Methods.Count (m => m.Name == "Consume"));
			Assert.IsEmpty (output.Methods.OfType<BoundMethodStringOverload> ());
		}

		[Test]
		public void SetterOverridesRetainTheInheritedManagedPropertyShape ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <class name="Base" visibility="public" extends="java.lang.Object">
				    <method name="getValue" return="int" visibility="public" final="false" />
				    <method name="setValue" return="void" visibility="public" final="false"><parameter name="value" type="int" /></method>
				  </class>
				  <class name="Derived" visibility="public" extends="example.Base">
				    <method name="setValue" return="void" visibility="public" final="false"><parameter name="value" type="int" /></method>
				  </class>
				</package></api>
				""");
			var derived = types.OfType<ClassGen> ().Single (t => t.Name == "Derived");

			ManagedContractProjection.Apply (types, options);

			var property = derived.Properties.Single ();
			Assert.AreEqual ("Value", property.Name);
			Assert.AreEqual ("()I", property.Getter.JniSignature);
			Assert.IsNotNull (property.Setter);
			Assert.AreEqual ("(I)V", property.Setter.JniSignature);
			Assert.IsEmpty (derived.Methods);
			var output = new BoundClass (derived, options, generator.Context, new GenerationInfo ("", "", "Test"));
			Assert.IsTrue (output.Properties.Single (p => p.Name == "Value").IsOverride);
		}

		[Test]
		public void NewSettersDoNotChangeInheritedReadOnlyPropertyContracts ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Base" visibility="public" extends="java.lang.Object">
				    <method name="getValue" return="int" visibility="protected" final="false" />
				  </class>
				  <class name="Derived" visibility="public" extends="example.Base">
				    <method name="getValue" return="int" visibility="public" final="false" />
				    <method name="setValue" return="void" visibility="public"><parameter name="value" type="int" /></method>
				  </class>
				</package></api>
				""");
			var derived = types.OfType<ClassGen> ().Single (t => t.Name == "Derived");
			derived.Properties.Single ().Getter.RetVal.NotNull = true;

			ManagedContractProjection.Apply (types, options);

			var output = new BoundClass (derived, options, generator.Context, new GenerationInfo ("", "", "Test"));
			var property = output.Properties.Single (p => p.Name == "Value");
			Assert.IsTrue (property.IsProtected);
			Assert.IsTrue (property.IsOverride);
			Assert.IsFalse (property.HasSet);
			var setter = derived.Methods.Single ();
			Assert.AreEqual ("SetValue", setter.Name);
			Assert.AreEqual ("(I)V", setter.JniSignature);
			Assert.IsTrue (output.Methods.Single (m => m.Name == "SetValue").IsPublic);
		}

		[Test]
		public void MultipleListenerConnectorsRetainDistinctEventConveniences ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <interface name="ChangedListener" visibility="public">
				    <method name="onChanged" return="void" visibility="public" abstract="true" />
				  </interface>
				  <class name="Publisher" visibility="public" extends="java.lang.Object">
				    <method name="addChangedListener" return="void" visibility="public"><parameter name="listener" type="example.ChangedListener" /></method>
				    <method name="removeChangedListener" return="void" visibility="public"><parameter name="listener" type="example.ChangedListener" /></method>
				    <method name="setChangedListener" return="void" visibility="public"><parameter name="listener" type="example.ChangedListener" /></method>
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var publisher = types.OfType<ClassGen> ().Single (t => t.Name == "Publisher");
			var output = new BoundClass (publisher, options, generator.Context, new GenerationInfo ("", "", "Test"));

			Assert.AreEqual (2, output.Events.Count);
			CollectionAssert.AreEquivalent (new [] { "Changed", "ChangedViaSetChangedListener" }, output.Events.Select (e => e.Name));
		}

		[Test]
		public void OverloadedListenerConnectorsRetainIndependentImplementorFields ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="FirstListener" visibility="public">
				    <method name="onChanged" return="void" visibility="public" abstract="true" />
				  </interface>
				  <interface name="SecondListener" visibility="public">
				    <method name="onChanged" return="void" visibility="public" abstract="true" />
				  </interface>
				  <class name="Publisher" visibility="public" extends="java.lang.Object">
				    <method name="setChangedListener" return="void" visibility="public"><parameter name="listener" type="example.FirstListener" /></method>
				    <method name="setChangedListener" return="void" visibility="public"><parameter name="listener" type="example.SecondListener" /></method>
				  </class>
				</package></api>
				""");
			var type = types.OfType<ClassGen> ().Single (t => t.Name == "Publisher");
			ManagedContractProjection.Apply (types, options);
			var output = new BoundClass (type, options, generator.Context, new GenerationInfo ("", "", "Test"));

			var names = output.Fields.OfType<WeakImplementorField> ().Select (f => f.Name).ToArray ();
			CollectionAssert.AreEquivalent (new [] { "weak_implementor_SetChangedListener", "weak_implementor_SetChangedListener2" }, names);
			Assert.AreEqual (2, output.Events.Count);
			foreach (var name in names)
				Assert.AreEqual (1, output.Events.Count (e => e.AddBody.Any (s => s.Contains ("ref " + name + ","))));
			foreach (var listener in types.OfType<InterfaceGen> ())
				Assert.IsTrue (output.Events.Any (e => e.RemoveBody.Any (s =>
					s.Contains ("(" + options.GetOutputName (listener.FullName) + options.NullableOperator + ") null"))));
			Assert.AreEqual (2, type.Methods.Count);
		}

		[Test]
		public void ReferencedPrimitiveAliasesRetainTheirNativeMarshaler ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition (api);
			types.Single (t => t.Name == "Base").Methods.Single (m => m.JavaName == "mode")
				.RetVal.SetManagedReferenceType (new ColorSymbol ());

			ManagedContractProjection.Apply (types, options);
			var method = types.Single (t => t.Name == "Derived").Methods.Single (m => m.JavaName == "mode");

			Assert.AreEqual ("Android.Graphics.Color", method.RetVal.FullName);
			Assert.AreEqual ("()I", method.JniSignature);
			StringAssert.Contains ("new global::Android.Graphics.Color", method.RetVal.FromNative (options, "__value", false));
		}

		[Test]
		public void ReferencedPropertyAliasesPropagateToOverridesAndLookup ()
		{
			var types = ParseApiDefinition (api);
			var basis = types.Single (t => t.Name == "Base");
			var getter = basis.Methods.Single (m => m.JavaName == "getState");
			basis.Methods.Remove (getter);
			basis.Properties.Add (new Property ("RawState") { Getter = getter });
			basis.RefreshPropertyLookup ();
			var derived = types.Single (t => t.Name == "Derived");

			ManagedContractProjection.Apply (types, options);

			Assert.IsNotNull (derived.GetPropertyByName ("RawState", false));
			Assert.IsNull (derived.GetPropertyByName ("State", false));
		}

		[Test]
		public void ImplicitAbstractInterfaceImplementationDefinesABaseOverrideSlot ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <interface name="Reader" visibility="public">
				    <method name="read" return="java.lang.Object" visibility="public" abstract="true" />
				  </interface>
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.Reader" name-generic-aware="example.Reader" />
				  </class>
				  <class name="Implementation" visibility="public" extends="example.Base">
				    <method name="read" return="java.lang.Object" visibility="public" final="true" />
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var method = types.Single (t => t.Name == "Implementation").Methods.Single (m => m.JavaName == "read");
			Assert.IsTrue (method.IsOverride);
			Assert.AreEqual ("override", method.ManagedOverride);
			Assert.IsTrue (method.IsFinal);
		}

		[Test]
		public void GenericArgumentsFollowLateTypeNameAllocation ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <class name="Container" visibility="public" extends="java.lang.Object">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				  </class>
				  <class name="Value" visibility="public" extends="java.lang.Object" />
				</package><package name="example.Value">
				  <class name="Marker" visibility="public" extends="java.lang.Object" />
				</package></api>
				""");
			var symbol = new GenericSymbol (types.Single (t => t.Name == "Container"), "<example.Value>");
			var collection = options.SymbolTable.Lookup ("java.util.List<example.Value>", new GenericParameterDefinitionList ());
			Assert.IsTrue (symbol.Validate (options, new GenericParameterDefinitionList (), generator.Context));
			Assert.IsTrue (collection.Validate (options, new GenericParameterDefinitionList (), generator.Context));
			Assert.AreEqual ("Example.Container<Example.Value>", symbol.GetGenericType (null));
			Assert.AreEqual ("System.Collections.Generic.IList<Example.Value>", collection.FullName);

			ManagedContractProjection.AllocateTypeNames (types);

			Assert.AreEqual ("Example.Container<Example.ValueType>", symbol.GetGenericType (null));
			Assert.AreEqual ("System.Collections.Generic.IList<Example.ValueType>", collection.FullName);
			Assert.AreEqual ("Lexample/Container;", symbol.JniName);
		}

		[Test]
		public void MissingGenericInterfaceSlotsBecomeAbstractDeclarationsNotAdapters ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <interface name="Reader" visibility="public">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <method name="read" return="T" visibility="public" abstract="true" />
				  </interface>
				  <class name="Parser" visibility="public" abstract="true" extends="java.lang.Object">
				    <typeParameters><typeParameter name="Message" /></typeParameters>
				    <implements name="example.Reader" name-generic-aware="example.Reader&lt;Message&gt;" />
				  </class>
				</package></api>
				""");
			options.ExperimentalSemanticModel = true;
			var parser = types.OfType<ClassGen> ().Single (t => t.Name == "Parser");
			parser.FixupExplicitImplementation ();
			var contract = types.OfType<InterfaceGen> ().Single ().Methods.Single ();
			Assert.IsTrue (parser.ExplicitlyImplementedInterfaceMethods.Contains (contract.GetSignature ()));

			ManagedContractProjection.Apply (types, options);

			Assert.IsFalse (parser.ExplicitlyImplementedInterfaceMethods.Contains (contract.GetSignature ()));
			Assert.AreEqual (1, new ClassInvokerClass (parser, options).Methods.Count (m => m.Name == "Read"));
			var output = new BoundClass (parser, options, generator.Context, new GenerationInfo ("", "", "Test"));
			Assert.AreEqual (1, output.Methods.Count (m => m.Name == "Read" && m.ExplicitInterfaceImplementation == null));
			Assert.IsTrue (output.Methods.Single (m => m.Name == "Read").IsAbstract);
		}

		[Test]
		public void PackagePrivateInterfacesDoNotDefinePublicManagedReturnContracts ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <interface name="Factory" visibility="">
				    <method name="configure" return="example.Factory" visibility="public" abstract="true" />
				  </interface>
				  <class name="Implementation" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.Factory" name-generic-aware="example.Factory" />
				    <method name="configure" return="java.lang.Object" visibility="public" final="false" />
				  </class>
				</package></api>
				""");
			var method = types.Single (t => t.Name == "Implementation").Methods.Single ();
			var native = method.JniSignature;

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Java.Lang.Object", method.RetVal.FullName);
			Assert.AreEqual (native, method.JniSignature);
			Assert.IsEmpty (method.PublicContractInterfaceMethods);
			var output = new BoundClass ((ClassGen) method.DeclaringType, options, generator.Context, new GenerationInfo ("", "", "Test"));
			Assert.IsEmpty (output.Methods.OfType<ExplicitInterfaceInvokerMethod> ());
			Assert.IsEmpty (new ClassInvokerClass ((ClassGen) method.DeclaringType, options).Methods);
		}

		[Test]
		public void ReferencedErasedMethodsRetainNewTypedInterfaceContracts ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <class name="Buffer" visibility="public" extends="java.lang.Object" />
				  <class name="Base" visibility="public" extends="java.lang.Object">
				    <method name="accept" return="void" visibility="public" final="false"><parameter name="value" type="example.Buffer" /></method>
				  </class>
				  <interface name="Consumer" visibility="public">
				    <method name="accept" return="void" visibility="public" abstract="true"><parameter name="value" type="example.Buffer" /></method>
				  </interface>
				  <class name="Implementation" visibility="public" extends="example.Base">
				    <implements name="example.Consumer" name-generic-aware="example.Consumer" />
				  </class>
				</package></api>
				""");
			var basis = types.Single (t => t.Name == "Base").Methods.Single ();
			basis.Parameters [0].SetManagedReferenceType (options.SymbolTable.Lookup ("java.lang.Object"));
			var implementation = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");
			var contract = types.OfType<InterfaceGen> ().Single ().Methods.Single ();

			ManagedContractProjection.Apply (types, options);

			CollectionAssert.AreEqual (new [] { contract }, implementation.InheritedInterfaceContracts);
			Assert.IsEmpty (basis.PublicContractInterfaceMethods);
			var output = new BoundClass (implementation, options, generator.Context, new GenerationInfo ("", "", "Test"));
			Assert.AreEqual (1, output.Methods.OfType<ExplicitInterfaceInvokerMethod> ().Count ());
			Assert.AreEqual ("(Lexample/Buffer;)V", contract.JniSignature);
		}

		[TestCase (false)]
		[TestCase (true)]
		public void InheritedNativeAccessorsRetainExplicitInterfaceProperties (bool isAbstract)
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Base" visibility="public" extends="java.lang.Object">
				    <method name="getValue" managedName="ReadValue" return="int" visibility="public" />
				    <method name="setValue" managedName="WriteValue" return="void" visibility="public"><parameter name="newValue" type="int" /></method>
				  </class>
				  <interface name="Contract" visibility="public">
				    <method name="getValue" return="int" visibility="public" abstract="true" />
				    <method name="setValue" return="void" visibility="public" abstract="true"><parameter name="newValue" type="int" /></method>
				  </interface>
				  <class name="Implementation" visibility="public" extends="example.Base">
				    <implements name="example.Contract" name-generic-aware="example.Contract" />
				  </class>
				</package></api>
				""");
			var implementation = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");
			implementation.IsAbstract = isAbstract;
			var contract = types.OfType<InterfaceGen> ().Single ().Properties.Single ();

			ManagedContractProjection.Apply (types, options);

			CollectionAssert.AreEqual (new [] { contract }, implementation.InterfacePropertyContracts);
			var output = new BoundClass (implementation, options, generator.Context, new GenerationInfo ("", "", "Test"));
			var property = output.Properties.OfType<InterfaceInvokerProperty> ().Single ();
			Assert.AreEqual (options.GetOutputName ("Example.IContract"), property.ExplicitInterfaceImplementation);
			Assert.IsFalse (property.IsPublic);
			Assert.IsTrue (property.GetBody.Any (s => s.Contains ("getValue.()I")));
			Assert.IsTrue (property.SetBody.Any (s => s.Contains ("setValue.(I)V")));
			Assert.IsTrue (property.SetBody.Any (s => s.Contains ("value")));
			Assert.AreEqual ("newValue", contract.Setter.Parameters [0].Name);
			Assert.IsEmpty (output.Properties.OfType<BoundAbstractProperty> ());
			if (isAbstract)
				Assert.IsEmpty (new ClassInvokerClass (implementation, options).Properties.OfType<BoundProperty> ());
		}

		[Test]
		public void UnimplementedAbstractInterfacePropertiesRemainAbstract ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Contract" visibility="public">
				    <method name="getValue" return="int" visibility="public" abstract="true" />
				  </interface>
				  <class name="Implementation" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.Contract" name-generic-aware="example.Contract" />
				  </class>
				</package></api>
				""");
			var implementation = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");

			ManagedContractProjection.Apply (types, options);

			Assert.IsEmpty (implementation.InterfacePropertyContracts);
			var output = new BoundClass (implementation, options, generator.Context, new GenerationInfo ("", "", "Test"));
			Assert.AreEqual (1, output.Properties.OfType<BoundAbstractProperty> ().Count ());
		}

		[Test]
		public void MatchingManagedPropertiesNeedNoExplicitInterfaceAdapter ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Contract" visibility="public">
				    <method name="getValue" return="int" visibility="public" abstract="true" />
				  </interface>
				  <class name="Implementation" visibility="public" extends="java.lang.Object">
				    <implements name="example.Contract" name-generic-aware="example.Contract" />
				    <method name="getValue" return="int" visibility="public" />
				  </class>
				</package></api>
				""");
			var implementation = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");

			ManagedContractProjection.Apply (types, options);

			Assert.IsEmpty (implementation.InterfacePropertyContracts);
		}

		[Test]
		public void InterfaceInvokersRetainMutableContractsAfterReadOnlyProperties ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="State" visibility="public">
				    <method name="getValue" return="java.lang.Object" visibility="public" abstract="true" />
				  </interface>
				  <interface name="MutableState" visibility="public">
				    <implements name="example.State" name-generic-aware="example.State" />
				    <method name="getValue" return="java.lang.Object" visibility="public" abstract="true" />
				    <method name="setValue" return="void" visibility="public" abstract="true"><parameter name="newValue" type="java.lang.Object" /></method>
				  </interface>
				  <interface name="SpecializedState" visibility="public">
				    <implements name="example.State" name-generic-aware="example.State" />
				    <implements name="example.MutableState" name-generic-aware="example.MutableState" />
				  </interface>
				</package></api>
				""");
			var type = types.OfType<InterfaceGen> ().Single (t => t.Name == "ISpecializedState");
			var mutable = types.OfType<InterfaceGen> ().Single (t => t.Name == "IMutableState").Properties.Single ();

			ManagedContractProjection.Apply (types, options);

			CollectionAssert.AreEqual (new [] { mutable }, type.InheritedPropertyContracts);
			var output = new InterfaceInvokerClass (type, options);
			var property = output.Properties.OfType<InterfaceInvokerProperty> ().Single (p => p.ExplicitInterfaceImplementation != null);
			Assert.AreEqual (options.GetOutputName ("Example.IMutableState"), property.ExplicitInterfaceImplementation);
			Assert.IsFalse (property.IsPublic);
			Assert.IsTrue (property.HasSet);
			Assert.IsTrue (property.SetBody.Any (s => s.Contains ("setValue.(Ljava/lang/Object;)V")));
			Assert.IsTrue (property.SetBody.Any (s => s.Contains (options.GetInterfaceInvokerMembersName (mutable.Setter.DeclaringType))));
		}

		[Test]
		public void SynthesizedAbstractDeclarationsAlwaysHaveConcreteInvokerOverrides ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Operation" visibility="public">
				    <method name="apply" return="int" visibility="public" abstract="true" />
				  </interface>
				  <class name="Implementation" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.Operation" name-generic-aware="example.Operation" />
				  </class>
				</package></api>
				""");
			var contract = types.OfType<InterfaceGen> ().Single ().Methods.Single ();
			contract.IsAbstract = false;
			var type = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");
			ManagedContractProjection.Apply (types, options);
			var output = new BoundClass (type, options, generator.Context, new GenerationInfo ("", "", "Test"));
			var declarations = output.Methods.OfType<BoundMethodAbstractDeclaration> ().Select (m => m.JavaMethod).ToArray ();

			CollectionAssert.AreEqual (new [] { contract }, declarations);
			type.ExplicitlyImplementedInterfaceMethods.Add (contract.GetSignature ());
			var invoker = new ClassInvokerClass (type, options, declarations);
			var method = invoker.Methods.OfType<BoundMethod> ().Single ();
			Assert.AreEqual ("Apply", method.Name);
			Assert.IsTrue (method.IsOverride);
			Assert.IsTrue (method.Body.Any (s => s.Contains ("apply.()I")));
			Assert.IsFalse (contract.IsAbstract);
		}

		[Test]
		public void ArrayOverridesUseTheReferencedManagedElementType ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <class name="Base" visibility="public" extends="java.lang.Object">
				    <method name="accept" return="void" visibility="public" final="false"><parameter name="values" type="int[]" /></method>
				  </class>
				  <class name="Derived" visibility="public" extends="example.Base">
				    <method name="accept" return="void" visibility="public" final="false"><parameter name="values" type="int[]" /></method>
				  </class>
				</package></api>
				""");
			var basis = types.Single (t => t.Name == "Base").Methods.Single ();
			basis.Parameters [0].SetManagedReferenceType (new ArraySymbol (new GeneratedEnumSymbol ("Example.Mode")));
			var method = types.Single (t => t.Name == "Derived").Methods.Single ();
			var native = method.JniSignature;

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Example.Mode[]", method.Parameters [0].Type);
			Assert.AreEqual (native, method.JniSignature);
			Assert.IsTrue (method.IsOverride);
		}

		[Test]
		public void UnrelatedListenersUseDistinctArgumentTypesWithoutRenamingEvents ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="FirstListener" visibility="public">
				    <method name="onChanged" return="void" visibility="public" abstract="true"><parameter name="value" type="int" /></method>
				    <method name="onClosed" return="void" visibility="public" abstract="true" />
				  </interface>
				  <interface name="SecondListener" visibility="public">
				    <method name="onChanged" return="void" visibility="public" abstract="true"><parameter name="value" type="long" /></method>
				    <method name="onClosed" return="void" visibility="public" abstract="true" />
				  </interface>
				</package></api>
				""");
			var listeners = types.OfType<InterfaceGen> ().ToArray ();
			var methods = listeners.Select (i => i.Methods.Single (m => m.JavaName == "onChanged")).ToArray ();
			var descriptors = methods.Select (m => m.JniSignature).ToArray ();

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual (2, listeners.Select (i => i.GetArgsName (i.Methods.Single (m => m.JavaName == "onChanged"))).Distinct ().Count ());
			Assert.IsTrue (methods.All (m => m.Name == "OnChanged" && m.ListenerProjectionName == null));
			CollectionAssert.AreEqual (descriptors, methods.Select (m => m.JniSignature));
		}

		[Test]
		public void ListenerOverloadsKeepTheirMethodsButUseDistinctEventInfrastructure ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="ChangedListener" visibility="public">
				    <method name="onChanged" return="void" visibility="public" abstract="true">
				      <parameter name="value" type="int" />
				    </method>
				    <method name="onChanged" return="void" visibility="public" abstract="true">
				      <parameter name="value" type="long" />
				    </method>
				  </interface>
				</package></api>
				""");
			var listener = types.OfType<InterfaceGen> ().Single ();
			var descriptors = listener.Methods.Select (m => m.JniSignature).ToArray ();

			ManagedContractProjection.Apply (types, options);

			Assert.IsTrue (listener.Methods.All (m => m.Name == "OnChanged"));
			CollectionAssert.AreEquivalent (descriptors, listener.Methods.Select (m => m.JniSignature));
			CollectionAssert.AreEquivalent (new [] { "OnChangedWithInt", "OnChangedWithLong" },
				listener.Methods.Select (m => m.ListenerProjectionName));
			Assert.AreEqual (2, listener.Methods.Select (listener.GetArgsName).Distinct ().Count ());
			Assert.AreEqual (2, listener.Methods.Select (listener.GetEventDelegateName).Distinct ().Count ());
		}

		[Test]
		public void InterfaceMethodsRemainCallableWhenClassesProjectProperties ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <interface name="Reader" visibility="public">
				    <method name="getState" managedName="ReadState" return="java.lang.Object" visibility="public" abstract="true" />
				  </interface>
				  <class name="Implementation" visibility="public" extends="java.lang.Object">
				    <implements name="example.Reader" name-generic-aware="example.Reader" />
				    <method name="getState" return="java.lang.Object" visibility="public" final="false" />
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var klass = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");
			var contract = types.OfType<InterfaceGen> ().Single ().Methods.Single ();
			var output = new BoundClass (klass, options, generator.Context, new GenerationInfo ("", "", "Test"));

			Assert.IsTrue (klass.Properties.Single ().Getter.PublicContractInterfaceMethods.Contains (contract));
			Assert.AreEqual (1, output.Methods.OfType<ExplicitInterfaceInvokerMethod> ().Count (m => m.Name == "ReadState"));
			Assert.AreEqual (1, output.Properties.Count (p => p.Name == "State"));
		}

		[Test]
		public void ConcretePeersRetainInterfaceSlotsAbsentFromManagedBases ()
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Operation" visibility="public">
				    <method name="remove" return="void" visibility="public" abstract="true" />
				  </interface>
				  <class name="Worker" visibility="public" extends="java.lang.Object">
				    <implements name="example.Operation" name-generic-aware="example.Operation" />
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var worker = types.OfType<ClassGen> ().Single (t => t.Name == "Worker");
			var output = GetGeneratedTypeOutput (worker);

			StringAssert.Contains ("Example.IOperation.Remove", output);
			StringAssert.Contains ("remove.()V", output);
			Assert.IsEmpty (worker.Methods);
		}

		[TestCase ("a")]
		[TestCase ("zzg")]
		public void SemanticTypesAreNotDiscardedByObfuscatedNameHeuristics (string name)
		{
			var package = System.Xml.Linq.XElement.Parse ($"""<package name="example"><class name="{name}" visibility="public" /></package>""");
			var type = package.Element ("class");
			options.ExperimentalSemanticModel = false;
			Assert.IsTrue (XmlApiImporter.CreateGenBaseSupport (package, type, options, false).IsObfuscated);

			options.ExperimentalSemanticModel = true;
			Assert.IsFalse (XmlApiImporter.CreateGenBaseSupport (package, type, options, false).IsObfuscated);
			type.SetAttributeValue ("obfuscated", "true");
			Assert.IsTrue (XmlApiImporter.CreateGenBaseSupport (package, type, options, false).IsObfuscated);
		}

		[Test]
		public void SynthesizedAbstractPropertiesHaveConcreteInvokerOverrides ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="State" visibility="public">
				    <method name="getVersion" return="int" visibility="public" abstract="true" />
				  </interface>
				  <class name="Worker" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.State" name-generic-aware="example.State" />
				  </class>
				</package></api>
				""");
			foreach (var type in types)
				type.FillProperties ();
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Worker"));

			StringAssert.Contains ("abstract int Version", output);
			StringAssert.Contains ("override unsafe int Version", output);
			StringAssert.Contains ("getVersion.()I", output);
		}

		[Test]
		public void ClassInvokersDoNotTurnExplicitInterfaceAdaptersIntoOverrides ()
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Consumer" visibility="public">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <method name="accept" return="void" visibility="public" abstract="true">
				      <parameter name="value" type="T" />
				    </method>
				  </interface>
				  <class name="Worker" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.Consumer" name-generic-aware="example.Consumer&lt;float[]&gt;" />
				    <method name="accept" return="void" visibility="public" final="true"
				        managed-bridge-signatures="(Ljava/lang/Object;)V">
				      <parameter name="value" type="float[]" />
				    </method>
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Worker"));

			StringAssert.Contains ("Example.IConsumer.Accept", output);
			StringAssert.DoesNotContain ("override unsafe void Accept", output);
		}

		[Test]
		public void ClassInvokersRetainInheritedSynthesizedAbstractSlots ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Reader" visibility="public">
				    <method name="read" return="int" visibility="public" abstract="true" />
				  </interface>
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.Reader" name-generic-aware="example.Reader" />
				  </class>
				  <class name="Worker" visibility="public" abstract="true" extends="example.Base" />
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Worker"));

			StringAssert.Contains ("override unsafe int Read", output);
			StringAssert.Contains ("read.()I", output);
		}

		[Test]
		public void GenericInterfaceAdaptersDoNotRequireProjectedTypedTargets ()
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Hidden" visibility="" extends="java.lang.Object" />
				  <interface name="Operation" visibility="public">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <method name="apply" return="T" visibility="public" abstract="true">
				      <parameter name="value" type="T" />
				    </method>
				  </interface>
				  <class name="Worker" visibility="public" extends="java.lang.Object">
				    <implements name="example.Operation" name-generic-aware="example.Operation&lt;example.Hidden&gt;" />
				  </class>
				</package></api>
				""");
			var type = (ClassGen) types.Single (t => t.Name == "Worker");
			var output = GetGeneratedTypeOutput (type);

			StringAssert.Contains ("apply.(Ljava/lang/Object;)Ljava/lang/Object;", output);
			StringAssert.DoesNotContain ("Example.Hidden", output);
			StringAssert.DoesNotContain ("JavaCast", output);
			Assert.IsEmpty (type.Methods);
		}

		[Test]
		public void StringConveniencesDoNotRetypeNestedCharSequenceCollections ()
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <interface name="CharSequence" visibility="public" />
				</package><package name="java.util">
				  <interface name="List" visibility="public"><typeParameters><typeParameter name="T" /></typeParameters></interface>
				</package><package name="example">
				  <class name="Worker" visibility="public" extends="java.lang.Object">
				    <constructor name="Worker" visibility="public">
				      <parameter name="label" type="java.lang.CharSequence" />
				      <parameter name="values" type="java.util.List&lt;java.lang.CharSequence&gt;" />
				    </constructor>
				  </class>
				</package></api>
				""");
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Worker"));

			StringAssert.Contains ("Worker (string", output);
			StringAssert.Contains ("IList<global::Java.Lang.ICharSequence>", output);
			StringAssert.DoesNotContain ("IList<string>", output);
		}

		[Test]
		public void ArrayInterfaceBridgesDoNotAlsoEmitGenericForwarders ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Evaluator" visibility="public">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <method name="evaluate" return="T" visibility="public" abstract="true">
				      <parameter name="value" type="T" />
				    </method>
				  </interface>
				  <class name="ArrayEvaluator" visibility="public" extends="java.lang.Object">
				    <implements name="example.Evaluator" name-generic-aware="example.Evaluator&lt;float[]&gt;" />
				    <method name="evaluate" return="float[]" visibility="public" final="true"
				        managed-bridge-signatures="(Ljava/lang/Object;)Ljava/lang/Object;">
				      <parameter name="value" type="float[]" />
				    </method>
				  </class>
				</package></api>
				""");
			var type = (ClassGen) types.Single (t => t.Name == "ArrayEvaluator");

			ManagedContractProjection.Apply (types, options);
			var output = new BoundClass (type, options, generator.Context, new GenerationInfo ("", "", "Test"));

			Assert.AreEqual (1, output.Methods.OfType<ExplicitInterfaceInvokerMethod> ().Count ());
			Assert.IsEmpty (output.Methods.OfType<GenericExplicitInterfaceImplementationMethod> ());
		}

		[Test]
		public void ErasedBaseOverridesRetainTypedInterfaceContracts ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <class name="Result" visibility="public" extends="java.lang.Object" />
				  <interface name="Consumer" visibility="public">
				    <method name="accept" return="void" visibility="public" abstract="true">
				      <parameter name="value" type="example.Result" />
				    </method>
				  </interface>
				  <class name="Base" visibility="public" extends="java.lang.Object">
				    <method name="accept" return="void" visibility="public" final="false">
				      <parameter name="value" type="java.lang.Object" />
				    </method>
				  </class>
				  <class name="Implementation" visibility="public" extends="example.Base">
				    <implements name="example.Consumer" name-generic-aware="example.Consumer" />
				    <method name="accept" return="void" visibility="public" final="false">
				      <parameter name="value" type="example.Result" />
				    </method>
				  </class>
				</package></api>
				""");
			var klass = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");
			var method = klass.Methods.Single ();
			method.ManagedBaseJniSignature = "(Ljava/lang/Object;)V";

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Java.Lang.Object", method.Parameters.Single ().Type);
			Assert.AreEqual ("(Lexample/Result;)V", method.JniSignature);
			Assert.AreEqual (1, method.PublicContractInterfaceMethods.Count);
			var output = new BoundClass (klass, options, generator.Context, new GenerationInfo ("", "", "Test"));
			Assert.AreEqual (1, output.Methods.OfType<ExplicitInterfaceInvokerMethod> ().Count (m => m.Name == "Accept"));
		}

		[Test]
		public void RepeatedImplicitInterfacePropertiesHaveOneAbstractDeclaration ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <class name="String" visibility="public" extends="java.lang.Object" />
				</package><package name="example">
				  <interface name="First" visibility="public">
				    <method name="getName" return="java.lang.String" visibility="public" abstract="true" />
				  </interface>
				  <interface name="Second" visibility="public">
				    <method name="getName" return="java.lang.String" visibility="public" abstract="true" />
				  </interface>
				  <class name="Reader" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.First" name-generic-aware="example.First" />
				    <implements name="example.Second" name-generic-aware="example.Second" />
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var reader = types.OfType<ClassGen> ().Single (t => t.Name == "Reader");
			var output = new BoundClass (reader, options, generator.Context, new GenerationInfo ("", "", "Test"));

			Assert.AreEqual (1, output.Properties.Count (p => p.Name == "Name"));
			Assert.AreEqual (1, new ClassInvokerClass (reader, options).Properties.Count (p => p.Name == "Name"));
		}

		[Test]
		public void SealedClassesDoNotReintroducePlannedVirtualModifiers ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition (api);
			var derived = types.OfType<ClassGen> ().Single (t => t.Name == "Derived");
			derived.IsFinal = true;
			var method = derived.Methods.Single (m => m.JavaName == "mode");
			method.IsOverride = false;
			method.ManagedOverride = "virtual";
			var output = new BoundMethod (derived, method, options, true);

			Assert.IsFalse (output.IsVirtual);
			Assert.IsFalse (output.IsOverride);
		}

		[Test]
		public void InterfacePeerMemberFieldsUseValidDistinctNativeIdentities ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="example.auth-api">
				  <interface name="Reader" visibility="public" />
				</package><package name="example.auth_api">
				  <interface name="Reader" visibility="public" />
				</package></api>
				""");
			var names = types.Select (t => options.GetInterfaceInvokerMembersName (t)).ToArray ();

			Assert.AreEqual (2, names.Distinct ().Count ());
			Assert.IsTrue (names.All (n => n.All (c => char.IsLetterOrDigit (c) || c == '_')));
			foreach (var iface in types.OfType<InterfaceGen> ())
				Assert.IsTrue (new InterfaceInvokerClass (iface, options).Fields.Any (f => f.Name == options.GetInterfaceInvokerMembersName (iface)));
		}

		[Test]
		public void GenericReturnsUseTheExactInterfaceMarshalerWithoutChangingNativeIdentity ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <interface name="Result" visibility="public" />
				  <interface name="Reader" visibility="public">
				    <method name="read" return="example.Result" visibility="public" abstract="true" />
				  </interface>
				  <class name="Parser" visibility="public" extends="java.lang.Object">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <implements name="example.Reader" name-generic-aware="example.Reader" />
				    <method name="read" return="T" visibility="public" final="false" />
				  </class>
				</package></api>
				""");
			var method = types.Single (t => t.Name == "Parser").Methods.Single ();
			var native = method.JniSignature;

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Example.IResult", method.RetVal.FullName);
			Assert.AreEqual (native, method.JniSignature);
			StringAssert.Contains ("Example.IResult>", method.RetVal.FromNative (options, "__value", true));
		}

		[Test]
		public void ExistingProjectedMethodsDoNotAcquireRedundantGenericAdapters ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <class name="Result" visibility="public" extends="java.lang.Object" />
				  <interface name="Reader" visibility="public">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <method name="read" return="T" visibility="public" abstract="true" />
				  </interface>
				  <class name="Parser" visibility="public" abstract="true" extends="java.lang.Object">
				    <typeParameters><typeParameter name="Message" /></typeParameters>
				    <implements name="example.Reader" name-generic-aware="example.Reader&lt;Message&gt;" />
				    <method name="read" return="example.Result" visibility="public" final="false" />
				  </class>
				</package></api>
				""");
			options.ExperimentalSemanticModel = true;
			var parser = types.OfType<ClassGen> ().Single (t => t.Name == "Parser");
			parser.FixupExplicitImplementation ();

			ManagedContractProjection.Apply (types, options);

			var output = new BoundClass (parser, options, generator.Context, new GenerationInfo ("", "", "Test"));
			Assert.AreEqual (1, output.Methods.Count (m => m.Name == "Read"));
			Assert.IsNull (output.Methods.Single (m => m.Name == "Read").ExplicitInterfaceImplementation);
		}

		[Test]
		public void DuplicateInheritedCopiesKeepOneNativeBinding ()
		{
			var types = ParseApiDefinition (api);
			var owner = types.Single (t => t.Name == "Base");
			var method = owner.Methods.Single (m => m.JavaName == "run");
			var copy = method.Clone (owner);
			Assert.IsTrue (copy.Validate (options, new GenericParameterDefinitionList (), generator.Context));
			owner.Methods.Add (copy);

			var decisions = ManagedContractProjection.Apply (types, options);

			Assert.AreEqual (1, owner.Methods.Count (m => m.JavaName == "run"));
			Assert.IsTrue (decisions.Any (d => d.Reason == "coalesce-duplicate-native-slot"));
		}

		[Test]
		public void CovariantBridgesRetainDistinctNonvirtualManagedAliases ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public">
				    <method name="read" return="java.lang.Object" visibility="public" final="false" />
				  </class>
				</package><package name="example">
				  <class name="Result" visibility="public" extends="java.lang.Object" />
				  <class name="Implementation" visibility="public" extends="java.lang.Object">
				    <method name="read" return="example.Result" visibility="public" final="false"
				      managed-bridge-signatures="()Ljava/lang/Object;" />
				    <method name="read" return="java.lang.Object" visibility="public" final="false"
				      managed-bridge-signatures="()Ljava/lang/Object;" />
				  </class>
				</package></api>
				""");
			var owner = types.Single (t => t.Name == "Implementation");
			var typed = owner.Methods.Single (m => m.RetVal.FullName == "Example.Result");
			var bridge = owner.Methods.Single (m => m.RetVal.FullName == "Java.Lang.Object");
			typed.RetVal.SetManagedType ("Java.Lang.Object");

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Read", typed.Name);
			Assert.AreEqual ("ReadBridge", bridge.Name);
			Assert.AreEqual ("()Lexample/Result;", typed.JniSignature);
			Assert.AreEqual ("()Ljava/lang/Object;", bridge.JniSignature);
			Assert.IsFalse (bridge.IsVirtual);
			Assert.IsFalse (bridge.IsOverride);
			Assert.AreEqual (2, owner.Methods.Count);
		}

		[Test]
		public void ProtectedOverridesKeepExplicitPublicInterfaceContracts ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public">
				    <method name="clone" return="java.lang.Object" visibility="protected" final="false" />
				  </class>
				</package><package name="example">
				  <interface name="Cloner" visibility="public">
				    <method name="clone" return="java.lang.Object" visibility="public" abstract="true" />
				  </interface>
				  <class name="Implementation" visibility="public" extends="java.lang.Object">
				    <implements name="example.Cloner" name-generic-aware="example.Cloner" />
				    <method name="clone" return="java.lang.Object" visibility="public" final="false" />
				  </class>
				</package></api>
				""");
			options.ExperimentalSemanticModel = true;
			ManagedContractProjection.Apply (types, options);
			var implementation = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");
			var method = implementation.Methods.Single ();

			Assert.AreEqual ("protected", method.Visibility);
			Assert.AreEqual (1, method.PublicContractInterfaceMethods.Count);
			var output = new BoundClass (implementation, options, generator.Context, new GenerationInfo ("", "", "Test"));
			Assert.AreEqual (1, output.Methods.Count (m => m.Name == "Clone" && m.ExplicitInterfaceImplementation == options.GetOutputName ("Example.ICloner")));
		}

		[Test]
		public void InvokersDoNotReimplementConcreteProjectedInterfaceSlots ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <class name="Result" visibility="public" extends="java.lang.Object" />
				  <class name="ChildResult" visibility="public" extends="example.Result" />
				  <interface name="Reader" visibility="public">
				    <method name="read" return="example.Result" visibility="public" abstract="true" />
				  </interface>
				  <class name="Implementation" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.Reader" name-generic-aware="example.Reader" />
				    <method name="read" return="example.ChildResult" visibility="public" final="true" />
				  </class>
				</package></api>
				""");
			options.ExperimentalSemanticModel = true;
			ManagedContractProjection.Apply (types, options);
			var implementation = types.OfType<ClassGen> ().Single (t => t.Name == "Implementation");
			Assert.AreEqual ("Example.Result", implementation.Methods.Single ().RetVal.FullName);
			Assert.IsFalse (new ClassInvokerClass (implementation, options).Methods.Any (m => m.Name == "Read"));
		}

		[Test]
		public void SealedManagedBaseSlotsAreHiddenInsteadOfOverridden ()
		{
			var types = ParseApiDefinition (api);
			var basis = types.Single (t => t.Name == "Base").Methods.Single (m => m.JavaName == "read");
			basis.IsFinal = true;

			ManagedContractProjection.Apply (types, options);

			var method = types.Single (t => t.Name == "Derived").Methods.Single (m => m.JavaName == "read");
			Assert.IsFalse (method.IsOverride);
			Assert.AreEqual ("none", method.ManagedOverride);
		}

		[Test]
		public void BridgeDescriptorsDoNotUnifyDifferentNativeNames ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				</package><package name="example">
				  <interface name="Converter" visibility="public">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				    <method name="first" return="java.lang.Object" visibility="public" abstract="true"><parameter name="value" type="java.lang.Object" /></method>
				    <method name="second" return="java.lang.Object" visibility="public" abstract="true"><parameter name="value" type="java.lang.Object" /></method>
				  </interface>
				  <class name="Implementation" visibility="public" extends="java.lang.Object">
				    <implements name="example.Converter" name-generic-aware="example.Converter&lt;java.lang.Object&gt;" />
				    <method name="first" return="java.lang.Object" visibility="public" managed-bridge-signatures="(Ljava/lang/Object;)Ljava/lang/Object;"><parameter name="value" type="java.lang.Object" /></method>
				    <method name="second" return="java.lang.Object" visibility="public" managed-bridge-signatures="(Ljava/lang/Object;)Ljava/lang/Object;"><parameter name="value" type="java.lang.Object" /></method>
				  </class>
				</package></api>
				""");
			var implementation = types.Single (t => t.Name == "Implementation");
			Assert.IsTrue (implementation.GetGenericMappings (types.OfType<InterfaceGen> ().Single (), new Dictionary<string, string> ()));
			ManagedContractProjection.Apply (types, options);
			Assert.AreEqual ("First", implementation.Methods.Single (m => m.JavaName == "first").Name);
			Assert.AreEqual ("Second", implementation.Methods.Single (m => m.JavaName == "second").Name);
		}

		[Test]
		public void CallbackCachesUseNativeReturnsAfterManagedErasure ()
		{
			options.ExperimentalSemanticModel = true;
			var owner = ParseApiDefinition (api).Single (t => t.Name == "Base");
			var first = owner.Methods.Single (m => m.JavaName == "run");
			var second = first.Clone (owner);
			first.JniSignatureOverride = "()Ljava/lang/String;";
			second.JniSignatureOverride = "()Ljava/lang/Object;";
			Assert.IsTrue (second.Validate (options, new GenericParameterDefinitionList (), generator.Context));

			Assert.AreEqual (first.EscapedCallbackName, second.EscapedCallbackName);
			Assert.AreNotEqual (first.GetEscapedCallbackName (options), second.GetEscapedCallbackName (options));
			Assert.AreNotEqual (UnmanagedCallbackSupport.GetConnectorName (owner, first, options),
				UnmanagedCallbackSupport.GetConnectorName (owner, second, options));
			Assert.AreNotEqual (options.CallbackNames.GetCallbackName (owner, first), options.CallbackNames.GetCallbackName (owner, second));
			StringAssert.IsMatch ("^[A-Za-z_][A-Za-z0-9_]*$", first.GetEscapedCallbackName (options));
		}

		[Test]
		public void CallbackNamesDistinguishNativeMethodsWithTheSameErasedShape ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="example"><interface name="Reader" visibility="public">
				  <method name="read-first" managedName="Read" return="void" visibility="public" abstract="false" managed-jni-signature="(I)V">
				    <parameter name="value" type="int" />
				  </method>
				  <method name="read-second" managedName="Read" return="void" visibility="public" abstract="false" managed-jni-signature="(I)V">
				    <parameter name="value" type="int" />
				  </method>
				</interface></package></api>
				""");
			var owner = types.Single ();
			var methods = owner.Methods.ToArray ();
			Assert.AreEqual (2, methods.Length);
			Assert.AreEqual (methods [0].IDSignature, methods [1].IDSignature);
			Assert.AreNotEqual (
				UnmanagedCallbackSupport.GetCallbackName (owner, methods [0], options),
				UnmanagedCallbackSupport.GetCallbackName (owner, methods [1], options));
			Assert.AreNotEqual (
				UnmanagedCallbackSupport.GetConnectorName (owner, methods [0], options),
				UnmanagedCallbackSupport.GetConnectorName (owner, methods [1], options));
		}

		[Test]
		public void InaccessibleInterfacesRetainPublicOperationsOnAbstractPeers ()
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Result" visibility="public" extends="java.lang.Object" />
				  <interface name="HiddenFactory" visibility="">
				    <method name="create" return="example.Result" visibility="public" abstract="true" />
				    <method name="getCount" return="int" visibility="public" abstract="true" />
				    <method name="configure" return="example.HiddenFactory" visibility="public" abstract="true" />
				  </interface>
				  <class name="Factory" visibility="public" abstract="true" extends="java.lang.Object">
				    <implements name="example.HiddenFactory" name-generic-aware="example.HiddenFactory" />
				    <method name="configure" return="example.Factory" visibility="public" final="false" />
				  </class>
				</package></api>
				""");
			var type = types.Single (t => t.Name == "Factory");

			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (type);

			StringAssert.DoesNotContain ("Example.IHiddenFactory", output);
			StringAssert.Contains ("public abstract global::Example.Result Create ();", output);
			StringAssert.Contains ("public override unsafe global::Example.Result Create ()", output);
			StringAssert.Contains ("public abstract int GetCount ();", output);
			StringAssert.Contains ("public override unsafe int GetCount ()", output);
			StringAssert.Contains ("create.()Lexample/Result;", output);
			StringAssert.Contains ("getCount.()I", output);
			StringAssert.DoesNotContain ("NotImplementedException", output);
		}

		[Test]
		public void ProtectedNestedTypesDoNotInheritMissingPrivatePeers ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Hidden" visibility="" extends="java.lang.Object">
				    <method name="work" return="int" visibility="public" />
				  </class>
				  <class name="Owner" visibility="public" extends="java.lang.Object" />
				  <class name="Owner.Callback" visibility="protected" extends="example.Hidden" />
				</package></api>
				""");
			var nested = types.Single (t => t.Name == "Owner").NestedTypes.Single ();

			Assert.AreEqual ("java.lang.Object", ((ClassGen) nested).BaseType);
			Assert.AreEqual ("work", nested.Methods.Single ().JavaName);
			Assert.AreEqual ("()I", nested.Methods.Single ().JniSignature);
		}

		[TestCase ("")]
		[TestCase ("protected")]
		public void PublicSignaturesDoNotExposeLessAccessibleNestedPeers (string visibility)
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ($$"""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Container" visibility="public" extends="java.lang.Object">
				    <constructor name="Container" visibility="public"><parameter name="value" type="example.Container.Hidden" /></constructor>
				  </class>
				  <class name="Container.Hidden" visibility="{{visibility}}" extends="java.lang.Object" />
				</package></api>
				""");
			var type = (ClassGen) types.Single (t => t.Name == "Container");

			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Java.Lang.Object", type.Ctors.Single ().Parameters [0].Type);
			Assert.AreEqual ("(Lexample/Container$Hidden;)V", type.Ctors.Single ().JniSignature);
		}

		[Test]
		public void InaccessibleSignatureTypesUseOpaquePeersWithoutChangingNativeSlots ()
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="Hidden" visibility="" />
				  <class name="Container" visibility="public" extends="java.lang.Object">
				    <constructor name="Container" visibility="public"><parameter name="value" type="example.Hidden" /></constructor>
				    <method name="getValue" return="example.Hidden" visibility="public" />
				    <method name="accept" return="void" visibility="public"><parameter name="value" type="example.Hidden" /></method>
				  </class>
				</package></api>
				""");
			var type = (ClassGen) types.Single (t => t.Name == "Container");

			var decisions = ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Java.Lang.Object", type.Properties.Single ().Getter.RetVal.FullName);
			Assert.AreEqual ("Java.Lang.Object", type.Methods.Single ().Parameters [0].Type);
			Assert.AreEqual ("Java.Lang.Object", type.Ctors.Single ().Parameters [0].Type);
			Assert.AreEqual ("()Lexample/Hidden;", type.Properties.Single ().Getter.JniSignature);
			Assert.AreEqual ("(Lexample/Hidden;)V", type.Methods.Single ().JniSignature);
			Assert.AreEqual ("(Lexample/Hidden;)V", type.Ctors.Single ().JniSignature);
			Assert.AreEqual (3, decisions.Count (d => d.Reason.StartsWith ("retain-inaccessible-native-")));
			var output = GetGeneratedTypeOutput (type);
			StringAssert.DoesNotContain ("Example.IHidden", output);
			StringAssert.Contains ("getValue.()Lexample/Hidden;", output);
			StringAssert.Contains ("accept.(Lexample/Hidden;)V", output);
		}

		[Test]
		public void ErasedStaticParametersRetainDistinctNativeOverloads ()
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example"><class name="Helpers" visibility="public" extends="java.lang.Object">
				  <method name="read" managedName="Read" return="boolean" visibility="public" static="true" managed-jni-signature="(Lexample/Reader;)Z">
				    <parameter name="value" type="java.lang.Object" />
				  </method>
				  <method name="read" managedName="Read" return="boolean" visibility="public" static="true" managed-jni-signature="(Lexample/Writer;)Z">
				    <parameter name="value" type="java.lang.Object" />
				  </method>
				</class></package></api>
				""");
			var type = types.Single (t => t.Name == "Helpers");

			ManagedContractProjection.Apply (types, options);

			CollectionAssert.AreEquivalent (new [] { "ReadWithReader", "ReadWithWriter" }, type.Methods.Select (m => m.Name));
			var output = GetGeneratedTypeOutput (type);
			StringAssert.Contains ("ReadWithReader (global::Java.Lang.Object value)", output);
			StringAssert.Contains ("ReadWithWriter (global::Java.Lang.Object value)", output);
			StringAssert.Contains ("read.(Lexample/Reader;)Z", output);
			StringAssert.Contains ("read.(Lexample/Writer;)Z", output);
		}

		[TestCase (true)]
		[TestCase (false)]
		public void ReturnOnlyOverloadsRetainAllNativeMethods (bool isStatic)
		{
			options.ExperimentalSemanticModel = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="First" visibility="public" extends="java.lang.Object" />
				  <class name="Second" visibility="public" extends="java.lang.Object" />
				  <class name="Helpers" visibility="public" extends="java.lang.Object">
				    <method name="read" return="example.First" visibility="public" final="true" />
				    <method name="read" return="example.Second" visibility="public" final="true" />
				  </class>
				</package></api>
				""");
			var type = types.Single (t => t.Name == "Helpers");
			foreach (var method in type.Methods)
				method.IsStatic = isStatic;

			ManagedContractProjection.Apply (types, options);

			CollectionAssert.AreEquivalent (new [] { "ReadReturningFirst", "ReadReturningSecond" }, type.Methods.Select (m => m.Name));
			var output = GetGeneratedTypeOutput (type);
			StringAssert.Contains ("read.()Lexample/First;", output);
			StringAssert.Contains ("read.()Lexample/Second;", output);
			StringAssert.Contains ("GetObject<global::Example.First>", output);
			StringAssert.Contains ("GetObject<global::Example.Second>", output);
		}

		[Test]
		public void MangledAbstractSlotsStillRequireConcreteClrOverrides ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				    <method name="read-ABCDEFG" return="int" visibility="public" abstract="true" final="false" />
				  </class>
				  <class name="Derived" visibility="public" final="true" extends="example.Base">
				    <method name="read-ABCDEFG" return="int" visibility="public" final="true" />
				  </class>
				</package></api>
				""");
			Java.Interop.Tools.Generator.Transformation.KotlinFixups.Fixup (types);
			var basis = types.Single (t => t.Name == "Base").Methods.Single ();
			Assert.IsTrue (basis.IsAbstract);
			Assert.IsTrue (basis.IsFinal);
			Assert.IsFalse (basis.IsVirtual);

			ManagedContractProjection.Apply (types, options);

			var derived = types.Single (t => t.Name == "Derived");
			Assert.IsTrue (derived.Methods.Single ().IsOverride);
			Assert.AreEqual ("override", derived.Methods.Single ().ManagedOverride);
			StringAssert.Contains ("public override sealed unsafe int Read ()", GetGeneratedTypeOutput (derived));
			Assert.AreEqual ("read-ABCDEFG", derived.Methods.Single ().JavaName);
			Assert.AreEqual ("()I", derived.Methods.Single ().JniSignature);
		}

		[Test]
		public void NonvirtualManagedBaseDoesNotProduceAnInvalidOverride ()
		{
			var types = ParseApiDefinition (api);
			var basis = types.Single (t => t.Name == "Base").Methods.Single (m => m.JavaName == "mode");
			basis.IsVirtual = false;
			basis.IsOverride = false;
			ManagedContractProjection.Apply (types, options);
			var method = types.Single (t => t.Name == "Derived").Methods.Single (m => m.JavaName == "mode");

			Assert.IsFalse (method.IsOverride);
			Assert.AreEqual ("none", method.ManagedOverride);
		}

		[Test]
		public void AbstractOverridesReachTheAbstractDeclarationWriter ()
		{
			var types = ParseApiDefinition (api.Replace (
				"<method name=\"read\" return=\"example.ChildResult\" visibility=\"public\" final=\"false\" />",
				"<method name=\"read\" return=\"example.ChildResult\" visibility=\"public\" final=\"false\" abstract=\"true\" />"));
			ManagedContractProjection.Apply (types, options);
			var method = types.Single (t => t.Name == "Derived").Methods.Single (m => m.JavaName == "read");

			Assert.AreEqual ("override", method.ManagedOverride);
		}

		[TestCase (false, "")]
		[TestCase (true, "ActualProperty")]
		public void ReferencedMembersRetainNativeIdentityAndActualPropertyShape (bool getter, string expectedProperty)
		{
			options.ExperimentalSemanticModel = true;
			var owner = ParseApiDefinition (api).Single (t => t.Name == "Base");
			using var assembly = Mono.Cecil.AssemblyDefinition.CreateAssembly (
				new Mono.Cecil.AssemblyNameDefinition ("Reference", new System.Version (1, 0)), "Reference", Mono.Cecil.ModuleKind.Dll);
			var module = assembly.MainModule;
			var type = new Mono.Cecil.TypeDefinition ("Example", "Reference", Mono.Cecil.TypeAttributes.Public);
			module.Types.Add (type);
			var method = new Mono.Cecil.MethodDefinition (getter ? "get_ActualProperty" : "GetValue", Mono.Cecil.MethodAttributes.Public, module.TypeSystem.Int32);
			type.Methods.Add (method);
			if (getter) {
				method.SemanticsAttributes = Mono.Cecil.MethodSemanticsAttributes.Getter;
				type.Properties.Add (new Mono.Cecil.PropertyDefinition ("ActualProperty", Mono.Cecil.PropertyAttributes.None, module.TypeSystem.Int32) { GetMethod = method });
			}
			var attribute = new Mono.Cecil.CustomAttribute (new Mono.Cecil.MethodReference (
				".ctor", module.TypeSystem.Void, new Mono.Cecil.TypeReference ("Android.Runtime", "RegisterAttribute", module, module.TypeSystem.CoreLibrary)));
			attribute.ConstructorArguments.Add (new Mono.Cecil.CustomAttributeArgument (module.TypeSystem.String, "getValue"));
			attribute.ConstructorArguments.Add (new Mono.Cecil.CustomAttributeArgument (module.TypeSystem.String, "()I"));
			method.CustomAttributes.Add (attribute);

			var imported = CecilApiImporter.CreateMethod (owner, method, options);

			Assert.AreEqual ("()I", imported.JniSignatureOverride);
			Assert.AreEqual (expectedProperty, imported.PropertyNameOverride);
		}

		[Test]
		public void ModernInterfacesRetainConstantsWithoutLegacyAlternativeClasses ()
		{
			options.ExperimentalSemanticModel = true;
			options.SupportInterfaceConstants = true;
			options.SupportDefaultInterfaceMethods = true;
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="Flags" visibility="public">
				    <field name="VALUE" type="int" visibility="public" static="true" final="true" value="7" />
				  </interface>
				</package></api>
				""");
			var output = GetGeneratedTypeOutput (types.Single ());

			StringAssert.Contains ("interface IFlags", output);
			StringAssert.Contains ("const int Value = (int) 7", output);
			StringAssert.DoesNotContain ("abstract class Flags", output);
		}

		[Test]
		public void NativeGenericClassesRemainErasedInGenericAdapters ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Task" visibility="public" extends="java.lang.Object">
				      <typeParameters><typeParameter name="T" /></typeParameters>
				    </class>
				    <class name="Consumer" visibility="public" extends="java.lang.Object">
				      <method name="consume" return="example.Task&lt;java.lang.Object&gt;" visibility="public" final="false">
				        <parameter name="task" type="example.Task&lt;java.lang.Object&gt;" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			var method = types.Single (t => t.Name == "Consumer").Methods.Single ();
			var mappings = new System.Collections.Generic.Dictionary<string, string> ();

			Assert.AreEqual ("task", method.Parameters [0].GetGenericCall (options, mappings));
			Assert.AreEqual ("result", method.RetVal.GetGenericReturn (options, "result", mappings));
		}

		[Test]
		public void InterfaceNamesAccountForImplicitAbstractImplementations ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Contract" visibility="public">
				      <method name="owner" return="void" visibility="public" abstract="true" />
				    </interface>
				    <class name="Owner" visibility="public" abstract="true" extends="java.lang.Object">
				      <implements name="example.Contract" name-generic-aware="example.Contract" />
				    </class>
				    <class name="Child" visibility="public" extends="example.Owner">
				      <method name="owner" return="void" visibility="public" final="false" />
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("InvokeOwner", types.Single (t => t.Name == "IContract").Methods.Single ().Name);
			Assert.AreEqual ("InvokeOwner", types.Single (t => t.Name == "Child").Methods.Single ().Name);
			StringAssert.Contains ("void InvokeOwner", GetGeneratedTypeOutput (types.Single (t => t.Name == "Owner")));
		}

		[Test]
		[TestCase (false)]
		[TestCase (true)]
		public void ExplicitInterfaceInvokersRetainFormattedContractNames (bool semantic)
		{
			options.ExperimentalSemanticModel = semantic;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <interface name="CharSequence" visibility="public" />
				</package><package name="example">
				  <interface name="Contract" visibility="public">
				    <method name="read" return="java.lang.CharSequence" visibility="public" abstract="true" />
				  </interface>
				</package></api>
				""");
			var contract = types.Single (t => t.Name == "IContract");
			var writer = new ExplicitInterfaceInvokerMethod (contract, contract.Methods.Single (), options);

			Assert.AreEqual (semantic ? "ReadFormatted" : "Read", writer.Name);
			Assert.IsTrue (writer.Body.Any (s => s.Contains ("read.()Ljava/lang/CharSequence;")));
		}

		[Test]
		public void InheritedInterfaceCallbacksSelectTheirDeclaringContract ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <interface name="First" visibility="public">
				    <method name="read" return="java.lang.Object" visibility="public" abstract="true" />
				  </interface>
				  <interface name="Second" visibility="public">
				    <method name="read" return="java.lang.Object" visibility="public" abstract="true" />
				  </interface>
				  <interface name="Combined" visibility="public">
				    <implements name="example.First" name-generic-aware="example.First" />
				    <implements name="example.Second" name-generic-aware="example.Second" />
				  </interface>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);

			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "ICombined"));

			StringAssert.Contains ($"(({options.GetOutputName ("Example.IFirst")}) __this).Read ()", output);
			StringAssert.DoesNotContain ("__this.Read ()", output);
		}

		[Test]
		public void FinalErasedOverloadsDoNotCollideWithRequiredBaseOverrides ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang">
				  <class name="Object" visibility="public" />
				  <class name="String" visibility="public" extends="java.lang.Object" />
				</package><package name="example">
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				    <method name="apply" return="java.lang.Object" visibility="protected" abstract="true">
				      <parameter name="value" type="java.lang.Object" />
				    </method>
				  </class>
				  <class name="Derived" visibility="public" extends="example.Base">
				    <method name="apply" return="java.lang.Object" visibility="public" final="true" managed-jni-signature="(Ljava/lang/String;)Ljava/lang/Object;">
				      <parameter name="value" type="java.lang.Object" managed-jni-type="Ljava/lang/String;" />
				    </method>
				    <method name="apply" return="java.lang.Object" visibility="protected" final="true">
				      <parameter name="value" type="java.lang.Object" />
				    </method>
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var derived = types.Single (t => t.Name == "Derived");
			var methods = derived.Methods.ToArray ();
			var basis = methods.Single (m => m.IsOverride);
			var typed = methods.Single (m => !m.IsOverride);

			Assert.AreEqual ("Apply", basis.Name);
			Assert.AreEqual ("ApplyWithString", typed.Name);
			Assert.AreEqual ("(Ljava/lang/String;)Ljava/lang/Object;", typed.JniSignature);
			var output = GetGeneratedTypeOutput (derived);
			StringAssert.Contains ("ApplyWithString (", output);
			StringAssert.Contains ("override sealed unsafe", output);
		}

		[Test]
		public void NestedTypesCannotHaveTheirEnclosingManagedTypeName ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Outer" visibility="public" extends="java.lang.Object">
				    <method name="create" return="example.Outer.Item.Item" visibility="public" />
				  </class>
				  <class name="Outer.Item" visibility="public" extends="java.lang.Object" static="true" />
				  <class name="Outer.Item.Item" visibility="public" extends="java.lang.Object" static="true" />
				</package></api>
				""");
			ManagedContractProjection.AllocateTypeNames (types);
			var outer = types.Single (t => t.Name == "Outer");
			var nested = outer.NestedTypes.Single ().NestedTypes.Single ();

			Assert.AreEqual ("ItemType", nested.Name);
			Assert.AreEqual ("Example.Outer.Item.ItemType", nested.FullName);
			Assert.AreEqual (nested.FullName, outer.Methods.Single ().RetVal.FullName);
			Assert.AreEqual ("example/Outer$Item$Item", nested.RawJniName);
		}

		[Test]
		public void FieldsCannotBeDroppedByStaticMethodNameCollisions ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Owner" visibility="public" extends="java.lang.Object">
				    <field name="create" type="example.Owner" visibility="public" static="true" final="true" />
				    <method name="create" return="example.Owner" visibility="public" static="true">
				      <parameter name="value" type="int" />
				    </method>
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var owner = types.Single (t => t.Name == "Owner");

			Assert.AreEqual ("CreateValue", owner.Fields.Single ().Name);
			var output = GetGeneratedTypeOutput (owner);
			StringAssert.Contains ("[Register (\"create\")]", output);
			StringAssert.Contains ("[Register (\"create\", \"(I)Lexample/Owner;\", \"\")]", output);
			StringAssert.Contains ("CreateValue {", output);
		}

		[Test]
		public void DistinctNativeFieldsCannotShareTheirProjectedManagedName ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Owner" visibility="public" extends="java.lang.Object">
				    <field name="DetectionApi" type="example.Owner" visibility="public" static="true" final="true" />
				    <field name="DETECTION_API" type="example.Owner" visibility="public" static="true" final="true" />
				  </class>
				</package></api>
				""");
			var decisions = ManagedContractProjection.Apply (types, options);
			var owner = types.Single (t => t.Name == "Owner");

			Assert.AreEqual ("DetectionApi", owner.Fields.Single (f => f.JavaName == "DETECTION_API").Name);
			Assert.AreEqual ("DetectionApiValue", owner.Fields.Single (f => f.JavaName == "DetectionApi").Name);
			Assert.IsTrue (decisions.Any (d => d.Reason == "field-field-name-collision"));
			var output = GetGeneratedTypeOutput (owner);
			StringAssert.Contains ("[Register (\"DETECTION_API\")]", output);
			StringAssert.Contains ("[Register (\"DetectionApi\")]", output);
			StringAssert.Contains ("DetectionApi {", output);
			StringAssert.Contains ("DetectionApiValue {", output);
		}

		[Test]
		public void FieldsCannotHideInheritedMethodContracts ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				<package name="example">
				  <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				    <method name="work" return="void" visibility="protected" abstract="true" />
				  </class>
				  <class name="Derived" visibility="public" abstract="true" extends="example.Base">
				    <field name="work" type="int" visibility="protected" />
				  </class>
				</package></api>
				""");
			ManagedContractProjection.Apply (types, options);
			var derived = types.Single (t => t.Name == "Derived");

			Assert.AreEqual ("WorkField", derived.Fields.Single ().Name);
			Assert.AreEqual ("work", derived.Fields.Single ().JavaName);
			var output = GetGeneratedTypeOutput (derived);
			StringAssert.Contains ("protected int WorkField", output);
			StringAssert.Contains ("protected override unsafe void Work ()", output);
		}

		[Test]
		public void PublicFacadesForUnemittedVariantPropertiesInvokeNativeSlots ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Result" visibility="public" extends="java.lang.Object" />
				    <class name="Base" visibility="public" extends="java.lang.Object">
				      <method name="getAdapter" return="example.Result" visibility="public" final="false" />
				      <method name="setAdapter" return="void" visibility="public" final="false">
				        <parameter name="value" type="example.Result" />
				      </method>
				    </class>
				    <class name="Derived" visibility="public" extends="example.Base">
				      <method name="getAdapter" return="java.lang.Object" visibility="protected" abstract="true" />
				      <method name="setAdapter" return="void" visibility="protected" abstract="true">
				        <parameter name="value" type="java.lang.Object" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			var derived = types.Single (t => t.Name == "Derived");
			derived.Properties.Single ().Setter.PublicContractFacadeName = "InvokeSetRawAdapter";

			var output = GetGeneratedTypeOutput (derived);

			StringAssert.Contains ("public unsafe void InvokeSetRawAdapter", output);
			StringAssert.Contains ("[Register (\"setAdapter\", \"(Ljava/lang/Object;)V\", \"\")]", output);
			StringAssert.Contains ("const string __id = \"setAdapter.(Ljava/lang/Object;)V\";", output);
			StringAssert.Contains ("_members.InstanceMethods.Invoke", output);
			StringAssert.DoesNotContain ("Adapter = value;", output);
		}

		[Test]
		public void WidenedJavaVisibilityRetainsAPublicCallableFacade ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" extends="java.lang.Object">
				      <method name="run" return="int" visibility="protected" final="false">
				        <parameter name="value" type="int" />
				      </method>
				    </class>
				    <class name="Derived" visibility="public" extends="example.Base">
				      <method name="run" return="int" visibility="public" final="false">
				        <parameter name="value" type="int" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var derived = types.Single (t => t.Name == "Derived");
			var method = derived.Methods.Single ();
			var output = GetGeneratedTypeOutput (derived);

			Assert.AreEqual ("protected", method.Visibility);
			Assert.AreEqual ("InvokeRun", method.PublicContractFacadeName);
			StringAssert.Contains ("protected override unsafe int Run", output);
			StringAssert.Contains ("public int InvokeRun", output);
			StringAssert.Contains ("return Run (value);", output);
			StringAssert.Contains ("[Register (\"run\", \"(I)I\"", output);
		}

		[Test]
		[TestCase (false)]
		[TestCase (true)]
		public void ErasedInterfaceParametersKeepTheNativeDescriptor (bool bridge)
		{
			var source = """
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Result" visibility="public" extends="java.lang.Object" />
				    <interface name="Listener" visibility="public">
				      <typeParameters><typeParameter name="T" /></typeParameters>
				      <method name="accept" return="void" visibility="public" abstract="true" managed-jni-signature="(Lexample/Result;)V">
				        <parameter name="value" type="T" />
				      </method>
				    </interface>
				    <class name="Consumer" visibility="public" extends="java.lang.Object">
				      <implements name="example.Listener" name-generic-aware="example.Listener&lt;example.Result&gt;" />
				      <method name="accept" return="void" visibility="public" final="false" managed-jni-signature="(Lexample/Result;)V">
				        <parameter name="value" type="example.Result" />
				      </method>
				    </class>
				  </package>
				</api>
				""";
			if (bridge)
				source = source.Replace ("abstract=\"true\" managed-jni-signature=\"(Lexample/Result;)V\"",
					"abstract=\"true\" managed-jni-signature=\"(Ljava/lang/Object;)V\"");
			var types = ParseApiDefinition (source);
			var method = types.Single (t => t.Name == "Consumer").Methods.Single ();
			if (bridge)
				method.NativeBridgeSignatures = ["(Ljava/lang/Object;)V"];
			ManagedContractProjection.Apply (types, options);

			Assert.AreEqual ("Java.Lang.Object", method.Parameters [0].Type);
			Assert.AreEqual ("Lexample/Result;", method.Parameters [0].JniType);
			Assert.AreEqual ("(Lexample/Result;)V", method.JniSignature);
		}

		[Test]
		public void TypeNamesAvoidNamespaceAndInterfacePropertyCollisions ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Provider" visibility="public">
				      <method name="getStatus" return="int" visibility="public" abstract="true" />
				    </interface>
				    <class name="Status" visibility="public" extends="java.lang.Object">
				      <implements name="example.Provider" name-generic-aware="example.Provider" />
				    </class>
				    <class name="Constants" visibility="public" extends="java.lang.Object" />
				  </package>
				  <package name="example.constants"><class name="Child" visibility="public" extends="java.lang.Object" /></package>
				</api>
				""");
			var native_names = types.Select (t => t.JavaName).ToArray ();
			var decisions = ManagedContractProjection.AllocateTypeNames (types);

			Assert.AreEqual ("Example.StatusType", types.Single (t => t.JavaName == "example.Status").FullName);
			Assert.AreEqual ("Example.ConstantsType", types.Single (t => t.JavaName == "example.Constants").FullName);
			CollectionAssert.AreEqual (native_names, types.Select (t => t.JavaName));
			Assert.AreEqual (2, decisions.Count);
		}

		[Test]
		public void NestedTypesDoNotStealRequiredInterfacePropertyNames ()
		{
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Provider" visibility="public">
				      <method name="getMode" return="int" visibility="public" abstract="true" />
				    </interface>
				    <class name="Container" visibility="public" extends="java.lang.Object">
				      <implements name="example.Provider" name-generic-aware="example.Provider" />
				      <method name="getMode" return="int" visibility="public" />
				    </class>
				    <class name="Container.Mode" visibility="public" extends="java.lang.Object" />
				  </package>
				</api>
				""", beforeFillProperties: gens => ManagedContractProjection.AllocateTypeNames (gens));
			ManagedContractProjection.Apply (types, options);
			var container = types.Single (t => t.Name == "Container");
			var nested = container.NestedTypes.Single ();
			Assert.AreEqual ("ModeType", nested.Name);
			Assert.AreEqual ("example.Container.Mode", nested.JavaName);
			Assert.AreEqual ("Example.Container.ModeType", nested.FullName);
			Assert.AreEqual ("Mode", container.Properties.Single ().Name);
		}

		[Test]
		public void InternalNestedTypesDoNotStealEnclosingMethodNames ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Container" visibility="public" extends="java.lang.Object">
				      <method name="entrySet" return="java.lang.Object" visibility="public" />
				    </class>
				    <class name="Container.EntrySet" visibility="internal" extends="java.lang.Object" />
				  </package>
				</api>
				""", beforeFillProperties: gens => ManagedContractProjection.AllocateTypeNames (gens));

			var container = types.Single (t => t.Name == "Container");
			Assert.AreEqual ("EntrySet", container.Methods.Single ().Name);
			Assert.AreEqual ("EntrySetType", container.NestedTypes.Single ().Name);
			Assert.AreEqual ("example.Container.EntrySet", container.NestedTypes.Single ().JavaName);
		}

		[Test]
		public void PublicCallbacksKeepNamesWhenRestrictedAidlInterfacesCollide ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="Callback" visibility="public" />
				  <interface name="ICallback" visibility="internal" />
				</package></api>
				""", beforeFillProperties: gens => ManagedContractProjection.AllocateTypeNames (gens));

			Assert.AreEqual ("Example.ICallback", types.Single (t => t.JavaName == "example.Callback").FullName);
			Assert.AreEqual ("Example.ICallbackType", types.Single (t => t.JavaName == "example.ICallback").FullName);
		}

		[Test]
		public void InaccessibleArrayParametersKeepTheWholeNativeArrayOpaque ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Scheduler" visibility="internal" />
				    <class name="Factory" visibility="public" extends="java.lang.Object">
				      <method name="schedulers" return="void" visibility="public" jni-signature="([Lexample/Scheduler;)V">
				        <parameter name="values" type="example.Scheduler..." />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var method = types.Single (t => t.Name == "Factory").Methods.Single ();
			Assert.AreEqual ("Java.Lang.Object", method.Parameters [0].Type);
			Assert.AreEqual ("[Lexample/Scheduler;", method.Parameters [0].JniType);
			Assert.AreEqual ("([Lexample/Scheduler;)V", method.JniSignature);
		}

		[Test]
		public void InternalNestedTypesCannotDropPublicConstants ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Owner" visibility="public" extends="java.lang.Object">
				      <field name="CONSTANT" type="int" visibility="public" static="true" final="true" value="2" />
				    </class>
				    <class name="Owner.Constant" visibility="internal" extends="java.lang.Object" />
				  </package>
				</api>
				""", beforeFillProperties: gens => ManagedContractProjection.AllocateTypeNames (gens));

			var owner = types.Single (t => t.Name == "Owner");
			Assert.AreEqual ("ConstantType", owner.NestedTypes.Single ().Name);
			StringAssert.Contains ("[Register (\"CONSTANT\")]", GetGeneratedTypeOutput (owner));
		}

		[Test]
		public void RestrictedInterfacePropertiesRetainHiddenAccessorContracts ()
		{
			options.ExperimentalSemanticModel = true;
			const string hidden = "[global::System.ComponentModel.EditorBrowsable (global::System.ComponentModel.EditorBrowsableState.Never)]";
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="Contract" visibility="public">
				    <method name="getValue" return="int" visibility="public" abstract="true" />
				  </interface>
				</package></api>
				""");
			var contract = types.Single ();
			var property = contract.Properties.Single ();
			property.Getter.CustomAttributes = hidden;
			var writer = new generator.SourceWriters.BoundInterfacePropertyDeclaration (contract, property, "", options);
			Assert.IsTrue (writer.Attributes.OfType<generator.SourceWriters.EditorBrowsableAttr> ().Any ());
			Assert.IsTrue (writer.GetterAttributes.OfType<generator.SourceWriters.CustomAttr> ().Any ());
		}

		[Test]
		public void InternalPeersKeepInternalBaseContractsInsteadOfFlatteningThem ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="internal" abstract="true" extends="java.lang.Object">
				      <method name="getValue" return="int" visibility="public" abstract="true" />
				    </class>
				    <class name="Child" visibility="internal" extends="example.Base">
				      <method name="getValue" return="int" visibility="public" />
				    </class>
				  </package>
				</api>
				""");
			var child = types.OfType<ClassGen> ().Single (t => t.Name == "Child");
			Assert.AreEqual ("example.Base", child.BaseGen.JavaName);
			ManagedContractProjection.Apply (types, options);
			StringAssert.Contains ("override unsafe int Value", GetGeneratedTypeOutput (child));
		}

		[Test]
		public void PhysicalBridgesRestoreErasedBaseOverridesAfterPrivateParameterProjection ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Hidden" visibility="internal" extends="java.lang.Object" />
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="get" return="java.lang.Object" visibility="public" abstract="true" jni-signature="(Ljava/lang/Object;)Ljava/lang/Object;">
				        <parameter name="value" type="java.lang.Object" />
				      </method>
				    </class>
				    <class name="Child" visibility="public" extends="example.Base">
				      <method name="get" return="java.lang.Object" visibility="public" jni-signature="(Lexample/Hidden;)Ljava/lang/Object;"
				        managed-bridge-signatures="(Ljava/lang/Object;)Ljava/lang/Object;">
				        <parameter name="value" type="example.Hidden" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var child = types.Single (t => t.Name == "Child");
			var method = child.Methods.Single ();
			Assert.IsTrue (method.IsOverride);
			Assert.AreEqual ("Java.Lang.Object", method.Parameters [0].Type);
			Assert.AreEqual ("(Lexample/Hidden;)Ljava/lang/Object;", method.JniSignature);
		}

		[Test]
		public void InterfaceInvokersUseExplicitMethodsWhenPropertiesShareTheirNames ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Parent" visibility="public">
				      <method name="values" return="java.lang.Object" visibility="public" abstract="true" />
				    </interface>
				    <interface name="Child" visibility="public">
				      <implements name="example.Parent" name-generic-aware="example.Parent" />
				      <method name="getValues" return="java.lang.Object" visibility="public" abstract="true" />
				    </interface>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var child = types.Single (t => t.Name == "IChild");
			StringAssert.Contains ("Example.IParent.Values (", GetGeneratedTypeOutput (child));
		}

		[Test]
		public void OneNativeGetterCanImplementDistinctAbstractClrPropertyNames ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="getValue" return="int" visibility="public" abstract="true" jni-signature="()I" />
				    </class>
				    <class name="Child" visibility="public" extends="example.Base">
				      <method name="getValue" return="int" visibility="public" jni-signature="()I" />
				    </class>
				  </package>
				</api>
				""");
			var basis = types.Single (t => t.Name == "Base");
			var alias = basis.Properties.Single ().Getter.Clone (basis);
			alias.Name = "GetOtherValue";
			Assert.IsTrue (alias.Validate (options, basis.TypeParameters, new CodeGeneratorContext ()));
			basis.Properties.Add (new Property ("OtherValue") { Getter = alias });
			basis.RefreshPropertyLookup ();
			ManagedContractProjection.Apply (types, options);

			var child = types.Single (t => t.Name == "Child");
			Assert.IsTrue (child.Properties.Any (p => p.Name == "OtherValue" && !p.Getter.IsAbstract));
			StringAssert.Contains ("override unsafe int OtherValue", GetGeneratedTypeOutput (child));
		}

		[Test]
		public void ArraySpecializationsKeepOpaqueAbstractBaseContractsAndPhysicalJniSlots ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="get" return="java.lang.Object" visibility="public" abstract="true" jni-signature="()Ljava/lang/Object;" />
				      <method name="set" return="void" visibility="public" abstract="true" jni-signature="(Ljava/lang/Object;)V">
				        <parameter name="value" type="java.lang.Object" />
				      </method>
				    </class>
				    <class name="Child" visibility="public" extends="example.Base">
				      <method name="get" return="float[]" visibility="public" jni-signature="()[F"
				        managed-bridge-signatures="()Ljava/lang/Object;" />
				      <method name="set" return="void" visibility="public" jni-signature="([F)V"
				        managed-bridge-signatures="(Ljava/lang/Object;)V">
				        <parameter name="value" type="float[]" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var child = types.Single (t => t.Name == "Child");
			var getter = child.Methods.Single (m => m.JavaName == "get");
			var setter = child.Methods.Single (m => m.JavaName == "set");
			Assert.AreEqual ("Java.Lang.Object", getter.RetVal.FullName);
			Assert.AreEqual ("Java.Lang.Object", setter.Parameters [0].Type);
			Assert.AreEqual ("()[F", getter.JniSignature);
			Assert.AreEqual ("([F)V", setter.JniSignature);
			Assert.IsTrue (getter.IsOverride && setter.IsOverride);
		}

		[Test]
		public void ExcludedNominalCarriersValidatePublicSlotsWithoutEmittingPrivatePeers ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Hidden" visibility="internal" api-classification="Excluded" extends="java.lang.Object" />
				    <class name="Service" visibility="public" extends="java.lang.Object">
				      <method name="read" return="example.Hidden" visibility="public" jni-signature="()Lexample/Hidden;" />
				      <method name="write" return="void" visibility="public" jni-signature="(Lexample/Hidden;)V">
				        <parameter name="value" type="example.Hidden" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			Assert.IsFalse (types.Single (t => t.Name == "Hidden").IsGeneratable);
			var service = types.Single (t => t.Name == "Service");
			Assert.AreEqual (2, service.Methods.Count);
			Assert.AreEqual ("Java.Lang.Object", service.Methods.Single (m => m.JavaName == "read").RetVal.FullName);
			Assert.AreEqual ("Java.Lang.Object", service.Methods.Single (m => m.JavaName == "write").Parameters [0].Type);
			Assert.AreEqual ("()Lexample/Hidden;", service.Methods.Single (m => m.JavaName == "read").JniSignature);
		}

		[Test]
		public void ExactNativeOverloadsTakePrecedenceOverOtherPhysicalBridges ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="First" visibility="public" />
				    <interface name="Second" visibility="public" />
				    <class name="Base" visibility="public" extends="java.lang.Object">
				      <method name="map" return="example.Base" visibility="public" jni-signature="(Lexample/First;)Lexample/Base;">
				        <parameter name="value" type="example.First" />
				      </method>
				      <method name="map" return="example.Base" visibility="public" jni-signature="(Lexample/Second;)Lexample/Base;">
				        <parameter name="value" type="example.Second" />
				      </method>
				    </class>
				    <class name="Child" visibility="public" extends="example.Base">
				      <method name="map" return="example.Child" visibility="public" jni-signature="(Lexample/First;)Lexample/Child;"
				        managed-bridge-signatures="(Lexample/First;)Lexample/Base;|(Lexample/Second;)Lexample/Base;">
				        <parameter name="value" type="example.First" />
				      </method>
				      <method name="map" return="example.Child" visibility="public" jni-signature="(Lexample/Second;)Lexample/Child;"
				        managed-bridge-signatures="(Lexample/First;)Lexample/Base;|(Lexample/Second;)Lexample/Base;">
				        <parameter name="value" type="example.Second" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var child = types.Single (t => t.Name == "Child");
			CollectionAssert.AreEquivalent (new [] { "Example.IFirst", "Example.ISecond" },
				child.Methods.Select (m => m.Parameters [0].Type));
		}

		[Test]
		public void DisjointOverloadsCannotBorrowAnotherOverloadsBridgeContract ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="First" visibility="public" />
				    <interface name="Second" visibility="public" />
				    <class name="Base" visibility="public" extends="java.lang.Object">
				      <method name="map" return="example.Base" visibility="public" jni-signature="(Lexample/First;)Lexample/Base;">
				        <parameter name="value" type="example.First" />
				      </method>
				    </class>
				    <class name="Child" visibility="public" extends="example.Base">
				      <method name="map" return="example.Child" visibility="public" jni-signature="(Lexample/Second;)Lexample/Child;"
				        managed-bridge-signatures="(Lexample/First;)Lexample/Base;">
				        <parameter name="value" type="example.Second" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var method = types.Single (t => t.Name == "Child").Methods.Single ();
			Assert.AreEqual ("Example.ISecond", method.Parameters [0].Type);
			Assert.IsFalse (method.IsOverride);
		}

		[Test]
		public void CollectionBoundCallbackResultsUseTheirProjectedMarshaller ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Owner" visibility="public" extends="java.lang.Object">
				      <method name="copy" return="S" visibility="public" jni-signature="(Ljava/util/Set;)Ljava/util/Set;">
				        <typeParameters><typeParameter name="S"><genericConstraints>
				          <genericConstraint type="java.util.Set" />
				        </genericConstraints></typeParameter></typeParameters>
				        <parameter name="value" type="S" />
				      </method>
				    </class>
				  </package>
				</api>
				""");
			var method = types.Single (t => t.Name == "Owner").Methods.Single ();
			StringAssert.Contains ("JavaSet.ToLocalJniHandle", method.RetVal.ToNative (options, "value"));
		}

		[TestCase ("public")]
		[TestCase ("internal")]
		public void AbstractInterfacePropertiesExistBeforeDerivedPropertyPlanning (string visibility)
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition (("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Service" visibility="public">
				      <method name="getVersion" return="int" visibility="public" abstract="true" jni-signature="()I" />
				    </interface>
				    <class name="Stub" visibility="public" abstract="true" extends="java.lang.Object">
				      <implements name="example.Service" name-generic-aware="example.Service" />
				    </class>
				    <class name="Impl" visibility="public" extends="example.Stub">
				      <method name="getVersion" return="int" visibility="public" jni-signature="()I" />
				    </class>
				  </package>
				</api>
				""").Replace ("visibility=\"public\"", $"visibility=\"{visibility}\""));
			ManagedContractProjection.PrepareAbstractContracts (types, options);
			ManagedContractProjection.Apply (types, options);
			Assert.IsTrue (types.Single (t => t.Name == "Stub").Properties.Any (p => p.Name == "Version" && p.Getter.IsAbstract));
			StringAssert.Contains ("override unsafe int Version", GetGeneratedTypeOutput (types.Single (t => t.Name == "Impl")));
		}

		[Test]
		public void NestedTypeCollisionSuffixCannotBecomeItsEnclosingTypeName ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="NameType" visibility="public" extends="java.lang.Object">
				      <method name="name" return="int" visibility="public" />
				    </class>
				    <class name="NameType.Name" visibility="public" extends="java.lang.Object" />
				  </package>
				</api>
				""", beforeFillProperties: gens => ManagedContractProjection.AllocateTypeNames (gens));
			Assert.AreEqual ("NameTypeType", types.Single (t => t.Name == "NameType").NestedTypes.Single ().Name);
		}

		[Test]
		public void NativePropertyAndMethodDuplicatesShareOneCallback ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example"><class name="Owner" visibility="public" extends="java.lang.Object">
				    <method name="getWrapper" return="java.lang.Object" visibility="public" jni-signature="()Ljava/lang/Object;" />
				  </class></package>
				</api>
				""");
			var owner = types.Single (t => t.Name == "Owner");
			var duplicate = owner.Properties.Single ().Getter.Clone (owner);
			Assert.IsTrue (duplicate.Validate (options, owner.TypeParameters, new CodeGeneratorContext ()));
			owner.Methods.Add (duplicate);
			ManagedContractProjection.Apply (types, options);
			Assert.AreEqual (1, owner.GetAllMethods ().Count (m => m.JavaName == "getWrapper"));
		}

		[Test]
		public void DefaultInterfaceGetterOverridesUseTheInheritedPropertyShape ()
		{
			options.ExperimentalSemanticModel = options.SupportDefaultInterfaceMethods = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Parent" visibility="public">
				      <method name="getKey" return="java.lang.Object" visibility="public" abstract="true" jni-signature="()Ljava/lang/Object;" />
				    </interface>
				    <interface name="Child" visibility="public">
				      <implements name="example.Parent" name-generic-aware="example.Parent" />
				      <method name="getKey" return="java.lang.Object" visibility="public" abstract="false" jni-signature="()Ljava/lang/Object;" />
				    </interface>
				  </package>
				</api>
				""");
			var parent = types.Single (t => t.Name == "IParent");
			var child = types.Single (t => t.Name == "IChild");
			var getter = child.GetAllMethods ().Single ();
			child.Properties.Clear ();
			child.Methods.Clear ();
			child.Methods.Add (getter);
			getter.IsInterfaceDefaultMethod = true;
			getter.OverriddenInterfaceMethod = parent.Properties.Single ().Getter;
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (child);
			StringAssert.Contains ("Example.IParent.Key", output);
			StringAssert.DoesNotContain ("Example.IParent.GetKey (", output);
			StringAssert.DoesNotContain ("virtual unsafe Java.Lang.Object Example.IParent.Key", output);
			var property = new BoundProperty (child, child.Properties.Single (), options);
			Assert.IsFalse (property.IsVirtual);
			Assert.IsFalse (property.IsOverride);
		}

		[Test]
		public void NativeOnlyInterfacesDoNotDuplicateProtectedPropertyCallbacks ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Hidden" visibility="internal">
				      <method name="getWrapper" return="java.lang.Object" visibility="public" abstract="true" jni-signature="()Ljava/lang/Object;" />
				    </interface>
				    <class name="Owner" visibility="public" abstract="true" extends="java.lang.Object">
				      <implements name="example.Hidden" name-generic-aware="example.Hidden" />
				      <method name="getWrapper" return="java.lang.Object" visibility="protected" jni-signature="()Ljava/lang/Object;" />
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.PrepareAbstractContracts (types, options);
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Owner"));
			StringAssert.Contains ("Wrapper", output);
			StringAssert.DoesNotContain (" GetWrapper (", output);
		}

		[Test]
		public void ClassInvokersPreferInheritedClassDeclarationsToInterfaceAdapters ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Contract" visibility="public">
				      <method name="write" return="void" visibility="public" abstract="true" />
				    </interface>
				    <class name="Base" visibility="public" abstract="true" extends="java.lang.Object">
				      <implements name="example.Contract" name-generic-aware="example.Contract" />
				      <method name="write" return="void" visibility="public" abstract="true" />
				    </class>
				    <class name="Middle" visibility="public" abstract="true" extends="example.Base" />
				    <class name="Child" visibility="public" abstract="true" extends="example.Middle" />
				  </package>
				</api>
				""");
			var basis = types.OfType<ClassGen> ().Single (t => t.Name == "Base");
			basis.ExplicitlyImplementedInterfaceMethods.Add (basis.Methods.Single ().GetSignature ());
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Child"));
			StringAssert.Contains ("override unsafe void Write (", output);
			StringAssert.DoesNotContain ("IContract.Write (", output);
		}

		[Test]
		public void DerivedTypesDoNotSplitInheritedPropertyOverridesIntoMethods ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Descriptor" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="getOperation" return="example.Operation" visibility="public" abstract="true"
				        jni-signature="()Lexample/Operation;" />
				    </class>
				    <class name="Operation" visibility="public" extends="example.Descriptor">
				      <method name="getOperation" return="example.Operation" visibility="public"
				        jni-signature="()Lexample/Operation;" />
				    </class>
				  </package>
				</api>
				""", beforeFillProperties: gens => ManagedContractProjection.AllocateTypeNames (gens));
			ManagedContractProjection.Apply (types, options);
			var derived = types.Single (t => t.JavaName == "example.Operation");
			Assert.AreEqual ("OperationType", derived.Name);
			Assert.AreEqual ("Operation", derived.Properties.Single ().Name);
			Assert.AreEqual ("example.Operation", derived.JavaName);
			var output = GetGeneratedTypeOutput (derived);
			StringAssert.Contains ("override unsafe Example.OperationType Operation", output);
			StringAssert.DoesNotContain ("override unsafe Example.OperationType GetOperation (", output);
		}

		[Test]
		public void InheritedPropertiesAloneDoNotRenameDerivedTypes ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Descriptor" visibility="public" abstract="true" extends="java.lang.Object">
				      <method name="getOperation" return="example.Operation" visibility="public" abstract="true" />
				    </class>
				    <class name="Operation" visibility="public" abstract="true" extends="example.Descriptor" />
				  </package>
				</api>
				""", beforeFillProperties: gens => ManagedContractProjection.AllocateTypeNames (gens));
			Assert.AreEqual ("Operation", types.Single (t => t.JavaName == "example.Operation").Name);
		}

		[Test]
		public void PublicCollectionSignaturesDoNotExposeInternalElementPeers ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="InternalValue" visibility="internal" extends="java.lang.Object" />
				    <class name="PublicOwner" visibility="public" extends="java.lang.Object">
				      <method name="read" return="java.util.List&lt;example.InternalValue&gt;" visibility="public" />
				      <method name="write" return="void" visibility="public"><parameter name="values" type="java.util.List&lt;example.InternalValue&gt;" /></method>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var owner = types.Single (t => t.Name == "PublicOwner");
			StringAssert.Contains ("Java.Lang.Object", owner.Methods.Single (m => m.JavaName == "read").RetVal.FullName);
			StringAssert.DoesNotContain ("InternalValue", owner.Methods.Single (m => m.JavaName == "write").Parameters [0].Type);
			StringAssert.Contains ("IList", owner.Methods.Single (m => m.JavaName == "read").RetVal.FullName);
		}

		[Test]
		public void DefaultMethodsDoNotExplicitlyImplementOmittedNativeInterfaces ()
		{
			options.ExperimentalSemanticModel = true;
			options.SupportDefaultInterfaceMethods = true;
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="Hidden" visibility="">
				    <method name="run" return="void" visibility="public" abstract="true" />
				  </interface>
				  <interface name="Public" visibility="public">
				    <implements name="example.Hidden" name-generic-aware="example.Hidden" />
				    <method name="run" return="void" visibility="public" abstract="false" />
				  </interface>
				</package></api>
				""");
			var decisions = ManagedContractProjection.Apply (types, options);
			var iface = types.Single (t => t.Name == "IPublic");
			var output = GetGeneratedTypeOutput (iface);

			Assert.IsNull (iface.Methods.Single ().OverriddenInterfaceMethod);
			Assert.IsTrue (decisions.Any (d => d.Reason == "retain-default-method-without-inaccessible-clr-contract"));
			StringAssert.DoesNotContain ("IHidden", output);
			StringAssert.Contains ("unsafe void Run ()", output);
			StringAssert.Contains ("run.()V", output);
		}

		[Test]
		public void PublicNativeAncestorsSurviveInaccessibleInterfaceEdges ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <interface name="Marker" visibility="public" />
				    <interface name="Hidden" visibility="">
				      <implements name="example.Marker" name-generic-aware="example.Marker" />
				    </interface>
				    <interface name="Public" visibility="public">
				      <implements name="example.Hidden" name-generic-aware="example.Hidden" />
				    </interface>
				    <class name="Peer" visibility="public" extends="java.lang.Object">
				      <implements name="example.Hidden" name-generic-aware="example.Hidden" />
				    </class>
				  </package>
				</api>
				""");
			var decisions = ManagedContractProjection.Apply (types, options);
			foreach (var name in new [] { "IPublic", "Peer" }) {
				var type = types.Single (t => t.Name == name);
				Assert.IsTrue (type.Interfaces.Any (i => i.FullName == "Example.IMarker"), name);
				var output = GetGeneratedTypeOutput (type);
				StringAssert.Contains ("Example.IMarker", output);
				StringAssert.DoesNotContain ("Example.IHidden", output);
			}
			Assert.AreEqual (2, decisions.Count (d => d.Reason == "retain-public-contract-through-inaccessible-interface"));
		}

		[Test]
		public void InaccessibleNativeListenersDoNotProduceJavaImplementors ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition ("""
				<api><package name="example">
				  <interface name="HiddenListener" visibility="">
				    <method name="run" return="void" visibility="public" abstract="true" />
				  </interface>
				  <interface name="PublicListener" visibility="public">
				    <method name="run" return="void" visibility="public" abstract="true" />
				  </interface>
				</package></api>
				""");
			var hidden = types.Single (t => t.Name == "IHiddenListener");
			Assert.IsTrue (((InterfaceGen) hidden).IsListener);
			ManagedContractProjection.Apply (types, options);

			Assert.IsFalse (((InterfaceGen) hidden).IsListener);
			StringAssert.DoesNotContain ("IHiddenListenerImplementor", GetGeneratedTypeOutput (hidden));
			StringAssert.Contains ("IPublicListenerImplementor", GetGeneratedTypeOutput (types.Single (t => t.Name == "IPublicListener")));
		}

		[Test]
		public void ExplicitInterfaceNamesAreQualifiedInsideShadowingNamespaces ()
		{
			options.ExperimentalSemanticModel = true;
			options.SupportDefaultInterfaceMethods = true;
			options.UseGlobal = true;
			var types = ParseApiDefinition ("""
				<api>
				  <package name="android.published">
				    <interface name="Parent" visibility="public">
				      <method name="run" return="void" visibility="public" abstract="false" />
				    </interface>
				  </package>
				  <package name="com.google.android">
				    <interface name="Child" visibility="public">
				      <implements name="android.published.Parent" name-generic-aware="android.published.Parent" />
				      <method name="run" return="void" visibility="public" abstract="false" />
				    </interface>
				  </package>
				</api>
				""");
			var child = types.Single (t => t.Name == "IChild");

			StringAssert.Contains ("void global::Android.Published.IParent.Run ()", GetGeneratedTypeOutput (child));
		}
	}
}
