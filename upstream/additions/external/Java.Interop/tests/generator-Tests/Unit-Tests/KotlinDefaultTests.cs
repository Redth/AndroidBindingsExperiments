using System;
using System.Linq;
using Java.Interop.Kotlin;
using Java.Interop.Tools.Generator.Semantics;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class KotlinDefaultTests : CodeGeneratorTestBase
	{
		const string api = """
			<api><package name="java.lang"><class name="Object" visibility="public" /></package>
			<package name="example"><class name="Service" visibility="public" extends="java.lang.Object">
			  <method name="compute" visibility="public" return="int"
			    kotlin-default-owner="example/Service" kotlin-default-receiver="true"
			    kotlin-default-mask-count="1" kotlin-default-jni-signature="(Lexample/Service;IIIILjava/lang/Object;)I">
			    <parameter name="optional" type="int" kotlin-default-argument-index="0" />
			    <parameter name="required" type="int" />
			    <parameter name="last" type="int" kotlin-default-argument-index="2" />
			  </method>
			</class></package></api>
			""";

		[Test]
		public void OptionalValuesDistinguishOmissionNullAndZero ()
		{
			KotlinOptional<int> omitted = default;
			KotlinOptional<int> zero = 0;
			KotlinOptional<string> explicitNull = new KotlinOptional<string> (null);
			Assert.IsFalse (omitted.IsSpecified);
			Assert.Throws<InvalidOperationException> (() => { _ = omitted.Value; });
			Assert.IsTrue (zero.IsSpecified);
			Assert.AreEqual (0, zero.Value);
			Assert.IsTrue (explicitNull.IsSpecified);
			Assert.IsNull (explicitNull.Value);
		}

		[Test]
		public void RequiredFacadeParametersMoveBeforeOptionalArgumentsButNativeOrderDoesNot ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition (api);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ("ComputeWithDefaults (int required, global::Java.Interop.Kotlin.KotlinOptional<int> optional = default,", output);
			StringAssert.Contains ("(this, optional.GetValueOrDefault (), required, last.GetValueOrDefault (),", output);
			StringAssert.Contains ("(optional.IsSpecified ? 0 : (1 << 0)) | (last.IsSpecified ? 0 : (1 << 2))", output);
			StringAssert.Contains ("Register (\"compute$default\", \"(Lexample/Service;IIIILjava/lang/Object;)I\"", output);
		}

		[Test]
		public void DefaultHelpersRetainProjectedCollectionParameterSymbols ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var source = api.Replace ("<class name=\"Service\"", "<class name=\"Hidden\" visibility=\"internal\" extends=\"java.lang.Object\" /><class name=\"Service\"")
				.Replace ("<parameter name=\"optional\" type=\"int\"",
				"<parameter name=\"optional\" type=\"java.util.List&lt;example.Hidden&gt;\"")
				.Replace ("IIIILjava/lang/Object;)I", "Ljava/util/List;IIILjava/lang/Object;)I");
			var types = ParseApiDefinition (source);
			ManagedContractProjection.Apply (types, options);
			var service = types.Single (t => t.Name == "Service");
			var original = service.Methods.Single ().Parameters [0];
			var output = GetGeneratedTypeOutput (service);
			StringAssert.Contains (options.GetTypeReferenceName (original), output);
			StringAssert.DoesNotContain ("System.Collections.IList?", output);
		}

		[Test]
		public void MaskedConstructorsRetainProjectedDictionaryArguments ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="example">
				    <class name="Hidden" visibility="internal" extends="java.lang.Object" />
				    <class name="Service" visibility="public" extends="java.lang.Object">
				      <constructor name="Service" visibility="public" kotlin-default-mask-count="1"
				        kotlin-default-jni-signature="(Ljava/util/Map;ILkotlin/jvm/internal/DefaultConstructorMarker;)V">
				        <parameter name="values" type="java.util.Map&lt;java.lang.Object, example.Hidden&gt;" kotlin-default-argument-index="0" />
				      </constructor>
				    </class>
				  </package>
				</api>
				""");
			ManagedContractProjection.Apply (types, options);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ("System.Collections.Generic.IDictionary", output);
			StringAssert.DoesNotContain ("System.Collections.IDictionary", output);
		}

		[Test]
		public void DefaultConstructorFactoriesDisambiguateTheVerifiedMarkerParameter ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition ("""
				<api><package name="java.lang"><class name="Object" visibility="public" /></package>
				  <package name="kotlin.jvm.internal"><class name="DefaultConstructorMarker" visibility="public" extends="java.lang.Object" /></package>
				  <package name="example">
				    <class name="Other" visibility="public" extends="java.lang.Object" />
				    <class name="Service" visibility="public" extends="java.lang.Object">
				      <constructor name="Service" visibility="public" kotlin-default-mask-count="1"
				        kotlin-default-jni-signature="(IILkotlin/jvm/internal/DefaultConstructorMarker;)V">
				        <parameter name="value" type="int" kotlin-default-argument-index="0" />
				      </constructor>
				      <constructor name="Service" visibility="public">
				        <parameter name="value" type="int" />
				        <parameter name="flags" type="int" />
				        <parameter name="other" type="example.Other" />
				      </constructor>
				    </class>
				  </package>
				</api>
				""");
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ("DefaultConstructorMarker) null", output);
		}

		[Test]
		public void DefaultConveniencesAreOptIn ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition (api);
			StringAssert.DoesNotContain ("ComputeWithDefaults", GetGeneratedTypeOutput (types.Single (t => t.Name == "Service")));
		}

		[Test]
		public void CharacterSequenceDefaultsCallTheEmittedFormattedShim ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var source = api.Replace ("<class name=\"Object\" visibility=\"public\" />",
				"<class name=\"Object\" visibility=\"public\" /><interface name=\"CharSequence\" visibility=\"public\" />")
				.Replace ("return=\"int\"", "return=\"java.lang.CharSequence\"")
				.Replace ("IIIILjava/lang/Object;)I", "IIIILjava/lang/Object;)Ljava/lang/CharSequence;");
			var types = ParseApiDefinition (source);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			var call = System.Text.RegularExpressions.Regex.Match (output, @"return (__InvokeKotlinDefault_\w+) \(");
			Assert.IsTrue (call.Success);
			StringAssert.EndsWith ("Formatted", call.Groups [1].Value);
			StringAssert.Contains ("ICharSequence " + call.Groups [1].Value + " (", output);
		}

		[TestCase ("-1")]
		[TestCase ("32")]
		public void InvalidMaskIndicesFailExplicitly (string index)
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition (api.Replace ("kotlin-default-argument-index=\"0\"", $"kotlin-default-argument-index=\"{index}\""));
			Assert.Throws<System.IO.InvalidDataException> (() => GetGeneratedTypeOutput (types.Single (t => t.Name == "Service")));
		}
	}
}
