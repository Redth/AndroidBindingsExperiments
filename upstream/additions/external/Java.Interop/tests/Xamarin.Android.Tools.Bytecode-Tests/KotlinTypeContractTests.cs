using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Xamarin.Android.Tools.Bytecode;

namespace Xamarin.Android.Tools.BytecodeTests
{
	[TestFixture]
	public class KotlinTypeContractTests
	{
		[TestCase (1, 1, true)]
		[TestCase (1, 9, true)]
		[TestCase (2, 0, true)]
		[TestCase (2, 4, true)]
		[TestCase (1, 0, false)]
		[TestCase (2, 5, false)]
		[TestCase (3, 0, false)]
		public void VersionPolicyIsExplicit (int major, int minor, bool supported)
		{
			Assert.AreEqual (supported, KotlinMetadata.IsVersionSupported (new Version (major, minor, 0)));
		}

		[Test]
		public void UnknownMetadataCannotProduceSemanticFacts ()
		{
			Assert.IsFalse (KotlinMetadata.IsVersionSupported (null));
			var metadata = new KotlinMetadata { Kind = KotlinMetadataKind.Class, MetadataVersion = new Version (3, 0, 0) };
			Assert.Throws<NotSupportedException> (() => metadata.ParseMetadata ());
			Assert.Throws<NotSupportedException> (() => metadata.AsClassMetadata ());
			metadata.Kind = KotlinMetadataKind.File;
			Assert.Throws<NotSupportedException> (() => metadata.AsFileMetadata ());
			metadata.Kind = KotlinMetadataKind.MultiFileClassPart;
			Assert.Throws<NotSupportedException> (() => metadata.AsFileMetadata ());
		}

		[Test]
		public void NestedArgumentsResolveFunctionTypeTablesAndNullableThresholds ()
		{
			var function = new KotlinFunction {
				TypeTable = new KotlinTypeTable {
					FirstNullable = 1,
					Types = new List<KotlinType> {
						new KotlinType { ClassName = "kotlin/collections/List", Arguments = new List<KotlinTypeArgument> {
							new KotlinTypeArgument { Projection = KotlinProjection.Out, TypeId = 1 },
						} },
						new KotlinType { ClassName = "kotlin/String;" },
					},
				},
			};
			var fact = KotlinTypeContract.Create (null, 0, null, function);
			Assert.IsNotNull (fact);
			Assert.AreEqual ("kotlin/collections/List", fact.Classifier);
			Assert.IsFalse (fact.Nullable);
			Assert.AreEqual ("Out", fact.Arguments [0].Projection);
			var element = fact.Arguments [0].Type;
			Assert.IsNotNull (element);
			Assert.AreEqual ("kotlin/String", element.Classifier);
			Assert.IsTrue (element.Nullable);
		}

		[Test]
		public void StarProjectionsAreNotMistakenForTypeTableZero ()
		{
			var source = new KotlinType { ClassName = "kotlin/collections/List", Arguments = new List<KotlinTypeArgument> {
				new KotlinTypeArgument { Projection = KotlinProjection.Star },
			} };
			var fact = KotlinTypeContract.Create (source, 0, null);
			Assert.IsNotNull (fact);
			Assert.IsNull (fact.Arguments [0].Type);
		}

		[Test]
		public void CyclicTypeTablesFailInsteadOfRecursing ()
		{
			var source = new KotlinType { ClassName = "example/Cycle", Arguments = new List<KotlinTypeArgument> {
				new KotlinTypeArgument { Projection = KotlinProjection.Inv, TypeId = 0 },
			} };
			var file = new KotlinFile { TypeTable = new KotlinTypeTable { Types = new List<KotlinType> { source } } };
			Assert.Throws<InvalidDataException> (() => KotlinTypeContract.Create (null, 0, file));
		}

		[Test]
		public void GenericPrimitiveArraysAreBoxedButPrimitiveArrayTypesAreNot ()
		{
			var generic = new KotlinType { ClassName = "kotlin/Array", Arguments = new List<KotlinTypeArgument> {
				new KotlinTypeArgument { Type = new KotlinType { ClassName = "kotlin/Float" } },
			} };
			Assert.AreEqual ("[Ljava/lang/Float;", generic.GetSignature ());
			Assert.AreEqual ("[F", new KotlinType { ClassName = "kotlin/FloatArray" }.GetSignature ());
			Assert.AreEqual ("Ljava/lang/Integer;", new KotlinType { ClassName = "kotlin/Int", Nullable = true }.GetSignature ());
		}
	}
}
