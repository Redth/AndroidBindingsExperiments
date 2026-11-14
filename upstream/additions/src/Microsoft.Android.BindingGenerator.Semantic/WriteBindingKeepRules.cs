#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Android.Build.Tasks;
using Microsoft.Build.Framework;
using Properties = Microsoft.Android.BindingGenerator.Semantic.Properties;

namespace Microsoft.Android.BindingGenerator.BuildTasks;

[MSBuildMultiThreadableTask]
public sealed class WriteBindingKeepRules : MultithreadedAndroidTask
{
	public override string TaskPrefix => "SBG";

	[Required]
	public string OwnershipReport { get; set; } = "";

	[Required]
	public string OutputFile { get; set; } = "";

	public override bool RunTask ()
	{
		string output = TaskEnvironment.GetAbsolutePath (OutputFile);
		bool success = false;
		try {
			using var report = JsonDocument.Parse (File.ReadAllText (TaskEnvironment.GetAbsolutePath (OwnershipReport)));
			var names = report.RootElement.GetProperty ("Types").EnumerateArray ()
				.Select (t => t.GetProperty ("JavaType").GetString () ??
					throw new InvalidDataException (Properties.Resources.MissingPeerName))
				.Distinct (StringComparer.Ordinal).OrderBy (n => n, StringComparer.Ordinal).ToArray ();
			var rules = new StringBuilder ();
			foreach (var name in names) {
				if (!Regex.IsMatch (name, @"^[\p{L}\p{N}_$/]+$", RegexOptions.CultureInvariant))
					throw new InvalidDataException (string.Format (CultureInfo.CurrentCulture, Properties.Resources.InvalidPeerName, name));
				rules.Append ("-keep class ").Append (name.Replace ('/', '.')).AppendLine (" {");
				rules.AppendLine ("    public *;");
				rules.AppendLine ("    protected *;");
				rules.AppendLine ("}");
			}
			Directory.CreateDirectory (Path.GetDirectoryName (output) ?? TaskEnvironment.ProjectDirectory);
			File.WriteAllText (output, rules.ToString (), new UTF8Encoding (false));
			success = true;
			return true;
		} finally {
			if (!success)
				File.Delete (output);
		}
	}
}
