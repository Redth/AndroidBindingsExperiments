#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mono.Cecil;
using Java.Interop.Tools.Cecil;

namespace Java.Interop.Tools.Generator.Semantics
{
	public sealed class BindingViewContract
	{
		public int SchemaVersion { get; set; }
		public BindingViewDefinition [] Views { get; set; } = [];
	}

	public sealed class BindingViewDefinition
	{
		public string JavaType { get; set; } = "";
		public string PeerType { get; set; } = "";
		public string OwnerAssembly { get; set; } = "";
		public string Namespace { get; set; } = "";
		public string Name { get; set; } = "";
		public BindingViewMember [] Members { get; set; } = [];
	}

	public sealed class BindingViewMember
	{
		public string JavaName { get; set; } = "";
		public string JniSignature { get; set; } = "";
		public string ManagedMember { get; set; } = "";
		public string Name { get; set; } = "";
	}

	public static class BindingViewProjection
	{
		public static void ValidateRequirements (string apiFile, string ownershipFile, IEnumerable<string> assemblies,
			string reportFile, DirectoryAssemblyResolver resolver)
		{
			File.Delete (reportFile);
			using var ownership = JsonDocument.Parse (File.ReadAllText (ownershipFile));
			if (ownership.RootElement.GetProperty ("SchemaVersion").GetInt32 () != 1)
				throw new InvalidDataException ("Unsupported canonical ownership manifest version.");
			var owners = ownership.RootElement.GetProperty ("Types").EnumerateArray ().ToDictionary (
				t => t.GetProperty ("JavaType").GetString () ?? "", t => (
					Assembly: t.GetProperty ("Assembly").GetString (), Type: t.GetProperty ("ManagedType").GetString ()), StringComparer.Ordinal);
			var api = XDocument.Load (apiFile);
			var native = api.Descendants ().Where (e => e.Name == "class" || e.Name == "interface")
				.ToDictionary (e => {
					var descriptor = (string?) e.Attribute ("jni-signature") ?? throw new InvalidDataException ("A native type has no JVM descriptor.");
					if (descriptor.Length < 3 || descriptor [0] != 'L' || descriptor [descriptor.Length - 1] != ';')
						throw new InvalidDataException ($"Invalid native type descriptor '{descriptor}'.");
					return descriptor.Substring (1, descriptor.Length - 2);
				}, StringComparer.Ordinal);
			var checkedRequirements = 0;
			foreach (var file in assemblies) {
				var assembly = resolver.Load (file) ?? throw new InvalidDataException ($"Cannot load required assembly '{file}'.");
				var resource = assembly.MainModule.Resources.OfType<EmbeddedResource> ()
					.SingleOrDefault (r => r.Name == "Java.Interop.NativeRequirements.json")
					?? throw new InvalidDataException ($"Assembly '{file}' does not contain its required native contract manifest.");
				using var stream = resource.GetResourceStream ();
				using var document = JsonDocument.Parse (stream);
				if (document.RootElement.GetProperty ("SchemaVersion").GetInt32 () != 1)
					throw new InvalidDataException ($"Unsupported native requirement manifest version in '{file}'.");
				foreach (var requirement in document.RootElement.GetProperty ("Requirements").EnumerateArray ()) {
					var javaType = requirement.GetProperty ("JavaType").GetString () ?? "";
					var javaName = requirement.GetProperty ("JavaName").GetString ();
					var signature = requirement.GetProperty ("JniSignature").GetString ();
					if (!owners.TryGetValue (javaType, out var owner) ||
						owner.Assembly != requirement.GetProperty ("OwnerAssembly").GetString () ||
						owner.Type != requirement.GetProperty ("PeerType").GetString ())
						throw new InvalidDataException ($"Assembly '{assembly.Name.Name}' requires a different canonical CLR owner for '{javaType}'. Rebuild or update the conflicting ownership profile.");
					var method = native.TryGetValue (javaType, out var type) ? type.Elements ("method").SingleOrDefault (
						m => (string?) m.Attribute ("name") == javaName && (string?) m.Attribute ("jni-signature") == signature) : null;
					if (method == null)
						throw new InvalidDataException ($"Assembly '{assembly.Name.Name}' requires missing native member '{javaType}.{javaName}{signature}'. Select a compatible Maven dependency or rebuild this library.");
					if (requirement.TryGetProperty ("IsStatic", out var isStatic) &&
						isStatic.GetBoolean () != ((string?) method.Attribute ("static") == "true"))
						throw new InvalidDataException ($"Assembly '{assembly.Name.Name}' requires a different invocation kind for '{javaType}.{javaName}{signature}'.");
					if (requirement.TryGetProperty ("KotlinReturnType", out var returnType) && returnType.ValueKind != JsonValueKind.Null &&
						returnType.GetString () != (string?) method.Attribute ("kotlin-return-type"))
						throw new InvalidDataException ($"Assembly '{assembly.Name.Name}' requires a different Kotlin return contract for '{javaType}.{javaName}{signature}'.");
					checkedRequirements++;
				}
			}
			if (checkedRequirements == 0)
				throw new InvalidDataException ("No native requirements were supplied for application validation.");
			File.WriteAllText (reportFile, JsonSerializer.Serialize (new { SchemaVersion = 1, CheckedRequirements = checkedRequirements }));
		}

