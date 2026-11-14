using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Java.Interop.Tools.Generator.Semantics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MonoDroid.Generation;
using NUnit.Framework;

namespace generatortests
{
	[TestFixture]
	class GenericProjectionConstraintTests : CodeGeneratorTestBase
	{
		const string api = """
			<api api-source="class-parse"><package name="java.lang">
			  <class name="Object" visibility="public" jni-signature="Ljava/lang/Object;" />
			  <class name="String" visibility="public" extends="java.lang.Object" jni-signature="Ljava/lang/String;" />
			  <interface name="CharSequence" visibility="public" jni-signature="Ljava/lang/CharSequence;">
			    <method name="length" visibility="public" abstract="true" return="int" jni-signature="()I" />
			  </interface>
			</package><package name="example">
			  <interface name="Record" visibility="public" jni-signature="Lexample/Record;">
			    <method name="getName" visibility="public" abstract="true" return="java.lang.String" jni-signature="()Ljava/lang/String;" />
			  </interface>
			  <class name="Steps" visibility="public" extends="java.lang.Object" jni-signature="Lexample/Steps;">
			    <implements name="example.Record" name-generic-aware="example.Record" />
			    <method name="getName" visibility="public" return="java.lang.String" jni-signature="()Ljava/lang/String;" />
			    <method name="echo" visibility="public" return="V" jni-signature="(Ljava/lang/Object;)Ljava/lang/Object;">
			      <typeParameters><typeParameter name="V" /></typeParameters>
			      <parameter name="value" type="V" />
			    </method>
			  </class>
			  <class name="Response" visibility="public" extends="java.lang.Object" jni-signature="Lexample/Response;">
			    <typeParameters><typeParameter name="T" classBound="example.Record" /></typeParameters>
			    <constructor name="Response" visibility="public" jni-signature="(Lexample/Record;)V"><parameter name="value" type="T" /></constructor>
			    <method name="get" visibility="public" return="T" jni-signature="()Lexample/Record;" />
			  </class>
			  <class name="Service" visibility="public" extends="java.lang.Object" jni-signature="Lexample/Service;">
			    <method name="read" visibility="public" return="example.Response&lt;R&gt;" jni-signature="(Lexample/Record;)Lexample/Response;">
			      <typeParameters><typeParameter name="R" classBound="example.Record" /></typeParameters>
			      <parameter name="record" type="R" />
			    </method>
			  </class>
			  <class name="Related" visibility="public" extends="java.lang.Object" jni-signature="Lexample/Related;">
			    <typeParameters><typeParameter name="U" classBound="example.Record" /><typeParameter name="T" classBound="U" /></typeParameters>
			    <method name="get" visibility="public" return="example.Response&lt;T&gt;" jni-signature="()Lexample/Response;" />
			  </class>
			</package></api>
			""";

		GenericFacadeProjection Create (out List<GenBase> types, string source = api)
		{
			options.ExperimentalSemanticModel = options.ExperimentalGenericFacades = true;
			options.SupportNullableReferenceTypes = options.SupportDefaultInterfaceMethods = options.SupportInterfaceConstants = true;
			types = ParseApiDefinition (source);
			return new GenericFacadeProjection (XDocument.Parse (source), types, options);
		}

		[Test]
		public void NativeRecordBoundsAcceptCanonicalPeersAndViewsButRejectUnrelatedArguments ()
		{
			var projection = Create (out var types);
			var response = projection.GenerateType ("example.Response");
			StringAssert.Contains ("where @T : global::Java.Interop.Kotlin.IJavaProjection<Example.IRecord>", response);
			StringAssert.Contains ("RequireAssignableTo (\"example/Record\")", response);
			var sources = new [] { response, projection.GenerateType ("example.Steps") };
			Assert.IsEmpty (Compile (sources, """
				_ = typeof(Example.ResponseView<Example.Steps>);
				_ = typeof(Example.ResponseView<Example.StepsView>);
				_ = typeof(Example.ResponseView<Example.IRecord>);
				"""));
			var errors = Compile (sources, "_ = typeof(Example.ResponseView<string>);");
			Assert.IsTrue (errors.Any (e => e.Id == "CS0311"), string.Join ("\n", errors));
			var peer = types.Single (t => t.JavaName == "example.Steps");
			StringAssert.Contains ("IJavaProjection<Example.Steps>", GetGeneratedTypeOutput (peer));
		}

