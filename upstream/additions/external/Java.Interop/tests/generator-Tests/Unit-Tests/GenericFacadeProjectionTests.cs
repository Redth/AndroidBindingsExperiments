using System.Linq;
using System.Xml.Linq;
using Xamarin.Android.Tools.Bytecode;
using Java.Interop.Tools.Generator.Semantics;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class GenericFacadeProjectionTests : CodeGeneratorTestBase
	{
		const string api = """
			<api api-source="class-parse"><package name="java.lang">
			  <class name="Object" visibility="public" jni-signature="Ljava/lang/Object;" />
			  <class name="String" visibility="public" extends="java.lang.Object" jni-signature="Ljava/lang/String;" />
			  <interface name="CharSequence" visibility="public" jni-signature="Ljava/lang/CharSequence;" />
			</package><package name="example">
			  <class name="Box" visibility="public" extends="java.lang.Object" jni-signature="Lexample/Box;">
			    <typeParameters><typeParameter name="T" /></typeParameters>
			    <constructor name="Box" visibility="public" jni-signature="(Ljava/lang/Object;)V"><parameter name="value" type="T" /></constructor>
			    <method name="get" visibility="public" return="T" jni-signature="()Ljava/lang/Object;" />
			    <method name="set" visibility="public" return="void" jni-signature="(Ljava/lang/Object;)V"><parameter name="value" type="T" /></method>
			    <method name="array" visibility="public" return="T[]" jni-signature="([Ljava/lang/Object;)[Ljava/lang/Object;"><parameter name="value" type="T[]" /></method>
			    <method name="primitiveArray" visibility="public" return="int[]" jni-signature="([I)[I"><parameter name="value" type="int[]" /></method>
			    <method name="echo" visibility="public" return="V" jni-signature="(Ljava/lang/Object;)Ljava/lang/Object;">
			      <typeParameters><typeParameter name="V" /></typeParameters><parameter name="value" type="V" />
			    </method>
			    <method name="hidden" visibility="public" return="T" annotated-visibility="LIBRARY" jni-signature="()Ljava/lang/Object;" />
			    <method name="covariant" visibility="public" return="example.Box&lt;? extends java.lang.String&gt;" jni-signature="()Lexample/Box;" />
			  </class>
			  <class name="Derived" visibility="public" extends="example.Box" extends-generic-aware="example.Box&lt;java.util.List&lt;U&gt;&gt;" jni-signature="Lexample/Derived;">
			    <typeParameters><typeParameter name="U" /></typeParameters>
			  </class>
			  <class name="Bounded" visibility="public" extends="java.lang.Object" jni-signature="Lexample/Bounded;">
			    <typeParameters><typeParameter name="T"><genericConstraints><genericConstraint type="java.lang.CharSequence" /></genericConstraints></typeParameter></typeParameters>
			    <method name="get" visibility="public" return="T" jni-signature="()Ljava/lang/CharSequence;" />
			  </class>
			</package></api>
			""";

		GenericFacadeProjection Create ()
		{
			options.ExperimentalSemanticModel = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			var types = ParseApiDefinition (api);
			return new GenericFacadeProjection (source, types, options);
		}

		[Test]
		public void TypeParametersAndMethodParametersBecomeTypedViewsNotRegisteredPeers ()
		{
			var projection = Create ();
			var output = projection.GenerateType ("example.Box");

			StringAssert.Contains ("class BoxView<", output);
			StringAssert.Contains ("DynamicallyAccessedMembers", output);
			StringAssert.Contains ("@T? @Get", output);
			StringAssert.Contains ("@Echo<", output);
			StringAssert.Contains ("JvmType<@V>", output);
			StringAssert.Contains (".CreateArgument (@value)", output);
			StringAssert.Contains ("this.Peer", output);
			StringAssert.DoesNotContain ("Runtime.Register", output);
			StringAssert.DoesNotContain ("@Hidden", output);
		}

		[Test]
		public void InheritedArgumentsAreSubstitutedThroughNestedGenericResults ()
		{
			var output = Create ().GenerateType ("example.Derived");

			StringAssert.Contains ("KotlinList<@U?>", output);
			StringAssert.Contains ("GetListType (__type_U)", output);
			StringAssert.DoesNotContain ("JvmType<@T>", output);
		}

		[Test]
		public void InheritedStaticMethodVariablesShadowGenericBaseSubstitutions ()
		{
			options.ExperimentalSemanticModel = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			source.Descendants ("class").Single (c => (string) c.Attribute ("name") == "Box").Add (XElement.Parse ("""
				<method name="createBox" visibility="public" static="true" return="example.Box&lt;T&gt;" jni-signature="(Ljava/lang/Object;)Lexample/Box;">
				  <typeParameters><typeParameter name="T" /></typeParameters>
				  <parameter name="value" type="T" />
				</method>
				"""));
			var types = ParseApiDefinition (source.ToString ());
			var output = new GenericFacadeProjection (source, types, options).GenerateType ("example.Derived");
			StringAssert.Contains ("static Example.BoxView<@T>", output);
			StringAssert.Contains ("BoxView<@T>.GetJvmType (__type_T)", output);
		}

		[Test]
		public void MethodSignaturesUseGenericPositionsRatherThanParameterNames ()
		{
			options.ExperimentalSemanticModel = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			var box = source.Descendants ("class").Single (c => (string) c.Attribute ("name") == "Box");
			box.Add (XElement.Parse ("""
				<method name="copyOf" static="true" visibility="public" return="void" jni-signature="([Ljava/lang/Object;)V">
				  <typeParameters><typeParameter name="E" /></typeParameters>
				  <parameter name="values" type="E[]" />
				</method>
				"""), XElement.Parse ("""
				<method name="copyOf" static="true" visibility="public" return="void" jni-signature="([Ljava/lang/CharSequence;)V">
				  <typeParameters><typeParameter name="Z" classBound="java.lang.CharSequence" /></typeParameters>
				  <parameter name="values" type="Z[]" />
				</method>
				"""));
			var types = ParseApiDefinition (source.ToString ());
			var output = new GenericFacadeProjection (source, types, options).GenerateType ("example.Box");
			StringAssert.Contains ("@CopyOfNative", output);
			StringAssert.Contains ("copyOf.([Ljava/lang/Object;)V", output);
			StringAssert.Contains ("copyOf.([Ljava/lang/CharSequence;)V", output);
		}

		[Test]
		public void GenericBoundsAreCheckedAgainstNativeAssignability ()
		{
			var projection = Create ();
			var output = projection.GenerateType ("example.Bounded");

			StringAssert.Contains ("RequireAssignableTo (\"java/lang/CharSequence\")", output);
			Assert.IsTrue (projection.Decisions.Any (d => d.Reason == "runtime-validated-erased-java-bound"));
		}

		[Test]
		public void RawClassParseInterfaceBoundsAreNotLostBeforeXmlAdjustment ()
		{
			options.ExperimentalSemanticModel = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			var parameter = source.Descendants ("class").Single (c => (string) c.Attribute ("name") == "Bounded")
				.Element ("typeParameters").Element ("typeParameter");
			parameter.Element ("genericConstraints").Remove ();
			parameter.SetAttributeValue ("classBound", "");
			parameter.SetAttributeValue ("interfaceBounds", "java.lang.CharSequence:java.lang.Comparable<T>");
			var types = ParseApiDefinition (source.ToString ());
			var output = new GenericFacadeProjection (source, types, options).GenerateType ("example.Bounded");

			StringAssert.Contains ("RequireAssignableTo (\"java/lang/CharSequence\")", output);
			StringAssert.Contains ("RequireAssignableTo (\"java/lang/Comparable\")", output);
		}

		[Test]
		public void GenericArraysUseBoxedReferenceArrayViewsNotPrimitiveArrays ()
		{
			var output = Create ().GenerateType ("example.Box");

			StringAssert.Contains ("JvmArray<@T>", output);
			StringAssert.Contains ("JvmType<int[]>.Default", output);
			StringAssert.Contains ("NewObject", output);
		}

		[Test]
		public void ExistentialNominalTypesDoNotAcquireUnsafeMutableFacades ()
		{
			var projection = Create ();
			var output = projection.GenerateType ("example.Box");

			Assert.IsTrue (projection.Decisions.Any (d => d.Reason == "existential-mutable-contract-retains-raw-peer"));
			StringAssert.DoesNotContain ("BoxView<string>", output);
		}

		[Test]
		public void GenericFieldsUseTypedReferenceReadsAndWrites ()
		{
			options.ExperimentalSemanticModel = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			source.Descendants ("class").Single (c => (string) c.Attribute ("name") == "Box").Add (XElement.Parse ("""
				<field name="slot" visibility="public" type="java.lang.Object" type-generic-aware="T" jni-signature="Ljava/lang/Object;" />
				"""));
			var types = ParseApiDefinition (source.ToString ());
			var output = new GenericFacadeProjection (source, types, options).GenerateType ("example.Box");

			StringAssert.Contains ("@T? @Slot", output);
			StringAssert.Contains ("InstanceFields.GetObjectValue", output);
			StringAssert.Contains ("InstanceFields.SetValue", output);
			StringAssert.Contains ("__type_T.CreateArgument (value)", output);
		}

		[Test]
		public void SuspendViewsKeepClassTypeParametersInTaskResults ()
		{
			options.ExperimentalSemanticModel = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			source.Root.Add (XElement.Parse ("""
				<package name="kotlin.coroutines"><interface name="Continuation" visibility="public" jni-signature="Lkotlin/coroutines/Continuation;" /></package>
				"""));
			var method = XElement.Parse ("""
				<method name="delayed" return="java.lang.Object" visibility="public" kotlin-suspend="true" jni-signature="(Ljava/lang/Object;Lkotlin/coroutines/Continuation;)Ljava/lang/Object;">
				  <parameter name="value" type="T" /><parameter name="continuation" type="kotlin.coroutines.Continuation" />
				</method>
				""");
			method.SetAttributeValue ("kotlin-return-type", new KotlinTypeContract { TypeParameter = "T" }.ToJson ());
			source.Descendants ("class").Single (c => (string) c.Attribute ("name") == "Box").Add (method);
			var types = ParseApiDefinition (source.ToString ());
			var output = new GenericFacadeProjection (source, types, options).GenerateType ("example.Box");

			StringAssert.Contains ("Task<@T?> @DelayedAsync", output);
			StringAssert.Contains ("KotlinCoroutines.RunAsync", output);
			StringAssert.Contains ("__continuation.PeerReference", output);
			StringAssert.Contains ("__type_T.Read", output);
		}

		[Test]
		public void GenericFlowViewsExposeTheirTypedElementConverter ()
		{
			options.ExperimentalSemanticModel = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse ("""
				<api api-source="class-parse"><package name="kotlinx.coroutines.flow">
				  <interface name="Flow" visibility="public" jni-signature="Lkotlinx/coroutines/flow/Flow;">
				    <typeParameters><typeParameter name="T" /></typeParameters>
				  </interface>
				</package></api>
				""");
			var types = ParseApiDefinition (source.ToString ());
			var output = new GenericFacadeProjection (source, types, options).GenerateType ("kotlinx.coroutines.flow.Flow");

			StringAssert.Contains ("IAsyncEnumerable<@T?> AsAsyncEnumerable", output);
			StringAssert.Contains ("__type_T.Read", output);
		}
	}
}
