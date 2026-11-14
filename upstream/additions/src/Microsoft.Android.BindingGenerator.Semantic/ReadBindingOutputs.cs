#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.Android.Build.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Properties = Microsoft.Android.BindingGenerator.Semantic.Properties;

namespace Microsoft.Android.BindingGenerator.BuildTasks;

[MSBuildMultiThreadableTask]
public sealed class ReadBindingOutputs : MultithreadedAndroidTask
{
	public override string TaskPrefix => "SBG";

	[Required]
	public string Manifest { get; set; } = "";

	[Output]
	public ITaskItem [] Files { get; set; } = [];

	public override bool RunTask ()
	{
		string path = TaskEnvironment.GetAbsolutePath (Manifest);
		string directory = Path.GetDirectoryName (path) ?? throw new InvalidOperationException (
			string.Format (CultureInfo.CurrentCulture, Properties.Resources.MissingManifestDirectory, path));
		Files = XDocument.Load (path).Descendants ()
			.Where (e => e.Name.LocalName == "Compile")
			.Select (e => e.Attribute ("Include")?.Value ?? throw new InvalidOperationException (
				string.Format (CultureInfo.CurrentCulture, Properties.Resources.MissingSourceInclude, path)))
			.Select (f => (ITaskItem) new TaskItem (Path.GetFullPath (
				f.Replace ("$(MSBuildThisFileDirectory)", directory + Path.DirectorySeparatorChar)
					.Replace ('\\', Path.DirectorySeparatorChar), directory))).ToArray ();
		return true;
	}
}
