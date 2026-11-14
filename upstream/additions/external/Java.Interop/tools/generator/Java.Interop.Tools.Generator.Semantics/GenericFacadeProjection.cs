using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Java.Interop.Tools.JavaTypeSystem.Models;
using MonoDroid.Generation;
using generator.SourceWriters;
using Xamarin.SourceWriter;
using Xamarin.Android.Tools.Bytecode;

namespace Java.Interop.Tools.Generator.Semantics
{
	public sealed record GenericProjectionDecision (string DeclaringType, string NativeSlot, string SourceType, string ManagedType, string Reason);

	public sealed class GenericFacadeProjection
	{
		const string runtime = "global::Java.Interop.Kotlin.";
		const string constructors = "[global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers (global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors | global::System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.NonPublicConstructors)] ";
		readonly CodeGenerationOptions options;
		readonly Dictionary<string, XElement> definitions;
		readonly Dictionary<string, GenBase> peers;
		readonly Dictionary<string, string> views = new Dictionary<string, string> (StringComparer.Ordinal);
		readonly Dictionary<string, string> mappedViews = new Dictionary<string, string> (StringComparer.Ordinal);
		readonly List<GenericProjectionDecision> decisions = new List<GenericProjectionDecision> ();

		sealed record Shape (string Type, string Converter);
		sealed record Member (XElement Node, Dictionary<string, string> Substitutions);
		sealed record Parameter (string JavaName, string Name, string Converter, string[] Bounds);

		public IReadOnlyList<GenericProjectionDecision> Decisions => decisions.AsReadOnly ();

		public GenericFacadeProjection (XDocument facts, IEnumerable<GenBase> definitions, CodeGenerationOptions options)
		{
			this.options = options ?? throw new ArgumentNullException (nameof (options));
			var source = new XDocument (facts ?? throw new ArgumentNullException (nameof (facts)));
			new BindingAccessibilityProjection (source).Apply (source);
			this.definitions = source.Root.Elements ("package").SelectMany (p => p.Elements ()
				.Where (t => t.Name == "class" || t.Name == "interface")
				.Select (t => (Name: BindingTypeDefinition.GetJavaName (p, t), Type: t)))
				.ToDictionary (p => p.Name, p => p.Type, StringComparer.Ordinal);
			peers = Flatten (definitions).Where (g => g.IsGeneratable && EffectivePublic (g))
				.ToDictionary (g => g.JavaName, StringComparer.Ordinal);
			var names = new HashSet<string> (peers.Values.Select (g => g.FullName), StringComparer.Ordinal);
			foreach (var pair in this.definitions.OrderBy (p => p.Key, StringComparer.Ordinal)) {
				if (!peers.TryGetValue (pair.Key, out var peer) || (string) pair.Value.Attribute ("visibility") != "public")
					continue;
				if (!TypeParameters (pair.Value).Any () &&
					!(((string) pair.Value.Attribute ("extends-generic-aware") ?? "").Contains ('<')) &&
					!pair.Value.Elements ("implements").Any (i => ((string) i.Attribute ("name-generic-aware") ?? "").Contains ('<')) &&
					!pair.Value.Elements ("field").Any (f => ((string) f.Attribute ("type-generic-aware") ?? "").Contains ('<')) &&
					!pair.Value.Elements ("method").Any (m =>
					TypeParameters (m).Any () || ((string) m.Attribute ("return") ?? "").Contains ('<') ||
					m.Elements ("parameter").Any (p => ((string) p.Attribute ("type") ?? "").Contains ('<'))))
					continue;
				var local = peer.FullName.Substring (peer.Namespace.Length == 0 ? 0 : peer.Namespace.Length + 1).Replace ('.', '_') + "View";
				var name = peer.Namespace.Length == 0 ? local : peer.Namespace + "." + local;
				while (!names.Add (name))
					name += "Generic";
				views.Add (pair.Key, name);
			}
			if (options.ExperimentalGenericFacades)
				foreach (var pair in views) {
					var sourceType = this.definitions [pair.Key];
					var parameters = AllocateParameters (TypeParameters (sourceType), new HashSet<string> (StringComparer.Ordinal)).ToArray ();
					var scope = parameters.ToDictionary (p => p.JavaName, StringComparer.Ordinal);
					var constrained = parameters.Any (p => ConstraintPeers (p, scope).Any ()) ||
						Members (pair.Key, new Dictionary<string, string> (), new HashSet<string> (StringComparer.Ordinal)).Any (m => {
							var methodParameters = AllocateParameters (TypeParameters (m.Node), new HashSet<string> (StringComparer.Ordinal))
								.Select (p => p with { Bounds = p.Bounds.Select (b => Substitute (b, MethodSubstitutions (m))).ToArray () }).ToArray ();
							var methodScope = new Dictionary<string, Parameter> (scope, StringComparer.Ordinal);
							foreach (var p in methodParameters)
								methodScope [p.JavaName] = p;
							return methodParameters.Any (p => ConstraintPeers (p, methodScope).Any ());
						});
					if (!constrained)
						continue;
					var name = pair.Value.EndsWith ("View", StringComparison.Ordinal) ? pair.Value.Substring (0, pair.Value.Length - 4) + "MappedView" :
						pair.Value + "Mapped";
					while (!names.Add (name))
						name += "Generic";
					mappedViews.Add (pair.Key, name);
				}
		}

		public void Generate (GenerationInfo info)
		{
			foreach (var pair in views.OrderBy (p => p.Key, StringComparer.Ordinal)) {
				using var writer = info.OpenStream (pair.Value + ".Generic");
				writer.Write (GenerateType (pair.Key));
				if (mappedViews.TryGetValue (pair.Key, out var mappedName)) {
					using var mappedWriter = info.OpenStream (mappedName + ".Generic");
					mappedWriter.Write (GenerateType (pair.Key, mapped: true));
				}
			}
		}

		public void WriteReport (string path)
		{
			File.WriteAllText (path, JsonSerializer.Serialize (new {
				SchemaVersion = 1,
				Representation = "managed-facades-over-canonical-peers",
				Facades = views.OrderBy (p => p.Key, StringComparer.Ordinal).Select (p =>
					new { JavaType = p.Key, ManagedView = p.Value, CanonicalPeer = peers [p.Key].FullName,
						MappedView = mappedViews.TryGetValue (p.Key, out var mappedName) ? mappedName : null }),
				Decisions = decisions.Distinct ().ToArray (),
			}, new JsonSerializerOptions { WriteIndented = true }));
		}

