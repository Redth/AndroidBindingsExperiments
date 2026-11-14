using System.Linq;
using System.Xml.Linq;
using NUnit.Framework;
using Xamarin.Android.Tools.Bytecode;

namespace generatortests
{
	[TestFixture]
	class KotlinFlowProjectionTests : CodeGeneratorTestBase
	{
		const string api = """
			<api><package name="java.lang"><class name="Object" visibility="public" /></package>
			<package name="kotlin.coroutines"><interface name="Continuation" visibility="public" /></package>
			<package name="kotlinx.coroutines.flow"><interface name="Flow" visibility="public">
			  <method name="collect" return="void" visibility="public" abstract="true" />
			</interface></package>
			<package name="example">
			  <class name="Payload" visibility="public" extends="java.lang.Object" />
			  <class name="Service" visibility="public" extends="java.lang.Object">
			    <method name="getNames" return="kotlinx.coroutines.flow.Flow" visibility="public" />
			    <method name="load" return="java.lang.Object" visibility="public" kotlin-suspend="true">
			      <parameter name="continuation" type="kotlin.coroutines.Continuation" />
			    </method>
			  </class>
			</package></api>
			""";

		[Test]
		public void FlowPropertiesGetTypedLazyFacades ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			source.Descendants ("method").Single (m => (string) m.Attribute ("name") == "getNames")
				.SetAttributeValue ("kotlin-return-type", new KotlinTypeContract {
					Classifier = "kotlinx/coroutines/flow/Flow",
					Arguments = [new KotlinTypeArgumentContract { Projection = "Out", Type = new KotlinTypeContract { Classifier = "kotlin/String", Nullable = true } }],
				}.ToJson ());
			var types = ParseApiDefinition (source.ToString ());
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ("IAsyncEnumerable<string?> GetNamesAsAsyncEnumerable", output);
			StringAssert.Contains ("var __flow = Names;", output);
			StringAssert.Contains ("KotlinCoroutines.GetNullableString", output);
		}

		[Test]
		public void SuspendReferenceResultsUseTheResolvedPeerOwner ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			source.Descendants ("method").Single (m => (string) m.Attribute ("name") == "load")
				.SetAttributeValue ("kotlin-return-type", new KotlinTypeContract { Classifier = "example/Payload", Nullable = true }.ToJson ());
			var types = ParseApiDefinition (source.ToString ());
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ("Task<Example.Payload?> LoadAsync", output);
			StringAssert.Contains ("KotlinCoroutines.GetNullablePeer<Example.Payload>", output);
		}

		[Test]
		public void NestedCollectionResultsKeepNullableElementContracts ()
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			source.Descendants ("method").Single (m => (string) m.Attribute ("name") == "load")
				.SetAttributeValue ("kotlin-return-type", new KotlinTypeContract {
					Classifier = "kotlin/collections/List",
					Arguments = [new KotlinTypeArgumentContract { Projection = "Out", Type = new KotlinTypeContract {
						Classifier = "kotlin/collections/List", Nullable = true,
						Arguments = [new KotlinTypeArgumentContract { Projection = "Out", Type = new KotlinTypeContract {
							Classifier = "kotlin/String", Nullable = true,
						} }],
					} }],
				}.ToJson ());
			var types = ParseApiDefinition (source.ToString ());
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));
			StringAssert.Contains ("KotlinList<global::Java.Interop.Kotlin.KotlinList<string?>?>", output);
			StringAssert.Contains ("KotlinCoroutines.GetNullableList", output);
			StringAssert.Contains ("KotlinCoroutines.GetNullableString", output);
		}

		[TestCase ("In")]
		[TestCase ("Star")]
		public void ContravariantAndStarCollectionReadsDoNotAssumeTheArgumentType (string projection)
		{
			options.ExperimentalSemanticModel = options.ExperimentalKotlinAsync = options.SupportNullableReferenceTypes = true;
			var source = XDocument.Parse (api);
			source.Descendants ("method").Single (m => (string) m.Attribute ("name") == "load")
				.SetAttributeValue ("kotlin-return-type", new KotlinTypeContract {
					Classifier = "kotlin/collections/MutableList",
					Arguments = [new KotlinTypeArgumentContract {
						Projection = projection,
						Type = new KotlinTypeContract { Classifier = "kotlin/String" },
					}],
				}.ToJson ());
			var types = ParseApiDefinition (source.ToString ());
			var output = GetGeneratedTypeOutput (types.Single (t => t.Name == "Service"));

			StringAssert.Contains ("KotlinList<global::Java.Lang.Object?>", output);
			StringAssert.DoesNotContain ("KotlinCoroutines.GetString", output);
		}
	}
}
