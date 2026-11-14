#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Microsoft.Android.BindingGenerator.BuildTasks;
using Microsoft.Android.Build.BaseTasks.Tests.Utilities;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NUnit.Framework;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Android.BindingGenerator.Tests;

[TestFixture]
public class BindingToolTests
{
	string directory = "";
	static string Host => Environment.GetEnvironmentVariable ("DOTNET_HOST_PATH") ?? "dotnet";

	[SetUp]
	public void SetUp ()
	{
		directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "binding-tool-" + Guid.NewGuid ().ToString ("N"));
		Directory.CreateDirectory (directory);
	}

	[TearDown]
	public void TearDown () => Directory.Delete (directory, true);

	[TestCase (typeof (RunBindingTool))]
	[TestCase (typeof (ReadBindingOutputs))]
	[TestCase (typeof (CheckBindingGenerator))]
	[TestCase (typeof (WriteBindingKeepRules))]
	public void ConcreteTasksSupportMultithreading (Type type)
	{
		Assert.IsNotNull (type.GetCustomAttribute<MSBuildMultiThreadableTaskAttribute> (false));
		Assert.IsTrue (typeof (IMultiThreadableTask).IsAssignableFrom (type));
	}

	[TestCase ("Semantic", "Android", "v11.0", "37.2.0-rc.2.84", true, true, true)]
	[TestCase ("Semantic", "Android", "v11.0", "37.99.0", true, true, true)]
	[TestCase ("semantic", "android", "v11.0", "37.2.0", true, true, true)]
	[TestCase ("Semantic", "", "", "", false, false, true)]
	[TestCase ("Invalid", "Android", "v11.0", "37.2.0", true, true, false)]
	[TestCase ("Semantic", "Android", "v11.0", "37.2.0", false, true, false)]
	[TestCase ("Semantic", "Android", "v11.0", "37.2.0", true, false, false)]
	[TestCase ("Semantic", "Android", "v10.0", "36.1.0", true, true, false)]
	[TestCase ("Semantic", "Android", "v12.0", "38.0.0", true, true, false)]
	[TestCase ("Semantic", "Android", "", "", true, true, false)]
	public void ValidatesSelection (string selection, string platform, string framework, string sdk, bool binding, bool imported, bool expected)
	{
		var errors = new List<BuildErrorEventArgs> ();
		var task = new CheckBindingGenerator {
			BuildEngine = new MockBuildEngine (TextWriter.Null, errors: errors),
			Selection = selection, Platform = platform, FrameworkVersion = framework,
			SdkVersion = sdk, BindingProject = binding, TailImported = imported,
		};
		Assert.AreEqual (expected, task.Execute ());
		if (!expected)
			Assert.That (errors.Select (e => e.Code), Is.All.EqualTo ("XA4350"));
	}

	[Test]
	public void ReadsActualOutputPathsIncludingEnums ()
	{
		string path = Path.Combine (directory, "sources.projitems");
		new XDocument (new XElement ("Project", new XElement ("ItemGroup",
			new XElement ("Compile", new XAttribute ("Include", "$(MSBuildThisFileDirectory)Thing.cs")),
			new XElement ("Compile", new XAttribute ("Include", "$(MSBuildThisFileDirectory)..\\enums\\Mode.cs"))))).Save (path);
		var task = new ReadBindingOutputs {
			BuildEngine = new MockBuildEngine (TextWriter.Null),
			TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment (directory, new Dictionary<string, string> ()),
			Manifest = "sources.projitems",
		};
		Assert.IsTrue (task.Execute ());
		Assert.AreEqual (Path.Combine (directory, "Thing.cs"), task.Files [0].ItemSpec);
		Assert.AreEqual (Path.GetFullPath (Path.Combine (directory, "..", "enums", "Mode.cs")), task.Files [1].ItemSpec);
	}

	[Test]
	public void MissingOutputCannotLeaveStaleSuccess ()
	{
		string path = Path.Combine (directory, "missing-output.txt");
		File.WriteAllText (path, "old success");
		var errors = new List<BuildErrorEventArgs> ();
		var task = CreateTool (directory, errors, "--version");
		task.RequiredOutputs = [new TaskItem ("missing-output.txt")];
		Assert.IsFalse (task.Execute ());
		Assert.IsFalse (File.Exists (path));
		Assert.That (errors.Any (e => e.Message?.Contains ("required output", StringComparison.Ordinal) == true));
	}

	[Test]
	public void NonzeroExitIsAnError ()
	{
		var errors = new List<BuildErrorEventArgs> ();
		var task = CreateTool (directory, errors, "--not-a-dotnet-option");
		Assert.IsFalse (task.Execute ());
		Assert.That (errors.Any (e => e.Code == "XASBG0000"));
	}

	[TestCase ("warning", "BG8A00", true)]
	[TestCase ("error", "BG4200", false)]
	public void PreservesCanonicalGeneratorDiagnostics (string severity, string code, bool expected)
	{
		File.Copy (Path.Combine (TestContext.CurrentContext.TestDirectory, "ProcessProbe.proj"), Path.Combine (directory, "ProcessProbe.proj"));
		var errors = new List<BuildErrorEventArgs> ();
		var warnings = new List<BuildWarningEventArgs> ();
		var task = CreateTool (directory, errors, "msbuild", "ProcessProbe.proj", "-t:Diagnostic", "-nologo",
			"-nr:false", "-v:minimal", $"-p:ProbeDiagnostic={severity} {code}: binding diagnostic");
		task.BuildEngine = new MockBuildEngine (TextWriter.Null, errors: errors, warnings: warnings);
		Assert.AreEqual (expected, task.Execute ());
		if (severity == "warning")
			Assert.That (warnings.Any (w => w.Code == code));
		else
			Assert.That (errors.Any (e => e.Code == code));
	}

	[Test]
	public void CancellationDoesNotStartTool ()
	{
		var errors = new List<BuildErrorEventArgs> ();
		var task = CreateTool (directory, errors, "--version");
		task.Cancel ();
		Assert.IsFalse (task.Execute ());
		Assert.That (errors.Any (e => e.Code == "XASBG7013"));
	}

	[Test]
	public void InvalidTransformIsReportedBeforeToolStarts ()
	{
		string path = Path.Combine (directory, "transform.xml");
		new XDocument (new XElement ("not-metadata")).Save (path);
		var errors = new List<BuildErrorEventArgs> ();
		var task = CreateTool (directory, errors, "--version");
		task.TransformFiles = [new TaskItem ("transform.xml")];
		Assert.IsFalse (task.Execute ());
		Assert.That (errors.Any (e => e.Message?.Contains ("Unsupported binding transform", StringComparison.Ordinal) == true));
	}

	[Test]
	public void RetentionRulesAreScopedToLocalPeerOwners ()
	{
		string report = Path.Combine (directory, "ownership.json");
		File.WriteAllText (report, """
			{"SchemaVersion":1,"Types":[{"JavaType":"net/example/Box"},{"JavaType":"net/example/Box$Listener"},{"JavaType":"net/example/Box"}]}
			""");
		var task = new WriteBindingKeepRules {
			BuildEngine = new MockBuildEngine (TextWriter.Null),
			OwnershipReport = report, OutputFile = Path.Combine (directory, "binding.pro"),
		};
		Assert.IsTrue (task.Execute ());
		string output = File.ReadAllText (task.OutputFile);
		Assert.AreEqual (2, output.Split ("-keep class ").Length - 1);
		StringAssert.Contains ("-keep class net.example.Box {", output);
		StringAssert.Contains ("-keep class net.example.Box$Listener {", output);
		StringAssert.DoesNotContain ("**", output);
		StringAssert.Contains ("public *;", output);
		StringAssert.Contains ("protected *;", output);
	}

	[Test]
	public void InvalidPeerNameCannotInjectRetentionRules ()
	{
		string report = Path.Combine (directory, "ownership.json");
		File.WriteAllText (report, """
			{"Types":[{"JavaType":"net/example/Box { *; }"}]}
			""");
		var errors = new List<BuildErrorEventArgs> ();
		var task = new WriteBindingKeepRules {
			BuildEngine = new MockBuildEngine (TextWriter.Null, errors: errors),
			OwnershipReport = report, OutputFile = Path.Combine (directory, "binding.pro"),
		};
		File.WriteAllText (task.OutputFile, "stale rules");
		Assert.IsFalse (task.Execute ());
		Assert.IsFalse (File.Exists (task.OutputFile));
		Assert.That (errors.Any (e => e.Message?.Contains ("retention rules", StringComparison.Ordinal) == true));
	}

	[Test]
	public async Task ParallelToolsUseEngineDirectoryAndEnvironment ()
	{
		string cwd = Directory.GetCurrentDirectory ();
		string? originalEnvironment = Environment.GetEnvironmentVariable ("BINDING_TEST_ENV");
		var work = Enumerable.Range (0, 2).Select (i => {
			string child = Path.Combine (directory, "project with spaces " + i);
			Directory.CreateDirectory (child);
			File.Copy (Path.Combine (TestContext.CurrentContext.TestDirectory, "ProcessProbe.proj"), Path.Combine (child, "ProcessProbe.proj"));
			var errors = new List<BuildErrorEventArgs> ();
			var task = CreateTool (child, errors, "msbuild", "ProcessProbe.proj", "-t:Probe", "-nologo", "-v:quiet", "-nr:false");
			var environment = TaskEnvironment.Fallback.GetEnvironmentVariables ().ToDictionary (p => p.Key, p => p.Value);
			environment ["BINDING_TEST_ENV"] = "marker-" + i;
			task.TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment (child, environment);
			task.RequiredOutputs = [new TaskItem ("probe-output.txt")];
			return (Task: task, Directory: child, Marker: "marker-" + i, Errors: errors);
		}).ToArray ();
		var result = await Task.WhenAll (work.Select (w => Task.Run (() => w.Task.Execute ())));
		Assert.That (result, Is.All.True, string.Join ("\n", work.SelectMany (w => w.Errors).Select (e => e.Message)));
		foreach (var item in work)
			Assert.AreEqual (item.Directory + "|" + item.Marker, File.ReadAllText (Path.Combine (item.Directory, "probe-output.txt")).Trim ());
		Assert.AreEqual (cwd, Directory.GetCurrentDirectory ());
		Assert.AreEqual (originalEnvironment, Environment.GetEnvironmentVariable ("BINDING_TEST_ENV"));
	}

	static RunBindingTool CreateTool (string projectDirectory, List<BuildErrorEventArgs> errors, params string [] arguments) => new () {
		BuildEngine = new MockBuildEngine (TextWriter.Null, errors: errors),
		TaskEnvironment = TaskEnvironment.CreateWithProjectDirectoryAndEnvironment (projectDirectory,
			TaskEnvironment.Fallback.GetEnvironmentVariables ().ToDictionary (p => p.Key, p => p.Value)),
		Executable = Host,
		Arguments = arguments.Select (a => (ITaskItem) new TaskItem (a)).ToArray (),
	};
}