		public string GenerateType (string javaName, bool mapped = false)
		{
			var source = definitions [javaName];
			var peer = peers [javaName];
			var fullName = mapped ? mappedViews [javaName] : views [javaName];
			var dot = fullName.LastIndexOf ('.');
			var ns = dot < 0 ? "" : fullName.Substring (0, dot);
			var name = dot < 0 ? fullName : fullName.Substring (dot + 1);
			var parameters = AllocateParameters (TypeParameters (source), new HashSet<string> (StringComparer.Ordinal) { name, "Peer", "GetJvmType" }).ToArray ();
			var scope = parameters.ToDictionary (p => p.JavaName, StringComparer.Ordinal);
			var self = name + Arguments (parameters.Select (p => p.Name));
			var canonical = options.GetOutputName (peer.FullName);
			var text = new StringBuilder ("#nullable enable\n");
			if (ns.Length > 0)
				text.Append ("namespace ").Append (ns).AppendLine (" {");
			var typeAttributes = new List<AttributeWriter> ();
			SourceWriterExtensions.AddSupportedOSPlatform (typeAttributes, peer, options);
			SourceWriterExtensions.AddObsolete (typeAttributes, peer.DeprecatedComment, options, peer.IsDeprecated, deprecatedSince: peer.DeprecatedSince);
			AppendBoundDocumentation (text, parameters, mapped);
			AppendAttributes (text, typeAttributes);
			text.Append ("public sealed partial class ").Append (name).Append (Arguments (parameters.Select (p => constructors + p.Name)));
			if (options.ExperimentalGenericFacades)
				text.Append (" : ").Append (JavaProjectionContract.Name (canonical));
			AppendConstraints (text, parameters, scope, javaName, "", mapped);
			text.AppendLine (" {").Append ("\tstatic readonly global::Java.Interop.JniPeerMembers __members = new global::Java.Interop.JniPeerMembers (")
				.Append (Literal (peer.RawJniName)).Append (", typeof (").Append (canonical).AppendLine ("));")
				.Append ("\tpublic ").Append (canonical).AppendLine (" Peer { get; }");
			foreach (var parameter in parameters)
				text.Append ("\treadonly ").Append (runtime).Append ("JvmType<").Append (parameter.Name).Append ("> ").Append (parameter.Converter).AppendLine (";");
			text.Append ("\tpublic ").Append (name).Append (" (").Append (canonical).Append (" peer");
			AppendConverterParameters (text, parameters);
			text.AppendLine (") {")
				.AppendLine ("\t\tif (peer == null) throw new global::System.ArgumentNullException (nameof (peer));")
				.AppendLine ("\t\tPeer = peer;");
			InitializeConverters (text, parameters, scope, javaName, "", instance: true);
			text.AppendLine ("\t}");
			text.Append ("\tpublic static ").Append (runtime).Append ("JvmType<").Append (self).Append ("> GetJvmType (")
				.Append (string.Join (", ", parameters.Select (p => runtime + "JvmType<" + p.Name + ">? " + p.Converter + " = null"))).AppendLine (") {");
			InitializeConverters (text, parameters, scope, javaName, "", instance: false);
			text.Append ("\t\treturn new ").Append (runtime).Append ("JvmType<").Append (self).Append ("> (")
				.Append (Literal (peer.RawJniName)).Append (", __peer => __peer == null ? null : new ").Append (self)
				.Append (" (").Append (runtime).Append ("KotlinCoroutines.GetPeer<").Append (canonical).Append ("> (__peer)")
				.Append (parameters.Length == 0 ? "" : ", " + string.Join (", ", parameters.Select (p => p.Converter)))
				.AppendLine ("), __view => __view?.Peer);")
				.AppendLine ("\t}");
			if (parameters.Length == 1 && KotlinTypeProjection.IsFlow (new KotlinTypeContract { Classifier = peer.RawJniName })) {
				text.Append ("\tpublic global::System.Collections.Generic.IAsyncEnumerable<").Append (parameters [0].Name)
					.Append ("?> AsAsyncEnumerable (global::System.Threading.CancellationToken cancellationToken = default, global::Java.Interop.IJavaPeerable? coroutineContext = null) => ")
					.Append (runtime).Append ("KotlinCoroutines.AsAsyncEnumerable (Peer, ").Append (parameters [0].Converter).AppendLine (".Read, cancellationToken, coroutineContext);");
			}
			var signatures = new HashSet<string> (StringComparer.Ordinal);
			var nativeMembers = new HashSet<string> (StringComparer.Ordinal);
			foreach (var member in Members (javaName, new Dictionary<string, string> (), new HashSet<string> (StringComparer.Ordinal))) {
				var method = member.Node;
				if (!nativeMembers.Add (method.Name + ":" + (string) method.Attribute ("name") + ":" + (string) method.Attribute ("jni-signature")))
					continue;
				if ((string) method.Attribute ("visibility") != "public" || (string) method.Attribute ("api-classification") == "Infrastructure" ||
					(string) method.Attribute ("synthetic") == "true" || (string) method.Attribute ("bridge") == "true")
					continue;
				if (method.Name == "field")
					AppendField (text, javaName, member, scope, self, signatures, mapped);
				else if ((string) method.Attribute ("kotlin-suspend") == "true")
					AppendSuspendMethod (text, javaName, member, scope, signatures, mapped);
				else
					AppendMethod (text, javaName, member, scope, self, signatures, mapped);
			}
			if (source.Name == "class" && (string) source.Attribute ("abstract") != "true")
				foreach (var constructor in source.Elements ("constructor").Where (c => (string) c.Attribute ("visibility") == "public" &&
					(string) c.Attribute ("synthetic") != "true"))
					AppendMethod (text, javaName, new Member (constructor, new Dictionary<string, string> ()), scope, self, signatures, mapped);
			text.AppendLine ("}");
			if (ns.Length > 0)
				text.AppendLine ("}");
			return text.ToString ();
		}