		[Test]
		public void MappedViewsKeepCustomConversionsAndNativeBoundsWithoutASecondPeer ()
		{
			var projection = Create (out _);
			var mapped = projection.GenerateType ("example.Response", mapped: true);
			StringAssert.Contains ("class ResponseMappedView<", mapped);
			StringAssert.DoesNotContain ("where @T", mapped);
			StringAssert.Contains ("RequireAssignableTo (\"example/Record\")", mapped);
			StringAssert.Contains ("Custom managed representations are allowed", mapped);
			StringAssert.DoesNotContain ("Runtime.Register", mapped);
			Assert.IsEmpty (Compile (new [] { mapped }, "_ = typeof(Example.ResponseMappedView<string>);"));
		}

		[Test]
		public void MethodBoundsAndNestedResultsUseTheSameProjectionContract ()
		{
			var projection = Create (out _);
			var response = projection.GenerateType ("example.Response");
			var service = projection.GenerateType ("example.Service");
			var mappedResponse = projection.GenerateType ("example.Response", mapped: true);
			var mappedService = projection.GenerateType ("example.Service", mapped: true);
			StringAssert.Contains ("where @R : global::Java.Interop.Kotlin.IJavaProjection<Example.IRecord>", service);
			StringAssert.Contains ("Example.ResponseView<@R>", service);
			StringAssert.Contains ("Example.ResponseMappedView<@R>", mappedService);
			Assert.IsEmpty (Compile (new [] { response, service, mappedResponse, mappedService },
				"_ = new Example.ServiceMappedView().Read<string>(\"custom\");"));
			Assert.IsTrue (Compile (new [] { response, service }, "_ = new Example.ServiceView().Read<string>(\"wrong\");")
				.Any (e => e.Id == "CS0311"));
		}

		[Test]
		public void DependentTypeVariablesChooseMappedNestedViewsInsteadOfInvalidClrConstraints ()
		{
			var projection = Create (out _);
			var related = projection.GenerateType ("example.Related");
			StringAssert.Contains ("where @U : global::Java.Interop.Kotlin.IJavaProjection<Example.IRecord>", related);
			StringAssert.DoesNotContain ("where @T", related);
			StringAssert.Contains ("Example.ResponseMappedView<@T>", related);
			StringAssert.Contains ("RequireAssignableTo (this.__type_U.NativeName)", related);
			Assert.IsTrue (projection.Decisions.Any (d => d.Reason == "runtime-only-type-variable-bound"));
			Assert.IsEmpty (Compile (new [] { related, projection.GenerateType ("example.Response", mapped: true) },
				"_ = typeof(Example.RelatedView<Example.Steps, Example.Steps>);"));
		}

		[Test]
		public void ConvertedAndRecursiveBoundsRemainExplicitRuntimeChecks ()
		{
			var source = api.Replace ("classBound=\"example.Record\"", "classBound=\"java.lang.CharSequence\" interfaceBounds=\"java.lang.Comparable&lt;T&gt;\"");
			var projection = Create (out _, source);
			var response = projection.GenerateType ("example.Response");
			StringAssert.DoesNotContain ("where @T", response);
			StringAssert.Contains ("java.lang.Comparable&lt;T&gt;", response);
			StringAssert.Contains ("RequireAssignableTo (\"java/lang/CharSequence\")", response);
			StringAssert.Contains ("RequireAssignableTo (\"java/lang/Comparable\")", response);
			Assert.IsTrue (projection.Decisions.Any (d => d.Reason == "runtime-only-parameterized-bound"));
			Assert.IsTrue (projection.Decisions.Any (d => d.Reason == "runtime-only-converted-bound"));
		}

		[Test]
		public void RawInterfacesHaveManagedOnlyDefaultProjectionImplementations ()
		{
			Create (out var types);
			var output = GetGeneratedTypeOutput (types.Single (t => t.JavaName == "example.Record"));
			StringAssert.Contains ("IJavaProjection<Example.IRecord>", output);
			StringAssert.Contains ("IJavaProjection<Example.IRecord>.Peer", output);
			StringAssert.Contains ("get { return this; }", output);
		}

		[Test]
		public void ImportedCanonicalPeersAdvertiseTheirProjectionCapability ()
		{
			using var assembly = Mono.Cecil.AssemblyDefinition.CreateAssembly (
				new Mono.Cecil.AssemblyNameDefinition ("Canonical", new Version (1, 0)), "Canonical.dll", Mono.Cecil.ModuleKind.Dll);
			var module = assembly.MainModule;
			var type = new Mono.Cecil.TypeDefinition ("Example", "IRecord",
				Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Interface | Mono.Cecil.TypeAttributes.Abstract);
			module.Types.Add (type);
			var contract = new Mono.Cecil.GenericInstanceType (new Mono.Cecil.TypeReference ("Java.Interop.Kotlin", "IJavaProjection`1", module,
				new Mono.Cecil.AssemblyNameReference ("Microsoft.Android.Kotlin", new Version (1, 0))));
			contract.GenericArguments.Add (type);
			type.Interfaces.Add (new Mono.Cecil.InterfaceImplementation (contract));
			Assert.IsTrue (CecilApiImporter.CreateGenBaseSupport (type, options).HasJavaProjection);
		}

