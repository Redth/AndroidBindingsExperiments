using System;
using System.IO;
using System.Reflection;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using NUnit.Framework;
using Xamarin.Android.Tasks;
using Xamarin.ProjectTools;

namespace Xamarin.Android.Build.Tests
{
	[TestFixture]
	public class ExperimentalBindingsTests
	{
		string directory;

		[SetUp]
		public void SetUp ()
		{
			directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "binding-options-" + Guid.NewGuid ().ToString ("N"));
			Directory.CreateDirectory (directory);
		}

		[TearDown]
		public void TearDown () => Directory.Delete (directory, recursive: true);

		[TestCase (false)]
		[TestCase (true)]
		public void GeneratorOptionsAreExplicitAndOptIn (bool enabled)
		{
			var generator = new BindingsGenerator {
				BuildEngine = new MockBuildEngine (TestContext.Out),
				OutputDirectory = directory, ApiXmlInput = Path.Combine (directory, "api.xml"),
				NoStdlib = true, ExperimentalSemanticModel = enabled, ExperimentalKotlinAsync = enabled,
				ExperimentalGenericFacades = enabled,
				ExperimentalApiDescriptor = enabled ? Path.Combine (directory, "descriptor.json") : null,
				ExperimentalOwnershipReport = enabled ? Path.Combine (directory, "ownership.json") : null,
				ExperimentalNativeManifest = enabled ? Path.Combine (directory, "native.json") : null,
			};
			CommandLine (generator);
			var response = File.ReadAllText (Path.Combine (directory, "generator.rsp"));
			Assert.AreEqual (enabled, response.Contains ("--experimental-semantic-model"));
			Assert.AreEqual (enabled, response.Contains ("--experimental-kotlin-async"));
			Assert.AreEqual (enabled, response.Contains ("--experimental-generic-facades"));
			Assert.AreEqual (enabled, response.Contains ("--experimental-api-descriptor="));
			Assert.AreEqual (enabled, response.Contains ("--experimental-ownership-report="));
			Assert.AreEqual (enabled, response.Contains ("--experimental-native-manifest="));
		}

		[TestCase ("")]
		[TestCase ("library")]
		public void GradleOutputIsolationIdentifiesTheSelectedModule (string module)
		{
			var script = Path.Combine (directory, "init.gradle.kts");
			File.WriteAllText (script, "");
			var gradle = new Gradle {
				ModuleName = module, Command = "assembleRelease",
				OutputPath = directory, BuildDirInitScriptPath = script,
			};
			StringAssert.Contains ("netAndroidModuleName=" + module, CommandLine (gradle));
		}

		[TestCase (false)]
		[TestCase (true)]
		public void PublishDiscoveryDoesNotPoisonOuterCompileCache (bool discovery)
		{
			var targets = Path.Combine (XABuildPaths.TopDirectory, "src", "Xamarin.Android.Build.Tasks",
				"Microsoft.Android.Sdk", "targets", "Microsoft.Android.Sdk.AssemblyResolution.targets");
			using var reader = XmlReader.Create (new StringReader ($"""
				<Project>
				  <PropertyGroup>
				    <_ComputeFilesToPublishForRuntimeIdentifiers>{discovery.ToString ().ToLowerInvariant ()}</_ComputeFilesToPublishForRuntimeIdentifiers>
				    <_XamarinAndroidBuildTasksAssembly>{typeof (BindingsGenerator).Assembly.Location}</_XamarinAndroidBuildTasksAssembly>
				    <CompileDependsOn>BeforeCompile;_GenerateCompileDependencyCache;CoreCompile;</CompileDependsOn>
				  </PropertyGroup>
				  <Import Project="{targets}" />
				</Project>
				"""));
			using var projects = new ProjectCollection ();
			var project = new Project (ProjectRootElement.Create (reader), null, null, projects);
			Assert.AreEqual (!discovery, project.GetPropertyValue ("CompileDependsOn").Contains ("_GenerateCompileDependencyCache"));
		}

		static string CommandLine (object task)
		{
			var method = task.GetType ().GetMethod ("GenerateCommandLineCommands", BindingFlags.Instance | BindingFlags.NonPublic)
				?? throw new InvalidOperationException ("The tool task does not expose a command line builder.");
			return method.Invoke (task, null) as string ?? throw new InvalidOperationException ("No command line was generated.");
		}
	}
}