		void AppendMethod (StringBuilder text, string javaName, Member member, Dictionary<string, Parameter> classScope, string self, HashSet<string> signatures, bool mapped)
		{
			var method = member.Node;
			var substitutions = MethodSubstitutions (member);
			var constructor = method.Name == "constructor";
			var nativeName = constructor ? "<init>" : (string) method.Attribute ("name");
			var descriptor = (string) method.Attribute ("jni-signature");
			if (!JniSignatureUtilities.TryParseMethodSignature (descriptor, out var nativeParameters, out var nativeReturn))
				throw new InvalidDataException ($"Invalid generic member descriptor: {javaName}.{nativeName}{descriptor}.");
			var slot = nativeName + descriptor;
			var occupied = new HashSet<string> (classScope.Values.Select (p => p.Name), StringComparer.Ordinal);
			var generic = AllocateParameters (TypeParameters (method), occupied)
				.Select (p => p with { Bounds = p.Bounds.Select (b => Substitute (b, substitutions)).ToArray () }).ToArray ();
			var scope = new Dictionary<string, Parameter> (classScope, StringComparer.Ordinal);
			foreach (var parameter in generic)
				scope [parameter.JavaName] = parameter;
			var nodes = method.Elements ("parameter").ToArray ();
			if (constructor && nodes.Length + 1 == nativeParameters.Length &&
				peers [javaName].EnclosingType is GenBase enclosing && nativeParameters [0] == enclosing.JniName)
				nodes = new [] { new XElement ("parameter", new XAttribute ("name", "outer"),
					new XAttribute ("type", TypeNameUtilities.JniSignatureToJavaTypeName (nativeParameters [0]))) }.Concat (nodes).ToArray ();
			if (nodes.Length != nativeParameters.Length)
				throw new InvalidDataException ($"Generic parameter count changed: {javaName}.{slot}.");
			var shapes = nodes.Select (p => Project (ParseSubstituted ((string) p.Attribute ("type"), substitutions), scope, javaName, slot, mapped)).ToArray ();
			var result = constructor ? null : Project (ParseSubstituted ((string) method.Attribute ("return"), substitutions), scope, javaName, slot, mapped);
			var managed = constructor ? null : peers [javaName].GetAllMethods ().FirstOrDefault (m => m.JavaName == nativeName && m.JniSignature == descriptor);
			var name = constructor ? "Create" : Identifier (managed?.Name ?? (string) method.Attribute ("managedName") ?? nativeName, capitalize: true);
			if (name.TrimStart ('@') == "Peer" || name.TrimStart ('@') == "GetJvmType" || name.TrimStart ('@') == self.Split ('<') [0] ||
				signatures.Contains ("field:" + name.TrimStart ('@')))
				name = "@Invoke" + name.TrimStart ('@');
			var signature = name.TrimStart ('@') + "`" + generic.Length + "(" +
				string.Join (",", shapes.Select (s => SignatureType (s.Type, generic))) + ")";
			if (!signatures.Add (signature)) {
				if (constructor) {
					name = "CreateInstance";
					while (!signatures.Add (name + signature))
						name += "Value";
				} else {
					name += "Native" + Convert.ToHexString (System.Security.Cryptography.SHA256.HashData (Encoding.UTF8.GetBytes (slot))).Substring (0, 8);
					signatures.Add (name + signature);
				}
			}
			var isStatic = constructor || (string) method.Attribute ("static") == "true";
			var localNames = new HashSet<string> (scope.Values.Select (p => p.Converter).Concat (
				new [] { "__args", "__result", "__value", "__peer", "Peer" }).Concat (Enumerable.Range (0, shapes.Length).Select (i => "__argument" + i)), StringComparer.Ordinal);
			var names = nodes.Select ((p, i) => {
				var value = Identifier ((string) p.Attribute ("name") ?? "value" + i);
				while (!localNames.Add (value.TrimStart ('@')))
					value = "@_" + value.TrimStart ('@');
				return value;
			}).ToArray ();
			var availability = constructor ? (MethodBase) (peers [javaName] as ClassGen)?.Ctors.FirstOrDefault (c => c.JniSignature == descriptor) : managed;
			AppendBoundDocumentation (text, generic, mapped);
			if (availability != null) {
				var attributes = new List<AttributeWriter> ();
				SourceWriterExtensions.AddSupportedOSPlatform (attributes, availability, options);
				SourceWriterExtensions.AddObsolete (attributes, availability.Deprecated, options, deprecatedSince: availability.DeprecatedSince);
				AppendAttributes (text, attributes);
			}
			text.Append ("\tpublic unsafe ").Append (isStatic ? "static " : "")
				.Append (constructor ? self : NullableType (result.Type, nativeReturn != "V" && IsReference (nativeReturn) && (string) method.Attribute ("return-not-null") != "true"))
				.Append (' ').Append (name).Append (Arguments (generic.Select (p => constructors + p.Name))).Append (" (")
				.Append (string.Join (", ", shapes.Select ((s, i) => NullableType (s.Type, IsReference (nativeParameters [i]) && (string) nodes [i].Attribute ("not-null") != "true") + " " + names [i])));
			var converters = constructor ? classScope.Values.Concat (generic).ToArray () : generic;
			foreach (var parameter in converters)
				text.Append (shapes.Length == 0 && parameter == converters.First () ? "" : ", ")
					.Append (runtime).Append ("JvmType<").Append (parameter.Name).Append (">? ").Append (parameter.Converter).Append (" = null");
			text.Append (")");
			AppendConstraints (text, generic, scope, javaName, slot, mapped);
			text.AppendLine (" {");
			InitializeConverters (text, converters, scope, javaName, slot, instance: false);
			for (var i = 0; i < shapes.Length; i++)
				if (IsReference (nativeParameters [i]))
					text.Append ("\t\tusing var __argument").Append (i).Append (" = ").Append (shapes [i].Converter).Append (".CreateArgument (").Append (names [i]).AppendLine (");");
			if (shapes.Length > 0) {
				text.Append ("\t\tglobal::Java.Interop.JniArgumentValue* __args = stackalloc global::Java.Interop.JniArgumentValue [").Append (shapes.Length).AppendLine ("];");
				for (var i = 0; i < shapes.Length; i++)
					text.Append ("\t\t__args [").Append (i).Append ("] = ").Append (IsReference (nativeParameters [i]) ? "__argument" + i + ".Value" :
						"new global::Java.Interop.JniArgumentValue (" +
						(shapes [i].Type is "byte" or "ushort" or "uint" or "ulong" ? "unchecked ((" + NativePrimitive (nativeParameters [i]) + ") " + names [i] + ")" : names [i]) + ")").AppendLine (";");
			}
			var args = shapes.Length == 0 ? "null" : "__args";
			if (constructor)
				text.Append ("\t\tvar __result = __members.JniPeerType.NewObject (__members.JniPeerType.GetConstructor (").Append (Literal (descriptor)).Append ("), ").Append (args).AppendLine (");");
			else {
				var operation = NativeOperation (nativeReturn);
				text.Append ("\t\t").Append (nativeReturn == "V" ? "" : "var __result = ").Append ("__members.")
					.Append (isStatic ? "StaticMethods.Invoke" : "InstanceMethods.InvokeVirtual").Append (operation).Append ("Method (")
					.Append (Literal (nativeName + "." + descriptor)).Append (isStatic ? ", " : ", this.Peer, ").Append (args).AppendLine (");");
			}
			if (constructor)
				text.AppendLine ("\t\ttry {")
					.Append ("\t\t\tvar __peer = global::Java.Interop.JniEnvironment.Runtime.ValueManager.GetValue<").Append (options.GetOutputName (peers [javaName].FullName))
					.AppendLine ("> (ref __result, global::Java.Interop.JniObjectReferenceOptions.CopyAndDispose);")
					.AppendLine ("\t\t\tif (__peer == null) throw new global::System.InvalidOperationException (\"The JVM did not create the canonical peer.\");")
					.Append ("\t\t\treturn new ").Append (self).Append (" (__peer")
					.Append (classScope.Count == 0 ? "" : ", " + string.Join (", ", classScope.Values.Select (p => p.Converter))).AppendLine (");")
					.AppendLine ("\t\t} finally { global::Java.Interop.JniObjectReference.Dispose (ref __result); }");
			else if (IsReference (nativeReturn)) {
				text.Append ("\t\tvar __value = ").Append (result.Converter).AppendLine (".Read (ref __result, global::Java.Interop.JniObjectReferenceOptions.CopyAndDispose);");
				if ((string) method.Attribute ("return-not-null") == "true")
					text.AppendLine ("\t\tif (__value is null) throw new global::System.InvalidOperationException (\"The JNI result violates its non-null contract.\");");
				text.AppendLine ("\t\treturn __value;");
			} else if (nativeReturn != "V")
				text.Append ("\t\treturn ").Append (result.Type is "byte" or "ushort" or "uint" or "ulong" ? "unchecked ((" + result.Type + ") __result)" : "__result").AppendLine (";");
			text.AppendLine ("\t}");
		}

