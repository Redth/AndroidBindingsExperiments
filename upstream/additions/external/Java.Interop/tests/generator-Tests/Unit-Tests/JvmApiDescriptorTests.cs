using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Java.Interop.Tools.Generator.Semantics;
using NUnit.Framework;
using Xamarin.Android.Tools.Bytecode;

namespace generatortests
{
	[TestFixture]
	public class JvmApiDescriptorTests
	{
		const string descriptor = """
			{"schemaVersion":1,"status":"complete","classes":[{
			  "binaryName":"example/Contract","origin":{"classSha256":"fixture"},
			  "jvm":{"visibility":"public","annotations":[],"fields":[],"methods":[
			    {"name":"load","descriptor":"(Lkotlin/coroutines/Continuation;)Ljava/lang/Object;","visibility":"public","annotations":[]}
			  ]},
			  "kotlin":{"status":"supported","version":[2,4,0],"class":{
			    "visibility":"public","constructors":[],"properties":[],
			    "typeParameters":[{"id":0,"name":"T","variance":"INVARIANT","reified":false,"upperBounds":[
			      {"classifier":{"kind":"class","name":"kotlin/CharSequence"},"nullable":false,"definitelyNonNull":false,"suspend":false,"arguments":[]}
			    ]}],
			    "functions":[{
			      "name":"load","visibility":"public","suspend":true,"typeParameters":[],"parameters":[],
			      "signature":{"name":"load","descriptor":"(Lkotlin/coroutines/Continuation;)Ljava/lang/Object;"},
			      "returnType":{"classifier":{"kind":"class","name":"kotlin/collections/List"},"nullable":false,"definitelyNonNull":false,"suspend":false,
			        "arguments":[{"variance":"OUT","type":{"classifier":{"kind":"parameter","id":0},"nullable":true,"definitelyNonNull":false,"suspend":false,"arguments":[]}}]
			      }
			    }]
			  }}
			}]}
			""";

		static XDocument Api () => XDocument.Parse ("""
			<api api-source="class-parse"><package name="example">
			  <class name="Contract" visibility="public" jni-signature="Lexample/Contract;">
			    <method name="load" visibility="public" jni-signature="(Lkotlin/coroutines/Continuation;)Ljava/lang/Object;" return="java.lang.Object">
			      <parameter name="continuation" type="kotlin.coroutines.Continuation" />
			    </method>
			  </class>
			</package></api>
			""");

		string path;

		[SetUp]
		public void CreateFile ()
		{
			path = Path.Combine (TestContext.CurrentContext.WorkDirectory, "descriptor-" + Guid.NewGuid ().ToString ("N") + ".json");
			File.WriteAllText (path, descriptor);
		}

		[TearDown]
		public void Cleanup () => File.Delete (path);

		[Test]
		public void OfficialMetadataSuppliesScopedBoundsAndSuspendResultWithoutChangingJvmSlots ()
		{
			using var input = new JvmApiDescriptor (path);
			var source = Api ();
			var before = source.ToString ();
			var output = input.Apply (source);
			var method = output.Descendants ("method").Single ();
			var contract = JsonSerializer.Deserialize<KotlinTypeContract> ((string) method.Attribute ("kotlin-return-type"));
			Assert.IsNotNull (contract);
			var parameter = contract.Arguments.Single ().Type;
			Assert.IsNotNull (parameter);
			Assert.AreEqual ("T", parameter.TypeParameter);
			Assert.AreEqual (0, parameter.TypeParameterId);
			Assert.AreEqual ("kotlin/CharSequence", parameter.UpperBounds.Single ().Classifier);
			Assert.IsTrue (parameter.Nullable);
			Assert.AreEqual ("Out", contract.Arguments.Single ().Projection);
			Assert.AreEqual ("true", method.Attribute ("kotlin-suspend")?.Value);
			Assert.AreEqual ("(Lkotlin/coroutines/Continuation;)Ljava/lang/Object;", method.Attribute ("jni-signature")?.Value);
			Assert.AreEqual (before, source.ToString ());
		}