		[TestCase (true)]
		[TestCase (false)]
		public void ReferencedBoundsRequireAnActualImportedProjectionContract (bool projectionAvailable)
		{
			var source = XDocument.Parse (api);
			source.Descendants ("interface").Single (e => (string) e.Attribute ("name") == "Record").Remove ();
			var imported = new InterfaceGen (new GenBaseSupport {
				Name = "IRecord", FullName = "Example.IRecord", Namespace = "Example",
				PackageName = "example", JavaSimpleName = "Record", IsAcw = true, Visibility = "public",
				IsGeneratable = false, ReferenceAssemblyName = "Canonical",
				HasJavaProjection = projectionAvailable,
			});
			options.SymbolTable.AddType (imported);
			var projection = Create (out _, source.ToString ());
			var response = projection.GenerateType ("example.Response");
			Assert.AreEqual (projectionAvailable, response.Contains ("where @T :"));
			StringAssert.Contains ("RequireAssignableTo (\"example/Record\")", response);
			if (!projectionAvailable)
				Assert.IsTrue (projection.Decisions.Any (d => d.Reason == "runtime-only-unavailable-peer-projection"));
		}

		[Test]
		public void DisabledGenericFacadesDoNotChangeLegacyPeerContracts ()
		{
			Create (out var types);
			options.ExperimentalGenericFacades = false;
			var output = GetGeneratedTypeOutput (types.Single (t => t.JavaName == "example.Steps"));
			StringAssert.DoesNotContain ("IJavaProjection", output);
		}

		static Diagnostic[] Compile (string[] generated, string consumer)
		{
			var trusted = AppContext.GetData ("TRUSTED_PLATFORM_ASSEMBLIES") as string ??
				throw new InvalidOperationException ("No platform compiler references.");
			var references = trusted.Split (Path.PathSeparator)
				.Where (p => Path.GetDirectoryName (p) == Path.GetDirectoryName (typeof (object).Assembly.Location))
				.Select (p => MetadataReference.CreateFromFile (p))
				.Append (MetadataReference.CreateFromFile (typeof (Java.Interop.Kotlin.IJavaProjection<>).Assembly.Location));
			var source = """
				using Java.Interop.Kotlin;
				namespace Java.Interop.Kotlin { public class JvmType<T> {} }
				namespace Example {
				  public interface IRecord : IJavaProjection<IRecord> { IRecord IJavaProjection<IRecord>.Peer => this; }
				  public class Steps : IRecord, IJavaProjection<Steps> { Steps IJavaProjection<Steps>.Peer => this; }
				  public class Response {}
				  public class Service {}
				  public class Related {}
				}
				class Consumer { public void Run() {
				""" + consumer + "} }";
			var signatures = generated.Select (s => new SignatureOnly ().Visit (CSharpSyntaxTree.ParseText (s).GetRoot ())?.NormalizeWhitespace ().ToFullString () ??
				throw new InvalidOperationException ("No generated signatures."));
			return CSharpCompilation.Create ("Constraints", signatures.Append (source).Select (s => CSharpSyntaxTree.ParseText (s)), references,
				new CSharpCompilationOptions (OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true))
				.GetDiagnostics ().Where (d => d.Severity == DiagnosticSeverity.Error).ToArray ();
		}

		sealed class SignatureOnly : CSharpSyntaxRewriter
		{
			public override SyntaxNode VisitClassDeclaration (ClassDeclarationSyntax node)
			{
				var members = node.Members.Select (m => m switch {
					MethodDeclarationSyntax method => (MemberDeclarationSyntax) method.WithBody (
						SyntaxFactory.Block (SyntaxFactory.ParseStatement ("throw new System.NotSupportedException();"))),
					PropertyDeclarationSyntax property when property.Identifier.ValueText == "Peer" => property.WithAccessorList (null)
						.WithExpressionBody (SyntaxFactory.ArrowExpressionClause (
							SyntaxFactory.ThrowExpression (SyntaxFactory.ObjectCreationExpression (
								SyntaxFactory.ParseTypeName ("System.NotSupportedException")).WithArgumentList (SyntaxFactory.ArgumentList ()))))
						.WithSemicolonToken (SyntaxFactory.Token (SyntaxKind.SemicolonToken)),
					_ => null,
				}).Where (m => m != null);
				return node.WithMembers (SyntaxFactory.List (members));
			}
		}
	}
}