		void AppendField (StringBuilder text, string javaName, Member member, Dictionary<string, Parameter> scope, string self, HashSet<string> signatures, bool mapped)
		{
			var field = member.Node;
			var nativeName = BindingMethodDefinition.Required (field, "name");
			var descriptor = BindingMethodDefinition.Required (field, "jni-signature");
			var shape = Project (ParseSubstituted ((string) field.Attribute ("type-generic-aware") ?? (string) field.Attribute ("type"), member.Substitutions),
				scope, javaName, nativeName + ":" + descriptor, mapped);
			var managed = peers [javaName].Fields.FirstOrDefault (f => f.JavaName == nativeName);
			var name = Identifier (managed?.Name ?? nativeName, capitalize: true);
			if (name.TrimStart ('@') is "Peer" or "GetJvmType" || name.TrimStart ('@') == self.Split ('<') [0] ||
				scope.Values.Any (p => p.Name.TrimStart ('@') == name.TrimStart ('@')) ||
				signatures.Any (s => s.StartsWith (name.TrimStart ('@') + "`", StringComparison.Ordinal)))
				name = "@Field" + name.TrimStart ('@');
			while (!signatures.Add ("field:" + name.TrimStart ('@')))
				name += "Value";
			var isStatic = (string) field.Attribute ("static") == "true";
			var prefix = "__members." + (isStatic ? "StaticFields" : "InstanceFields");
			var receiver = isStatic ? "" : ", this.Peer";
			var reference = IsReference (descriptor);
			var key = Literal (nativeName + "." + descriptor);
			if (managed != null) {
				var attributes = new List<AttributeWriter> ();
				SourceWriterExtensions.AddSupportedOSPlatform (attributes, managed, options);
				SourceWriterExtensions.AddObsolete (attributes, managed.DeprecatedComment, options, managed.IsDeprecated, deprecatedSince: managed.DeprecatedSince);
				AppendAttributes (text, attributes);
			}
			text.Append ("\tpublic unsafe ").Append (isStatic ? "static " : "")
				.Append (NullableType (shape.Type, reference && (string) field.Attribute ("not-null") != "true")).Append (' ').Append (name).AppendLine (" {")
				.AppendLine ("\t\tget {")
				.Append ("\t\t\tvar __result = ").Append (prefix).Append (".Get").Append (NativeOperation (descriptor))
				.Append ("Value (").Append (key).Append (receiver).AppendLine (");");
			if (reference) {
				text.Append ("\t\t\tvar __value = ").Append (shape.Converter).AppendLine (".Read (ref __result, global::Java.Interop.JniObjectReferenceOptions.CopyAndDispose);");
				if ((string) field.Attribute ("not-null") == "true")
					text.AppendLine ("\t\t\tif (__value is null) throw new global::System.InvalidOperationException (\"The JNI field violates its non-null contract.\");");
				text.AppendLine ("\t\t\treturn __value;");
			} else
				text.Append ("\t\t\treturn ").Append (shape.Type is "byte" or "ushort" or "uint" or "ulong" ? "unchecked ((" + shape.Type + ") __result)" : "__result").AppendLine (";");
			text.AppendLine ("\t\t}");
			if ((string) field.Attribute ("final") != "true") {
				text.AppendLine ("\t\tset {");
				if (reference)
					text.Append ("\t\t\tusing var __argument = ").Append (shape.Converter).AppendLine (".CreateArgument (value);");
				var value = reference ? "__argument.Reference" : shape.Type is "byte" or "ushort" or "uint" or "ulong" ?
					"unchecked ((" + NativePrimitive (descriptor) + ") value)" : "value";
				text.Append ("\t\t\t").Append (prefix).Append (".SetValue (").Append (key).Append (receiver).Append (", ").Append (value).AppendLine (");")
					.AppendLine ("\t\t}");
			}
			text.AppendLine ("\t}");
		}