		public static void Generate (string contractFile, string apiFile, string outputDirectory,
			IEnumerable<string> references, DirectoryAssemblyResolver resolver)
		{
			var contract = JsonSerializer.Deserialize<BindingViewContract> (File.ReadAllText (contractFile))
				?? throw new InvalidDataException ("The binding view contract is empty.");
			if (contract.SchemaVersion != 1 || contract.Views == null || contract.Views.Length == 0)
				throw new InvalidDataException ("A version 1 binding view contract must select at least one view.");
			var api = XDocument.Load (apiFile);
			var nativeTypes = api.Descendants ().Where (e => e.Name == "class" || e.Name == "interface")
				.ToDictionary (e => (string?) e.Attribute ("jni-signature") ??
					"L" + (((string?) e.Parent?.Attribute ("name") ?? "").Replace ('.', '/') + "/" +
					((string?) e.Attribute ("name") ?? "").Replace ('.', '$')).TrimStart ('/') + ";", StringComparer.Ordinal);
			var assemblies = references.Distinct ().Select (path => resolver.Load (path) ??
				throw new InvalidDataException ($"Cannot load canonical assembly reference '{path}'.")).ToArray ();
			var outputs = new Dictionary<string, string> (StringComparer.Ordinal);
			var requirements = new List<object> ();
			foreach (var view in contract.Views) {
				if (view == null || view.Members == null || view.Members.Length == 0)
					throw new InvalidDataException ("A binding view must select at least one member.");
				ValidateIdentifier (view.Name);
				foreach (var part in view.Namespace.Split ('.'))
					ValidateIdentifier (part);
				var descriptor = "L" + view.JavaType + ";";
				if (!nativeTypes.TryGetValue (descriptor, out var nativeType))
					throw new InvalidDataException ($"View '{view.Name}' requires missing native type '{view.JavaType}'.");
				var owners = assemblies.Select (a => (Assembly: a, Type: a.MainModule.GetType (view.PeerType)))
					.Where (v => v.Type != null).ToArray ();
				if (owners.Length != 1 || owners [0].Assembly.Name.Name != view.OwnerAssembly)
					throw new InvalidDataException ($"Native type '{view.JavaType}' requires exactly one canonical CLR owner '{view.OwnerAssembly}:{view.PeerType}'.");
				var peer = owners [0].Type;
				if (peer == null || Register (peer.CustomAttributes, 0) != view.JavaType)
					throw new InvalidDataException ($"Canonical peer '{view.PeerType}' does not register '{view.JavaType}'.");
				var text = new StringBuilder ();
				text.AppendLine ("// <auto-generated />").AppendLine ("#nullable enable")
					.AppendLine ("namespace " + string.Join (".", view.Namespace.Split ('.').Select (p => "@" + p)) + " {")
					.AppendLine ("\tinternal sealed class @" + view.Name + " {")
					.AppendLine ("\t\treadonly global::" + view.PeerType + " peer;")
					.AppendLine ("\t\tpublic @" + view.Name + " (global::" + view.PeerType + " peer) {")
					.AppendLine ("\t\t\tglobal::System.ArgumentNullException.ThrowIfNull (peer);")
					.AppendLine ("\t\t\tthis.peer = peer;").AppendLine ("\t\t}");
				var names = new HashSet<string> (StringComparer.Ordinal);
				foreach (var member in view.Members) {
					ValidateIdentifier (member.Name);
					if (member.Name == "peer" || member.Name == view.Name || !names.Add (member.Name))
						throw new InvalidDataException ($"Duplicate view member '{view.Name}.{member.Name}'.");
					var nativeMethods = nativeType.Elements ("method").Where (m => (string?) m.Attribute ("name") == member.JavaName &&
						(member.JniSignature.Length == 0 || (string?) m.Attribute ("jni-signature") == member.JniSignature)).ToArray ();
					if (nativeMethods.Length == 0)
						throw new InvalidDataException ($"View '{view.Name}.{member.Name}' requires missing native member '{view.JavaType}.{member.JavaName}{member.JniSignature}'.");
					if (nativeMethods.Length != 1)
						throw new InvalidDataException ($"View '{view.Name}.{member.Name}' is ambiguous. Select an exact JNI signature for overloaded member '{member.JavaName}'.");
					var nativeMethod = nativeMethods [0];
					member.JniSignature = (string?) nativeMethod.Attribute ("jni-signature") ??
						throw new InvalidDataException ($"Native member '{member.JavaName}' has no verified JVM descriptor.");
					if ((string?) nativeMethod.Attribute ("static") == "true")
						throw new InvalidDataException ($"Instance view '{view.Name}' cannot select static native member '{member.JavaName}'.");
					if (!peer.Methods.Any (m => Register (m.CustomAttributes, 0) == member.JavaName &&
						Register (m.CustomAttributes, 1) == member.JniSignature))
						throw new InvalidDataException ($"Canonical owner '{view.PeerType}' does not implement native requirement '{member.JavaName}{member.JniSignature}'.");
					var matches = peer.Methods.Where (m => m.Name == member.ManagedMember && m.IsPublic && !m.IsStatic &&
						!m.HasGenericParameters && !m.IsSpecialName).ToArray ();
					if (matches.Length != 1)
						throw new InvalidDataException ($"View '{view.Name}.{member.Name}' requires one unambiguous public instance method '{view.PeerType}.{member.ManagedMember}'.");
					var method = matches [0];
					var parameters = method.Parameters.Select ((p, i) => TypeName (p.ParameterType) +
						(p.IsOptional && !p.ParameterType.IsValueType ? "?" : "") + " p" + i +
						(p.IsOptional ? " = default" : "")).ToArray ();
					text.AppendLine ("\t\tpublic " + TypeName (method.ReturnType) + " @" + member.Name + " (" + string.Join (", ", parameters) + ") {")
						.AppendLine ("\t\t\tif (!peer.PeerReference.IsValid) throw new global::System.ObjectDisposedException (nameof (peer));")
						.AppendLine ("\t\t\t" + (method.ReturnType.FullName == "System.Void" ? "" : "return ") + "peer.@" + method.Name +
							" (" + string.Join (", ", method.Parameters.Select ((p, i) => "p" + i)) + ");")
						.AppendLine ("\t\t}");
					requirements.Add (new { view.JavaType, view.OwnerAssembly, view.PeerType, member.JavaName, member.JniSignature,
						member.ManagedMember, IsStatic = false, KotlinReturnType = (string?) nativeMethod.Attribute ("kotlin-return-type") });
				}
				text.AppendLine ("\t}").AppendLine ("}");
				if (!outputs.TryAdd (view.Namespace + "." + view.Name + ".cs", text.ToString ()))
					throw new InvalidDataException ($"Duplicate binding view '{view.Namespace}.{view.Name}'.");
			}
			Directory.CreateDirectory (outputDirectory);
			foreach (var output in outputs)
				File.WriteAllText (Path.Combine (outputDirectory, output.Key), output.Value);
			File.WriteAllText (Path.Combine (outputDirectory, "native-requirements.json"),
				JsonSerializer.Serialize (new { SchemaVersion = 1, Requirements = requirements }, new JsonSerializerOptions { WriteIndented = true }));
		}

