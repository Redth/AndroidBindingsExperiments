#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Android.Build.Tasks;
using Microsoft.Build.Framework;
using Xamarin.Android.Tools;
using Properties = Microsoft.Android.BindingGenerator.Semantic.Properties;

namespace Microsoft.Android.BindingGenerator.BuildTasks;

[MSBuildMultiThreadableTask]
public sealed class RunBindingTool : MultithreadedAndroidTask, ICancelableTask
{
	readonly CancellationTokenSource cancellation = new ();

	public override string TaskPrefix => "SBG";

	[Required]
	public string Executable { get; set; } = "";

	[Required]
	public ITaskItem [] Arguments { get; set; } = [];

	public ITaskItem []? TransformFiles { get; set; }
	public ITaskItem []? NamespaceTransforms { get; set; }
	public string? NamespaceTransformFile { get; set; }
	public ITaskItem []? RequiredOutputs { get; set; }

	public void Cancel () => cancellation.Cancel ();

	public override bool RunTask ()
	{
		bool success = false;
		try {
			success = RunTool ();
			return success;
		} finally {
			if (!success) {
				foreach (var output in RequiredOutputs ?? [])
					File.Delete (TaskEnvironment.GetAbsolutePath (output.ItemSpec));
			}
		}
	}

	bool RunTool ()
	{
		var arguments = Arguments.Select (a => a.ItemSpec).ToList ();
		AddTransforms (arguments);
		foreach (var output in RequiredOutputs ?? []) {
			string path = TaskEnvironment.GetAbsolutePath (output.ItemSpec);
			Directory.CreateDirectory (Path.GetDirectoryName (path) ?? TaskEnvironment.ProjectDirectory);
			File.Delete (path);
		}

		string executable = Executable;
		if (Path.IsPathRooted (executable) || executable.Contains (Path.DirectorySeparatorChar) || executable.Contains (Path.AltDirectorySeparatorChar))
			executable = TaskEnvironment.GetAbsolutePath (executable);
		var startInfo = TaskEnvironment.GetProcessStartInfo ();
		startInfo.FileName = executable;
		startInfo.UseShellExecute = false;
		startInfo.CreateNoWindow = true;
		foreach (var argument in arguments)
			startInfo.ArgumentList.Add (argument);

		Log.LogMessage (MessageImportance.Normal, "Semantic binding tool: {0} {1}", executable, arguments.FirstOrDefault ());
		Log.LogMessage (MessageImportance.Low, "Arguments: {0}", string.Join (" ", arguments));
		using var stdout = new StringWriter ();
		using var stderr = new StringWriter ();
		var engine = BuildEngine as IBuildEngine3;
		int exitCode;
		engine?.Yield ();
		try {
			exitCode = ProcessUtils.StartProcess (startInfo, stdout, stderr, cancellation.Token).GetAwaiter ().GetResult ();
		} finally {
			engine?.Reacquire ();
		}
		using (var reader = new StringReader (stdout.ToString ())) {
			while (reader.ReadLine () is string line)
				Log.LogMessageFromText (line, MessageImportance.Low);
		}
		using (var reader = new StringReader (stderr.ToString ())) {
			while (reader.ReadLine () is string line)
				Log.LogMessageFromText (line, MessageImportance.Low);
		}
		if (exitCode != 0) {
			if (!Log.HasLoggedErrors)
				Log.LogUnhandledToolError (TaskPrefix, string.Format (CultureInfo.CurrentCulture,
					Properties.Resources.ToolFailure, executable, exitCode, stderr));
			return false;
		}
		foreach (var output in RequiredOutputs ?? []) {
			string path = TaskEnvironment.GetAbsolutePath (output.ItemSpec);
			if (!File.Exists (path))
				throw new InvalidOperationException (string.Format (CultureInfo.CurrentCulture, Properties.Resources.MissingToolOutput, path));
		}
		return !Log.HasLoggedErrors;
	}

	void AddTransforms (List<string> arguments)
	{
		foreach (var transform in TransformFiles ?? []) {
			string path = TaskEnvironment.GetAbsolutePath (transform.ItemSpec);
			var root = XDocument.Load (path).Root ?? throw new InvalidOperationException (string.Format (CultureInfo.CurrentCulture, Properties.Resources.EmptyTransform, path));
			string option = root.Name.LocalName switch {
				"metadata" => "fixup",
				"enum-field-mappings" => "enumfields",
				"enum-method-mappings" => "enummethods",
				_ => throw new InvalidOperationException (string.Format (CultureInfo.CurrentCulture, Properties.Resources.UnsupportedTransform, root.Name, path)),
			};
			arguments.Add ($"--{option}={path}");
		}
		if (NamespaceTransforms is not { Length: > 0 })
			return;
		if (string.IsNullOrEmpty (NamespaceTransformFile))
			throw new InvalidOperationException (Properties.Resources.MissingNamespaceTransformFile);
		var metadata = new XElement ("metadata");
		foreach (var replacement in NamespaceTransforms) {
			if (!replacement.MetadataNames.Cast<string> ().Contains ("Replacement", StringComparer.OrdinalIgnoreCase))
				throw new InvalidOperationException (string.Format (CultureInfo.CurrentCulture, Properties.Resources.MissingNamespaceReplacement, replacement.ItemSpec));
			metadata.Add (new XElement ("ns-replace",
				new XAttribute ("source", replacement.ItemSpec),
				new XAttribute ("replacement", replacement.GetMetadata ("Replacement"))));
		}
		string file = TaskEnvironment.GetAbsolutePath (NamespaceTransformFile);
		Directory.CreateDirectory (Path.GetDirectoryName (file) ?? TaskEnvironment.ProjectDirectory);
		using (var writer = new StreamWriter (file, false, new UTF8Encoding (false)))
			metadata.Save (writer);
		arguments.Add ($"--fixup={file}");
	}
}