		void AppendSuspendMethod (StringBuilder text, string javaName, Member member, Dictionary<string, Parameter> classScope, HashSet<string> signatures, bool mapped)
		{
			var method = member.Node;
			var substitutions = MethodSubstitutions (member);
			var nativeName = BindingMethodDefinition.Required (method, "name");
			var descriptor = BindingMethodDefinition.Required (method, "jni-signature");
			if (!JniSignatureUtilities.TryParseMethodSignature (descriptor, out var native, out var nativeReturn) || native.Length == 0 ||
				native [native.Length - 1] != "Lkotlin/coroutines/Continuation;" || nativeReturn != "Ljava/lang/Object;")
				throw new InvalidDataException ($"Invalid suspend ABI for generic view '{javaName}.{nativeName}{descriptor}'.");
			var occupied = new HashSet<string> (classScope.Values.Select (p => p.Name.TrimStart ('@')), StringComparer.Ordinal);
			var generic = AllocateParameters (TypeParameters (method), occupied)
				.Select (p => p with { Bounds = p.Bounds.Select (b => Substitute (b, substitutions)).ToArray () }).ToArray ();
			var scope = new Dictionary<string, Parameter> (classScope, StringComparer.Ordinal);
			foreach (var parameter in generic)
				scope [parameter.JavaName] = parameter;
			var parameters = method.Elements ("parameter").Take (native.Length - 1).ToArray ();
			var shapes = parameters.Select (p => Project (ParseSubstituted ((string) p.Attribute ("type"), substitutions), scope, javaName, nativeName + descriptor, mapped)).ToArray ();
			var contract = method.Attribute ("kotlin-return-type") is XAttribute value ?
				JsonSerializer.Deserialize<KotlinTypeContract> (value.Value) : null;
			var unit = contract?.Classifier == "kotlin/Unit" && !contract.Nullable;
			var result = contract == null ? new Shape ("global::Java.Lang.Object", runtime + "JvmType<global::Java.Lang.Object>.Default") :
				Project (ParseSubstituted (JavaType (contract), substitutions), scope, javaName, nativeName + descriptor, mapped);
			if (contract?.Nullable == true && result.Type is "bool" or "sbyte" or "char" or "short" or "int" or "long" or "float" or "double")
				result = new Shape (result.Type + "?", runtime + "JvmType<" + result.Type + "?>.Default");
			var managed = peers [javaName].GetAllMethods ().FirstOrDefault (m => m.JavaName == nativeName && m.JniSignature == descriptor);
			var name = Identifier (managed?.Name ?? nativeName, capitalize: true);
			if (!name.EndsWith ("Async", StringComparison.Ordinal))
				name += "Async";
			var suffix = "`" + generic.Length + "(" + string.Join (",", shapes.Select (s => s.Type)) + ")";
			while (!signatures.Add (name.TrimStart ('@') + suffix))
				name += "Kotlin";
			var isStatic = (string) method.Attribute ("static") == "true";
			var names = parameters.Select ((p, i) => "@__value" + i).ToArray ();
			var nullable = contract == null || contract.Nullable || contract.TypeParameter != null;
			var returnType = unit ? "global::System.Threading.Tasks.Task" :
				"global::System.Threading.Tasks.Task<" + NullableType (result.Type, nullable) + ">";
			AppendBoundDocumentation (text, generic, mapped);
			if (managed != null) {
				var attributes = new List<AttributeWriter> ();
				SourceWriterExtensions.AddSupportedOSPlatform (attributes, managed, options);
				SourceWriterExtensions.AddObsolete (attributes, managed.Deprecated, options, deprecatedSince: managed.DeprecatedSince);
				AppendAttributes (text, attributes);
			}
			text.Append ("\tpublic unsafe ").Append (isStatic ? "static " : "").Append (returnType).Append (' ').Append (name)
				.Append (Arguments (generic.Select (p => constructors + p.Name))).Append (" (")
				.Append (string.Join (", ", shapes.Select ((s, i) => NullableType (s.Type, IsReference (native [i]) && (string) parameters [i].Attribute ("not-null") != "true") + " " + names [i])));
			foreach (var parameter in generic)
				text.Append (shapes.Length == 0 && parameter == generic.First () ? "" : ", ").Append (runtime).Append ("JvmType<").Append (parameter.Name).Append (">? ").Append (parameter.Converter).Append (" = null");
			if (shapes.Length > 0 || generic.Length > 0)
				text.Append (", ");
			text.Append ("global::System.Threading.CancellationToken cancellationToken = default, global::Java.Interop.IJavaPeerable? coroutineContext = null)");
			AppendConstraints (text, generic, scope, javaName, nativeName + descriptor, mapped);
			text.AppendLine (" {");
			InitializeConverters (text, generic, scope, javaName, nativeName + descriptor, instance: false);
			text.Append ("\t\treturn ").Append (runtime).Append ("KotlinCoroutines.").Append (unit ? "RunUnitAsync" : "RunAsync").AppendLine (" (__continuation => {");
			for (var i = 0; i < shapes.Length; i++)
				if (IsReference (native [i]))
					text.Append ("\t\t\tusing var __argument").Append (i).Append (" = ").Append (shapes [i].Converter).Append (".CreateArgument (").Append (names [i]).AppendLine (");");
			text.Append ("\t\t\tglobal::Java.Interop.JniArgumentValue* __args = stackalloc global::Java.Interop.JniArgumentValue [").Append (native.Length).AppendLine ("];");
			for (var i = 0; i < shapes.Length; i++)
				text.Append ("\t\t\t__args [").Append (i).Append ("] = ").Append (IsReference (native [i]) ? "__argument" + i + ".Value" :
					"new global::Java.Interop.JniArgumentValue (" + names [i] + ")").AppendLine (";");
			text.Append ("\t\t\t__args [").Append (native.Length - 1).AppendLine ("] = new global::Java.Interop.JniArgumentValue (__continuation.PeerReference);")
				.Append ("\t\t\tvar __result = __members.").Append (isStatic ? "StaticMethods.InvokeObjectMethod" : "InstanceMethods.InvokeVirtualObjectMethod")
				.Append (" (").Append (Literal (nativeName + "." + descriptor)).Append (isStatic ? ", " : ", this.Peer, ").AppendLine ("__args);")
				.AppendLine ("\t\t\treturn global::Java.Lang.Object.GetObject<global::Java.Lang.Object> (__result.Handle, global::Android.Runtime.JniHandleOwnership.TransferLocalRef);")
				.Append ("\t\t}, ");
			if (!unit)
				text.Append (result.Converter).Append (nullable ? ".Read, " : ".ReadRequired, ");
			text.AppendLine ("cancellationToken, coroutineContext);")
				.AppendLine ("\t}");
		}

