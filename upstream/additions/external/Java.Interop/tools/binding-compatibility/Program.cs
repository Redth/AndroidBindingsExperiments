using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Java.Interop.Tools.Generator.Semantics;
using Mono.Cecil;

if (args.Length < 3)
	throw new ArgumentException ("Usage: binding-compatibility extract OUTPUT REFERENCES ROOT.dll... | verify PROFILE CANDIDATE.dll | inspect ASSEMBLY TYPE");
switch (args [0]) {
case "extract": {
	var profile = BindingCompatibilityProfile.Extract (args.Skip (3), args [2]);
	File.WriteAllText (args [1], JsonSerializer.Serialize (profile, BindingCompatibilityProfile.JsonOptions));
	Console.WriteLine ($"Protected roots: {profile.Roots.Length}; closure types: {profile.Types.Length}; input assemblies: {profile.Inputs.Length}");
	break;
}
case "verify": {
	var profile = JsonSerializer.Deserialize<BindingCompatibilityProfile> (File.ReadAllText (args [1])) ??
		throw new InvalidDataException ("Empty compatibility profile.");
	using var candidate = AssemblyDefinition.ReadAssembly (args [2]);
	var errors = profile.Validate (candidate);
	foreach (var error in errors)
		Console.Error.WriteLine (error);
	Console.WriteLine ($"Compatibility errors: {errors.Count}");
	return errors.Count == 0 ? 0 : 1;
}
case "select": {
	var full = JsonSerializer.Deserialize<BindingCompatibilityProfile> (File.ReadAllText (args [1])) ??
		throw new InvalidDataException ("Empty compatibility profile.");
	var selection = BindingCompatibilityUsage.Select (full, args.Skip (4), args [3]);
	File.WriteAllText (args [2], JsonSerializer.Serialize (selection.Profile, BindingCompatibilityProfile.JsonOptions));
	File.WriteAllText (args [2] + ".usage.json", JsonSerializer.Serialize (selection, BindingCompatibilityProfile.JsonOptions));
	Console.WriteLine ($"Selected members: {selection.Profile.Types.Sum (t => t.Members.Length)}; full members: {full.Types.Sum (t => t.Members.Length)}.");
	break;
}
case "inspect": {
	using var assembly = AssemblyDefinition.ReadAssembly (args [1]);
	var type = BindingCompatibilityProfile.Flatten (assembly.MainModule.Types).Single (t => t.FullName == args [2]);
	Console.WriteLine (JsonSerializer.Serialize (BindingCompatibilityProfile.Describe (type), BindingCompatibilityProfile.JsonOptions));
	break;
}
default:
	throw new ArgumentException ($"Unknown command {args [0]}.");
}
return 0;