		[Test]
		public void StaleOrMismatchedJvmSlotsAreRejected ()
		{
			using var input = new JvmApiDescriptor (path);
			var source = Api ();
			source.Descendants ("method").Single ().SetAttributeValue ("jni-signature", "()Ljava/lang/Object;");
			Assert.Throws<InvalidDataException> (() => input.Apply (source));
		}

		[Test]
		public void SerializationPluginNamesUseJvmNestingRatherThanSplittingTheLastDollar ()
		{
			var root = JsonNode.Parse (descriptor) ?? throw new InvalidDataException ("No descriptor fixture.");
			var classes = root ["classes"]?.AsArray () ?? throw new InvalidDataException ("No classes.");
			var parent = classes [0] ?? throw new InvalidDataException ("No parent.");
			var child = parent.DeepClone ();
			child ["binaryName"] = "example/Contract$$serializer";
			var jvm = child ["jvm"] ?? throw new InvalidDataException ("No JVM facts.");
			jvm ["innerClasses"] = JsonNode.Parse ("""
				[{"binaryName":"example/Contract$$serializer","outerName":"example/Contract","innerName":"$serializer","access":25}]
				""");
			var parameter = child ["kotlin"]? ["class"]? ["typeParameters"]? [0] ??
				throw new InvalidDataException ("No child parameter.");
			parameter ["id"] = 1;
			parameter ["upperBounds"] = new JsonArray ();
			classes.Add (child);
			File.WriteAllText (path, root.ToJsonString ());
			var source = Api ();
			source.Descendants ("class").Single ().SetAttributeValue ("jni-signature", "Lexample/Contract$$serializer;");
			using var input = new JvmApiDescriptor (path);
			var output = input.Apply (source);
			var contract = JsonSerializer.Deserialize<KotlinTypeContract> (
				(string) output.Descendants ("method").Single ().Attribute ("kotlin-return-type"));
			Assert.IsNotNull (contract);
			var argument = contract.Arguments.Single ().Type;
			Assert.IsNotNull (argument);
			Assert.AreEqual (0, argument.TypeParameterId);
			Assert.AreEqual ("kotlin/CharSequence", argument.UpperBounds.Single ().Classifier);
		}

		[Test]
		public void CyclicJvmEnclosingClassesAreRejected ()
		{
			var root = JsonNode.Parse (descriptor) ?? throw new InvalidDataException ("No descriptor fixture.");
			var type = root ["classes"]? [0] ?? throw new InvalidDataException ("No class.");
			var jvm = type ["jvm"] ?? throw new InvalidDataException ("No JVM facts.");
			jvm ["innerClasses"] = JsonNode.Parse ("""
				[{"binaryName":"example/Contract","outerName":"example/Contract","innerName":"Contract","access":1}]
				""");
			File.WriteAllText (path, root.ToJsonString ());
			using var input = new JvmApiDescriptor (path);
			var error = Assert.Throws<InvalidDataException> (() => input.Apply (Api ()));
			StringAssert.Contains ("Cyclic JVM enclosing classes", error?.Message);
		}

		[Test]
		public void LenientMetadataCannotLeaveGuessedLegacySemanticContracts ()
		{
			File.WriteAllText (path, descriptor.Replace ("\"status\":\"supported\"", "\"status\":\"lenient\""));
			using var input = new JvmApiDescriptor (path);
			var source = Api ();
			source.Descendants ("class").Single ().SetAttributeValue ("visibility", "private");
			source.Descendants ("method").Single ().SetAttributeValue ("kotlin-return-type", "legacy-guess");
			source.Descendants ("method").Single ().SetAttributeValue ("visibility", "private");
			var output = input.Apply (source);
			Assert.IsNull (output.Descendants ("method").Single ().Attribute ("kotlin-return-type"));
			Assert.AreEqual ("unsupported", output.Descendants ("class").Single ().Attribute ("kotlin-metadata-status")?.Value);
			Assert.AreEqual ("public", output.Descendants ("class").Single ().Attribute ("visibility")?.Value);
			Assert.AreEqual ("public", output.Descendants ("method").Single ().Attribute ("visibility")?.Value);
		}
	}
}