		static string JavaType (KotlinTypeContract type)
		{
			if (type.TypeParameter != null)
				return type.TypeParameter;
			if (type.Classifier == null)
				return "java.lang.Object";
			var descriptor = KotlinUtilities.ConvertKotlinClassToJava (type.Classifier, convertUnsignedToPrimitive: false);
			var name = TypeNameUtilities.JniSignatureToJavaTypeName (descriptor) ?? "java.lang.Object";
			return name + (type.Arguments.Count == 0 ? "" : "<" + string.Join (", ", type.Arguments.Select (a =>
				a.Projection == "Star" || a.Type == null ? "?" :
				(a.Projection == "In" ? "? super " : a.Projection == "Out" ? "? extends " : "") + JavaType (a.Type))) + ">");
		}

		Shape Project (JavaTypeName type, Dictionary<string, Parameter> scope, string owner, string slot, bool mapped)
		{
			Shape result;
			if (type.DottedName == "?") {
				if (type.BoundsType?.Trim () == "extends" && type.GenericConstraints?.Count == 1)
					return Project (type.GenericConstraints [0], scope, owner, slot, mapped);
				decisions.Add (new GenericProjectionDecision (owner, slot, type.BoundsType ?? "*", "global::Java.Lang.Object", "existential-read-upper-bound"));
				return new Shape ("global::Java.Lang.Object", runtime + "JvmType<global::Java.Lang.Object>.Default");
			}
			var javaName = type.FullNameNonGeneric.Replace ('$', '.');
			if (scope.TryGetValue (javaName, out var parameter))
				result = new Shape (parameter.Name, parameter.Converter);
			else {
				var scalar = ScalarType (javaName);
				if (scalar != null)
					result = new Shape (scalar, runtime + "JvmType<" + scalar + ">.Default");
				else if (type.GenericArguments?.Count > 0 && javaName is "java.util.List" or "java.util.Set" or "java.util.Map") {
					var arguments = type.GenericArguments.Select (a => Project (a, scope, owner, slot, mapped)).ToArray ();
					var collection = javaName.Substring (javaName.LastIndexOf ('.') + 1);
					result = new Shape (runtime + "Kotlin" + collection + Arguments (arguments.Select (a => NullableType (a.Type, true))),
						runtime + "KotlinCoroutines.Get" + collection + "Type (" + string.Join (", ", arguments.Select (a => a.Converter)) + ")");
				} else if (type.GenericArguments?.Count > 0 && views.TryGetValue (javaName, out var view) &&
					type.GenericArguments.All (a => a.DottedName != "?") &&
					TypeParameters (definitions [javaName]).Count () == type.GenericArguments.Count) {
					if (mappedViews.TryGetValue (javaName, out var mappedName) &&
						(mapped || RequiresMappedArguments (javaName, type.GenericArguments.ToArray (), scope))) {
						view = mappedName;
						decisions.Add (new GenericProjectionDecision (owner, slot, Render (type), view,
							mapped ? "mapped-view-conversion" : "mapped-view-required-by-projected-argument"));
					}
					var arguments = type.GenericArguments.Select (a => Project (a, scope, owner, slot, mapped)).ToArray ();
					var name = options.GetOutputName (view) + Arguments (arguments.Select (a => a.Type));
					result = new Shape (name, name + ".GetJvmType (" + string.Join (", ", arguments.Select (a => a.Converter)) + ")");
				} else {
					var symbol = options.SymbolTable.Lookup (javaName);
					var peer = symbol as GenBase ?? (symbol as GenericSymbol)?.Gen;
					if (definitions.ContainsKey (javaName)) {
						if (peers.TryGetValue (javaName, out var localPeer))
							peer = localPeer;
						else if (peer == null || string.IsNullOrEmpty (peer.ReferenceAssemblyName) || peer.IsApiExcluded || !EffectivePublic (peer))
							peer = null;
					}
					var name = peer?.RawVisibility == "public" ? options.GetOutputName (peer.FullName) : "global::Java.Lang.Object";
					result = new Shape (name, runtime + "JvmType<" + name + ">.Default");
					if (type.GenericArguments?.Count > 0 || peer == null || peer.RawVisibility != "public")
						decisions.Add (new GenericProjectionDecision (owner, slot, Render (type), name,
							type.GenericArguments?.Any (a => a.DottedName == "?") == true ? "existential-mutable-contract-retains-raw-peer" :
							type.GenericArguments?.Count > 0 ? "generic-owner-has-no-available-typed-facade" : "unavailable-or-inaccessible-peer"));
				}
			}
			if (!string.IsNullOrEmpty (type.ArrayPart)) {
				var primitive = type.DottedName is "boolean" or "byte" or "char" or "short" or "int" or "long" or "float" or "double";
				foreach (var bracket in type.ArrayPart.Where (c => c == '[' || c == '.').Take (type.ArrayPart == "..." ? 1 : int.MaxValue)) {
					if (primitive) {
						var name = result.Type + "[]";
						result = new Shape (name, runtime + "JvmType<" + name + ">.Default");
						primitive = false;
					} else {
						var name = runtime + "JvmArray<" + result.Type + ">";
						result = new Shape (name, name + ".GetJvmType (" + result.Converter + ")");
					}
				}
			}
			return result;
		}

		IEnumerable<Member> Members (string name, Dictionary<string, string> substitutions, HashSet<string> visited)
		{
			if (!visited.Add (name) || !definitions.TryGetValue (name, out var source))
				yield break;
			foreach (var method in source.Elements ().Where (e => e.Name == "method" || e.Name == "field"))
				yield return new Member (method, substitutions);
			var parents = source.Elements ("implements").Select (i => (string) i.Attribute ("name-generic-aware") ?? (string) i.Attribute ("name"))
				.Concat (new [] { (string) source.Attribute ("extends-generic-aware") ?? (string) source.Attribute ("extends") });
			foreach (var parent in parents.Where (p => !string.IsNullOrEmpty (p))) {
				var parsed = ParseSubstituted (parent, substitutions);
				if (!definitions.TryGetValue (parsed.FullNameNonGeneric, out var definition))
					continue;
				var parameters = TypeParameters (definition).ToArray ();
				var mapping = parameters.Select ((p, i) => (Name: (string) p.Attribute ("name"),
					Value: parsed.GenericArguments != null && i < parsed.GenericArguments.Count ? Render (parsed.GenericArguments [i]) : "java.lang.Object"))
					.ToDictionary (p => p.Name, p => p.Value, StringComparer.Ordinal);
				foreach (var member in Members (parsed.FullNameNonGeneric, mapping, visited))
					yield return member;
			}
		}