		static string? Register (IEnumerable<CustomAttribute> attributes, int index) =>
			attributes.FirstOrDefault (a => a.AttributeType.FullName == "Android.Runtime.RegisterAttribute") is CustomAttribute registration &&
			registration.ConstructorArguments.Count > index ? registration.ConstructorArguments [index].Value as string : null;

		static void ValidateIdentifier (string value)
		{
			if (!Regex.IsMatch (value, "^[A-Za-z_][A-Za-z0-9_]*$"))
				throw new InvalidDataException ($"Invalid binding view identifier '{value}'.");
		}

		static string TypeName (TypeReference type)
		{
			if (type is ArrayType array)
				return TypeName (array.ElementType) + "[" + new string (',', array.Rank - 1) + "]";
			if (type is GenericInstanceType generic)
				return "global::" + generic.ElementType.FullName.Split ('`') [0].Replace ('/', '.') +
					"<" + string.Join (", ", generic.GenericArguments.Select (TypeName)) + ">";
			if (type is TypeSpecification || type.HasGenericParameters || type is GenericParameter)
				throw new InvalidDataException ($"Unsupported binding view signature type '{type.FullName}'.");
			return type.FullName == "System.Void" ? "void" : "global::" + type.FullName.Replace ('/', '.');
		}
	}
}
