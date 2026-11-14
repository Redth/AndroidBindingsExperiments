#nullable enable
using System;
using Microsoft.Android.Build.Tasks;
using Microsoft.Build.Framework;
using Properties = Microsoft.Android.BindingGenerator.Semantic.Properties;

namespace Microsoft.Android.BindingGenerator.BuildTasks;

[MSBuildMultiThreadableTask]
public sealed class CheckBindingGenerator : MultithreadedAndroidTask
{
	public override string TaskPrefix => "SBG";

	[Required]
	public string Selection { get; set; } = "";

	public string? Platform { get; set; }
	public string? FrameworkVersion { get; set; }
	public string? SdkVersion { get; set; }
	public bool BindingProject { get; set; }
	public bool TailImported { get; set; }

	public override bool RunTask ()
	{
		if (!string.Equals (Selection, "Semantic", StringComparison.OrdinalIgnoreCase)) {
			Log.LogCodedError ("XA4350", Properties.Resources.InvalidSelection, Selection);
		} else if (string.Equals (Platform, "android", StringComparison.OrdinalIgnoreCase)) {
			if (!BindingProject)
				Log.LogCodedError ("XA4350", Properties.Resources.RequiresBindingProject);
			if (!TailImported)
				Log.LogCodedError ("XA4350", Properties.Resources.MissingTailImport);
			if (!Version.TryParse (FrameworkVersion?.TrimStart ('v'), out var framework) || framework.Major != 11 ||
					!Version.TryParse (SdkVersion?.Split ('-') [0], out var sdk) || sdk.Major != 37)
				Log.LogCodedError ("XA4350", Properties.Resources.UnsupportedWorkload, FrameworkVersion ?? "", SdkVersion ?? "");
		}
		return !Log.HasLoggedErrors;
	}
}