		IEnumerable<Parameter> AllocateParameters (IEnumerable<XElement> parameters, HashSet<string> occupied)
		{
			foreach (var parameter in parameters) {
				var javaName = (string) parameter.Attribute ("name");
				var name = Identifier (javaName);
				while (!occupied.Add (name.TrimStart ('@')))
					name += "Type";
				yield return new Parameter (javaName, name, "__type_" + name.TrimStart ('@'),
					(parameter.Element ("genericConstraints")?.Elements ("genericConstraint").Select (c => (string) c.Attribute ("type")) ?? [])
					.Concat (new [] { (string) parameter.Attribute ("classBound") })
					.Concat (((string) parameter.Attribute ("interfaceBounds") ?? "").Split (':'))
					.Where (b => !string.IsNullOrEmpty (b)).Distinct (StringComparer.Ordinal).ToArray ());
			}
		}

		IEnumerable<GenBase> ConstraintPeers (Parameter parameter, Dictionary<string, Parameter> scope, string owner = null, string slot = null)
		{
			if (!options.ExperimentalGenericFacades)
				yield break;
			foreach (var bound in parameter.Bounds.Where (b => b != "java.lang.Object" && b != "kotlin.Any")) {
				var parsed = JavaTypeName.Parse (bound);
				var peer = options.SymbolTable.Lookup (parsed.FullNameNonGeneric) as GenBase;
				var reason = scope.ContainsKey (parsed.DottedName) ? "runtime-only-type-variable-bound" :
					parsed.GenericArguments?.Count > 0 || parsed.GenericParent != null ? "runtime-only-parameterized-bound" :
					!string.IsNullOrEmpty (parsed.ArrayPart) ? "runtime-only-array-bound" :
					ScalarType (parsed.FullNameNonGeneric) != null || parsed.FullNameNonGeneric is "java.lang.CharSequence" or "java.lang.Number" or
						"java.util.Collection" or "java.util.List" or "java.util.Set" or "java.util.Map" ? "runtime-only-converted-bound" :
					!JavaProjectionContract.IsAvailable (peer, options) ? "runtime-only-unavailable-peer-projection" :
					"clr-projection-bound";
				if (owner != null)
					decisions.Add (new GenericProjectionDecision (owner, slot, bound,
						reason == "clr-projection-bound" ? JavaProjectionContract.Name (options.GetOutputName (peer.FullName)) : parameter.Name, reason));
				if (reason == "clr-projection-bound")
					yield return peer;
			}
		}

		void AppendConstraints (StringBuilder text, IEnumerable<Parameter> parameters, Dictionary<string, Parameter> scope,
			string owner, string slot, bool mapped)
		{
			foreach (var parameter in parameters) {
				var constraints = ConstraintPeers (parameter, scope, mapped ? null : owner, slot)
					.Select (p => JavaProjectionContract.Name (options.GetOutputName (p.FullName))).Distinct (StringComparer.Ordinal).ToArray ();
				if (mapped) {
					if (constraints.Length > 0)
						decisions.Add (new GenericProjectionDecision (owner, slot, string.Join (" & ", parameter.Bounds),
							parameter.Name, "mapped-view-retains-runtime-bounds"));
				} else if (constraints.Length > 0)
					text.Append (" where ").Append (parameter.Name).Append (" : ").Append (string.Join (", ", constraints));
			}
		}

		static void AppendBoundDocumentation (StringBuilder text, IEnumerable<Parameter> parameters, bool mapped)
		{
			foreach (var parameter in parameters) {
				var bounds = parameter.Bounds.Where (b => b != "java.lang.Object" && b != "kotlin.Any").ToArray ();
				if (bounds.Length == 0)
					continue;
				var documentation = new XElement ("typeparam", new XAttribute ("name", parameter.Name.TrimStart ('@')),
					"Native upper bounds: " + string.Join (" & ", bounds) + ". " +
					(mapped ? "Custom managed representations are allowed; native assignability is checked at runtime." :
						"Compatible canonical peers and views satisfy available C# projection constraints; native assignability is also checked at runtime."));
				text.Append ("\t/// ").AppendLine (documentation.ToString (SaveOptions.DisableFormatting));
			}
		}

		bool RequiresMappedArguments (string javaName, JavaTypeName[] arguments, Dictionary<string, Parameter> scope)
		{
			var parameters = AllocateParameters (TypeParameters (definitions [javaName]), new HashSet<string> (StringComparer.Ordinal)).ToArray ();
			var targetScope = parameters.ToDictionary (p => p.JavaName, StringComparer.Ordinal);
			for (var i = 0; i < parameters.Length; i++) {
				var required = ConstraintPeers (parameters [i], targetScope).ToArray ();
				if (required.Length == 0)
					continue;
				var argument = arguments [i];
				var actual = scope.TryGetValue (argument.FullNameNonGeneric, out var variable)
					? ConstraintPeers (variable, scope).ToArray ()
					: string.IsNullOrEmpty (argument.ArrayPart) && options.SymbolTable.Lookup (argument.FullNameNonGeneric) is GenBase peer &&
						JavaProjectionContract.IsAvailable (peer, options) ? new [] { peer } : [];
				if (required.Any (bound => !actual.Any (candidate => IsClrAssignableTo (candidate, bound))))
					return true;
			}
			return false;
		}

		static bool IsClrAssignableTo (GenBase candidate, GenBase bound)
		{
			var visited = new HashSet<GenBase> ();
			var pending = new Stack<GenBase> ();
			pending.Push (candidate);
			while (pending.Count > 0) {
				var current = pending.Pop ();
				if (!visited.Add (current))
					continue;
				if (current.FullName == bound.FullName && current.ReferenceAssemblyName == bound.ReferenceAssemblyName)
					return true;
				if (current.BaseGen != null)
					pending.Push (current.BaseGen);
				foreach (var symbol in current.Interfaces)
					if ((symbol as GenBase ?? (symbol as GenericSymbol)?.Gen) is GenBase parent)
						pending.Push (parent);
			}
			return false;
		}

