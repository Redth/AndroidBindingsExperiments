using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xamarin.Android.Tools.Bytecode;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class KotlinSuspendTests : CodeGeneratorTestBase
	{
		const string api = """
			<api><package name="java.lang"><class name="Object" visibility="public" /></package>
			<package name="kotlin.coroutines"><interface name="Continuation" visibility="public" /></package>
			<package name="example"><class name="Service" visibility="public" extends="java.lang.Object">
			  <method name="read" visibility="public" return="java.lang.Object" final="true"
			    kotlin-suspend="true" kotlin-suspend-return-type="kotlin/String" kotlin-suspend-return-nullable="false">
			    <parameter name="cancellationToken" type="int" />
			    <parameter name="continuation" type="kotlin.coroutines.Continuation" />
			  </method>
			</class></package></api>
			""";

		[Test]
		public void SuspendConveniencesAreStrictlyOptIn ()
		{
			options.ExperimentalSemanticModel = true;
			var types = ParseApiDefinition (api);
			StringAssert.DoesNotContain ("ReadAsync", GetGeneratedTypeOutput (types.Single (t => t.Name == "Service")));
		}

		[Test]
		public void SuspendWrapperUsesRuntimeAndPreservesRawMethod ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition (api);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ("Task<string> ReadAsync (int cancellationToken, global::System.Threading.CancellationToken _cancellationToken = default,", output);
			StringAssert.Contains ("RunAsync (__continuation => Read (cancellationToken, global::Java.Interop.JavaObjectExtensions.JavaCast<Kotlin.Coroutines.IContinuation> (__continuation))", output);
			StringAssert.Contains ("KotlinCoroutines.GetString, _cancellationToken, coroutineContext)", output);
			StringAssert.DoesNotContain ("Task.Run", output);
			StringAssert.Contains ("unsafe Java.Lang.Object Read (", output);
		}

		[TestCase ("kotlin/Int", false, "int", "GetInt32")]
		[TestCase ("kotlin/Int", true, "int?", "GetNullableInt32")]
		[TestCase ("kotlin/Boolean", false, "bool", "GetBoolean")]
		[TestCase ("kotlin/Boolean", true, "bool?", "GetNullableBoolean")]
		[TestCase ("kotlin/Long", false, "long", "GetInt64")]
		[TestCase ("kotlin/Long", true, "long?", "GetNullableInt64")]
		[TestCase ("kotlin/Double", false, "double", "GetDouble")]
		[TestCase ("kotlin/Double", true, "double?", "GetNullableDouble")]
		[TestCase ("kotlin/Float", false, "float", "GetSingle")]
		[TestCase ("kotlin/Float", true, "float?", "GetNullableSingle")]
		[TestCase ("kotlin/Short", false, "short", "GetInt16")]
		[TestCase ("kotlin/Short", true, "short?", "GetNullableInt16")]
		[TestCase ("kotlin/Byte", false, "sbyte", "GetSByte")]
		[TestCase ("kotlin/Byte", true, "sbyte?", "GetNullableSByte")]
		[TestCase ("kotlin/Char", false, "char", "GetChar")]
		[TestCase ("kotlin/Char", true, "char?", "GetNullableChar")]
		[TestCase ("kotlin/String", true, "string?", "GetNullableString")]
		[TestCase ("example/Result", false, "global::Java.Lang.Object?", "value => value")]
		public void SuspendResultsDoNotGuessFromErasedObject (string kotlinType, bool nullable, string managedType, string converter)
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = options.SupportNullableReferenceTypes = true;
			var types = ParseApiDefinition (api.Replace ("kotlin/String", kotlinType)
				.Replace ("kotlin-suspend-return-nullable=\"false\"", $"kotlin-suspend-return-nullable=\"{nullable.ToString ().ToLowerInvariant ()}\""));
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ($"Task<{managedType}> ReadAsync", output);
			StringAssert.Contains (converter, output);
		}

		[Test]
		public void AContinuationParameterAloneDoesNotMakeASuspendFunction ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition (api.Replace ("kotlin-suspend=\"true\"", ""));
			StringAssert.DoesNotContain ("ReadAsync", GetGeneratedTypeOutput (types.Single (t => t.Name == "Service")));
		}

		[Test]
		public void UnitResultDoesNotExposeAResultObject ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition (api.Replace ("kotlin/String", "kotlin/Unit"));
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ("Task ReadAsync", output);
			StringAssert.Contains ("RunUnitAsync", output);
		}

		[Test]
		public void NativeAsyncNamesArePreservedAndSuspendOverloadsStayTogether ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition (api.Replace ("</class></package></api>", """
				  <method name="read" visibility="public" return="java.lang.Object" final="true" kotlin-suspend="true" kotlin-suspend-return-type="kotlin/String">
				    <parameter name="continuation" type="kotlin.coroutines.Continuation" />
				  </method>
				  <method name="readAsync" visibility="public" return="int" final="true" />
				</class></package></api>
				"""));
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			Assert.AreEqual (2, output.Split ("Task<string> ReadKotlinAsync (").Length - 1);
			StringAssert.DoesNotContain ("ReadKotlinAsync2", output);
			StringAssert.Contains ("int ReadAsync ()", output);
		}

		[Test]
		public void InterfaceConvenienceDoesNotAddAnAbstractJniSlot ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = options.SupportDefaultInterfaceMethods = true;
			var source = api.Replace ("<class name=\"Service\" visibility=\"public\" extends=\"java.lang.Object\">",
				"<interface name=\"Service\" visibility=\"public\">").Replace ("</class></package></api>", "</interface></package></api>");
			var types = ParseApiDefinition (source);
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "IService"));
			StringAssert.Contains ("Task<string> ReadAsync", output);
			StringAssert.Contains ("return global::Java.Interop.Kotlin.KotlinCoroutines.RunAsync", output);
			StringAssert.DoesNotContain ("Register (\"readAsync\"", output);
		}

		[Test]
		public void SuspendMetadataWithAnInvalidAbiFailsExplicitly ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = true;
			var types = ParseApiDefinition (api.Replace ("type=\"kotlin.coroutines.Continuation\"", "type=\"int\""));
			Assert.Throws<InvalidOperationException> (() => GetGeneratedTypeOutput (types.Single (t => t.Name == "Service")));
		}

		[Test]
		public void RealKotlinMetadataDistinguishesSuspendResultsFromJvmReturns ()
		{
			using var stream = typeof (KotlinSuspendTests).Assembly.GetManifestResourceStream ("generatortests.kotlin-gradle.AsyncFixture.class")
				?? throw new FileNotFoundException ("Missing compiled Kotlin async fixture.");
			var klass = new ClassFile (stream);
			KotlinFixups.Fixup (new [] { klass });
			var path = new ClassPath { ApiSource = "class-parse" };
			path.Add (klass);
			using var xml = new StringWriter ();
			path.SaveXmlDescription (xml);
			var methods = XDocument.Parse (xml.ToString ()).Descendants ("method").ToDictionary (m => (string) m.Attribute ("name"));
			Assert.AreEqual ("true", methods ["immediate"].Attribute ("kotlin-suspend")?.Value);
			Assert.AreEqual ("kotlin/String", methods ["immediate"].Attribute ("kotlin-suspend-return-type")?.Value);
			Assert.AreEqual ("java.lang.Object", methods ["immediate"].Attribute ("return")?.Value);
			Assert.AreEqual ("kotlin/Int", methods ["number"].Attribute ("kotlin-suspend-return-type")?.Value);
			Assert.AreEqual ("true", methods ["nullableText"].Attribute ("kotlin-suspend-return-nullable")?.Value);
			Assert.AreEqual ("kotlin/Unit", methods ["unit"].Attribute ("kotlin-suspend-return-type")?.Value);
			Assert.IsNull (methods ["finite"].Attribute ("kotlin-suspend"));
			StringAssert.Contains ("\"Classifier\":\"kotlin/Any\"", methods ["echo"].Attribute ("kotlin-return-type")?.Value);
		}
	}
}