		void InitializeConverters (StringBuilder text, IEnumerable<Parameter> parameters, Dictionary<string, Parameter> scope, string owner, string slot, bool instance)
		{
			foreach (var parameter in parameters) {
				text.Append ("\t\t").Append (instance ? "this." + parameter.Converter + " = " : parameter.Converter + " ??= ")
					.Append (instance ? parameter.Converter + " ?? " : "").Append (runtime).Append ("JvmType<").Append (parameter.Name).AppendLine (">.Default;");
			}
			foreach (var parameter in parameters) {
				foreach (var bound in parameter.Bounds.Where (b => b != "java.lang.Object" && b != "kotlin.Any")) {
					var parsed = JavaTypeName.Parse (bound);
					var value = scope.TryGetValue (parsed.DottedName, out var constraint) ? (instance ? "this." : "") + constraint.Converter + ".NativeName" :
						Literal ((options.SymbolTable.Lookup (parsed.FullNameNonGeneric) is GenBase peer ? peer.RawJniName : parsed.FullNameNonGeneric.Replace ('.', '/')));
					text.Append ("\t\t").Append (instance ? "this." : "").Append (parameter.Converter).Append (".RequireAssignableTo (").Append (value).AppendLine (");");
					decisions.Add (new GenericProjectionDecision (owner, slot, bound, parameter.Name, "runtime-validated-erased-java-bound"));
				}
			}
		}

		static void AppendConverterParameters (StringBuilder text, IEnumerable<Parameter> parameters)
		{
			foreach (var parameter in parameters)
				text.Append (", ").Append (runtime).Append ("JvmType<").Append (parameter.Name).Append (">? ").Append (parameter.Converter).Append (" = null");
		}

		static void AppendAttributes (StringBuilder text, IEnumerable<AttributeWriter> attributes)
		{
			using var buffer = new StringWriter ();
			var writer = new CodeWriter (buffer);
			foreach (var attribute in attributes)
				attribute.WriteAttribute (writer);
			using var lines = new StringReader (buffer.ToString ());
			for (var line = lines.ReadLine (); line != null; line = lines.ReadLine ())
				text.Append ('\t').AppendLine (line);
		}

		static IEnumerable<XElement> TypeParameters (XElement element) => element.Element ("typeParameters")?.Elements ("typeParameter") ?? [];
		static string ScalarType (string javaName) => javaName switch {
			"void" => "void", "boolean" => "bool", "byte" => "sbyte", "char" => "char", "short" => "short",
			"int" => "int", "long" => "long", "float" => "float", "double" => "double",
			"ubyte" => "byte", "ushort" => "ushort", "uint" => "uint", "ulong" => "ulong",
			"java.lang.String" => "string", "java.lang.Boolean" => "bool?", "java.lang.Byte" => "sbyte?",
			"java.lang.Character" => "char?", "java.lang.Short" => "short?", "java.lang.Integer" => "int?",
			"java.lang.Long" => "long?", "java.lang.Float" => "float?", "java.lang.Double" => "double?",
			_ => null,
		};
		static string Arguments (IEnumerable<string> values) => values.Any () ? "<" + string.Join (", ", values) + ">" : "";
		static string NullableType (string type, bool nullable) => nullable && type != "void" && !type.EndsWith ("?", StringComparison.Ordinal) ? type + "?" : type;
		static bool IsReference (string descriptor) => descriptor.StartsWith ("L", StringComparison.Ordinal) || descriptor.StartsWith ("[", StringComparison.Ordinal);
		static string NativeOperation (string descriptor) => descriptor switch {
			"V" => "Void", "Z" => "Boolean", "B" => "SByte", "C" => "Char", "S" => "Int16",
			"I" => "Int32", "J" => "Int64", "F" => "Single", "D" => "Double", _ => "Object",
		};
		static string NativePrimitive (string descriptor) => descriptor switch {
			"B" => "sbyte", "S" => "short", "I" => "int", "J" => "long",
			_ => throw new InvalidDataException ($"No unsigned generic argument conversion for '{descriptor}'."),
		};
		static string Literal (string value) => JsonSerializer.Serialize (value);
		static string Identifier (string value, bool capitalize = false)
		{
			var result = new string (value.Select (c => char.IsLetterOrDigit (c) || c == '_' ? c : '_').ToArray ());
			if (result.Length == 0 || char.IsDigit (result [0]))
				result = "_" + result;
			if (capitalize)
				result = char.ToUpperInvariant (result [0]) + result.Substring (1);
			return "@" + result;
		}
		static string SignatureType (string type, Parameter[] parameters)
		{
			for (var i = 0; i < parameters.Length; i++)
				type = Regex.Replace (type, Regex.Escape (parameters [i].Name) + @"(?![\w])", "!!" + i);
			return type;
		}

		static Dictionary<string, string> MethodSubstitutions (Member member)
		{
			var local = new HashSet<string> (TypeParameters (member.Node).Select (p => (string) p.Attribute ("name")), StringComparer.Ordinal);
			return member.Substitutions.Where (p => !local.Contains (p.Key)).ToDictionary (p => p.Key, p => p.Value, StringComparer.Ordinal);
		}
		static string Substitute (string value, Dictionary<string, string> substitutions)
		{
			try {
				return Render (JavaTypeName.Parse (value), substitutions);
			} catch (ArgumentException error) {
				throw new InvalidDataException ($"Cannot parse native generic type '{value}'.", error);
			}
		}
		static JavaTypeName ParseSubstituted (string value, Dictionary<string, string> substitutions)
		{
			var substituted = Substitute (value, substitutions);
			try {
				return JavaTypeName.Parse (substituted);
			} catch (ArgumentException error) {
				throw new InvalidDataException ($"Cannot parse substituted generic type '{substituted}' from '{value}'.", error);
			}
		}
		static string Render (JavaTypeName type, Dictionary<string, string> substitutions = null)
		{
			var name = substitutions != null && substitutions.TryGetValue (type.DottedName, out var replacement) ? replacement : type.DottedName;
			return (type.GenericParent == null ? "" : Render (type.GenericParent, substitutions) + ".") + name +
				(type.GenericArguments == null ? "" : "<" + string.Join (", ", type.GenericArguments.Select (a => Render (a, substitutions))) + ">") +
				(type.GenericConstraints == null ? "" : type.BoundsType + string.Join (" & ", type.GenericConstraints.Select (c => Render (c, substitutions)))) +
				type.ArrayPart;
		}
		static bool EffectivePublic (GenBase type)
		{
			for (var current = type; current != null; current = current.EnclosingType)
				if (current.RawVisibility != "public")
					return false;
			return true;
		}

		static IEnumerable<GenBase> Flatten (IEnumerable<GenBase> types)
		{
			foreach (var type in types) {
				yield return type;
				foreach (var nested in Flatten (type.NestedTypes))
					yield return nested;
			}
		}
	}
}
