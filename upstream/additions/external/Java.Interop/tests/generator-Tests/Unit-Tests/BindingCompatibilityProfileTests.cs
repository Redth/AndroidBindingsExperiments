#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Java.Interop.Tools.Generator.Semantics;
using Mono.Cecil;
using Mono.Cecil.Cil;
using NUnit.Framework;

namespace generatortests;

[TestFixture]
public class BindingCompatibilityProfileTests
{
	static AssemblyDefinition Assembly (string name = "Xamarin.AndroidX.Compat") =>
		AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition (name, new Version (1, 0, 0, 0)), name + ".dll", ModuleKind.Dll);

	static TypeDefinition Peer (AssemblyDefinition assembly, string name = "Peer")
	{
		var type = new TypeDefinition ("Legacy", name, TypeAttributes.Public);
		assembly.MainModule.Types.Add (type);
		Register (type, assembly.MainModule, "example/" + name);
		return type;
	}

	static MethodDefinition Method (TypeDefinition type, string name = "Call", MethodAttributes flags = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot)
	{
		var method = new MethodDefinition (name, flags, type.Module.TypeSystem.Void);
		type.Methods.Add (method);
		Register (method, type.Module, name.ToLowerInvariant (), "()V");
		return method;
	}

	static void Register (ICustomAttributeProvider provider, ModuleDefinition module, string name, string? signature = null)
	{
		var attrType = new TypeReference ("Android.Runtime", "RegisterAttribute", module, module.TypeSystem.Object.Scope);
		var ctor = new MethodReference (".ctor", module.TypeSystem.Void, attrType) { HasThis = true };
		ctor.Parameters.Add (new ParameterDefinition (module.TypeSystem.String));
		if (signature != null)
			ctor.Parameters.Add (new ParameterDefinition (module.TypeSystem.String));
		var attr = new CustomAttribute (ctor);
		attr.ConstructorArguments.Add (new CustomAttributeArgument (module.TypeSystem.String, name));
		if (signature != null)
			attr.ConstructorArguments.Add (new CustomAttributeArgument (module.TypeSystem.String, signature));
		provider.CustomAttributes.Add (attr);
	}

	static BindingCompatibilityProfile Profile (TypeDefinition type) => new (1, [], [], [BindingCompatibilityProfile.Describe (type)]);

	[Test]
	public void ConcreteAdditionsAreAllowed ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		Method (type);
		var profile = Profile (type);
		Method (type, "Additional");
		Assert.IsEmpty (profile.Validate (assembly));
	}

	[Test]
	public void AddingAbstractMemberIsNotACompatibleAddition ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		var profile = Profile (type);
		Method (type, "NewObligation", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.NewSlot);
		StringAssert.Contains ("abstract obligation", string.Join ("\n", profile.Validate (assembly)));
	}

	[Test]
	public void MissingMemberCannotBeHiddenByMatchingTypeNames ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		var method = Method (type);
		var profile = Profile (type);
		type.Methods.Remove (method);
		StringAssert.Contains ("Missing protected method", string.Join ("\n", profile.Validate (assembly)));
	}

	[Test]
	public void OwnerIdentityIsNotJustAssemblyFileName ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		var profile = Profile (type);
		assembly.Name.Version = new Version (2, 0, 0, 0);
		StringAssert.Contains ("identity changed", string.Join ("\n", profile.Validate (assembly)));
	}

	[Test]
	public void JniSlotChangesAreRejected ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		var method = Method (type);
		var profile = Profile (type);
		method.CustomAttributes.Clear ();
		Register (method, type.Module, "renamed", "()V");
		StringAssert.Contains ("JNI contract changed", string.Join ("\n", profile.Validate (assembly)));
	}

	[Test]
	public void VirtualSlotChangesAreRejected ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		var method = Method (type);
		var profile = Profile (type);
		method.IsNewSlot = false;
		StringAssert.Contains ("flags/JNI contract changed", string.Join ("\n", profile.Validate (assembly)));
	}

	[Test]
	public void GenericConstraintChangesAreRejected ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		var parameter = new GenericParameter ("T", type);
		type.GenericParameters.Add (parameter);
		var profile = Profile (type);
		parameter.Attributes = GenericParameterAttributes.ReferenceTypeConstraint;
		StringAssert.Contains ("generic contract changed", string.Join ("\n", profile.Validate (assembly)));
	}

	[Test]
	public void IdentityOnlyDoesNotRequireConstructionOrSubclassSurface ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		Method (type);
		var profile = Profile (type) with { Types = [BindingCompatibilityProfile.Describe (type) with { Preservation = "identity", Members = [] }] };
		type.Methods.Clear ();
		type.IsSealed = true;
		Assert.IsEmpty (profile.Validate (assembly));
		type.IsPublic = false;
		Assert.IsNotEmpty (profile.Validate (assembly), "Identity-only still requires a public usable type.");
	}

	[Test]
	public void ProfileRestoresNamesButDoesNotDeleteNativeAdditions ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		Method (type);
		var profile = Profile (type);
		var api = XDocument.Parse ("""
			<api><package name="example"><class name="Peer" jni-signature="Lexample/Peer;">
			  <method name="call" jni-signature="()V"/><method name="newApi" jni-signature="()V"/>
			</class></package></api>
			""");
		profile.Apply (api, assembly.Name.Name);
		var package = api.Root?.Element ("package");
		Assert.AreEqual ("Legacy", (string?) package?.Attribute ("managedName"));
		Assert.AreEqual ("Call", (string?) package?.Element ("class")?.Elements ("method").First ().Attribute ("managedName"));
		Assert.AreEqual (2, package?.Element ("class")?.Elements ("method").Count ());
	}

	[Test]
	public void MissingNativeSlotFailsBeforeEmission ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		Method (type);
		var api = XDocument.Parse ("""<api><package name="example"><class name="Peer" jni-signature="Lexample/Peer;"/></package></api>""");
		var error = Assert.Throws<InvalidDataException> (() => Profile (type).Apply (api, assembly.Name.Name));
		StringAssert.Contains ("JNI slot is missing", error?.Message);
	}

	[Test]
	public void ProtectedPeerCannotMoveToAnotherAssemblyDuringGeneration ()
	{
		using var assembly = Assembly ();
		var type = Peer (assembly);
		var api = XDocument.Parse ("""<api><package name="example"><class name="Peer" jni-signature="Lexample/Peer;"/></package></api>""");
		var error = Assert.Throws<InvalidDataException> (() => Profile (type).Apply (api, "Different.Owner"));
		StringAssert.Contains ("Protected owner cannot change", error?.Message);
	}

	[Test]
	public void SignatureOnlyUsageDoesNotInventSubclassRequirements ()
	{
		WithFixture ((directory, binding, consumer, type, holder) => {
			Method (type);
			holder.Fields.Add (new FieldDefinition ("Value", FieldAttributes.Public, consumer.MainModule.ImportReference (type)));
			binding.Write (Path.Combine (directory, "Xamarin.AndroidX.Compat.dll"));
			consumer.Write (Path.Combine (directory, "Consumer.dll"));
			var full = BindingCompatibilityProfile.Extract ([Path.Combine (directory, "Consumer.dll")], directory);
			var selected = BindingCompatibilityUsage.Select (full, [Path.Combine (directory, "Consumer.dll")], directory);
			Assert.AreEqual ("identity", selected.Profile.Types.Single ().Preservation);
			Assert.IsEmpty (selected.Profile.Types.Single ().Members);
			Assert.IsTrue (selected.Evidence.Any (e => e.Use == "public-signature"));
			Assert.IsFalse (selected.Evidence.Any (e => e.Use == "subclass"));
		});
	}

	[Test]
	public void ObservedCallAndSubclassAreRecordedSeparately ()
	{
		WithFixture ((directory, binding, consumer, type, holder) => {
			var called = Method (type);
			Method (type, "UnusedLegacyOverload");
			holder.BaseType = consumer.MainModule.ImportReference (type);
			var method = new MethodDefinition ("Use", MethodAttributes.Public | MethodAttributes.Static, consumer.MainModule.TypeSystem.Void);
			method.Body.Instructions.Add (Instruction.Create (OpCodes.Callvirt, consumer.MainModule.ImportReference (called)));
			method.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
			holder.Methods.Add (method);
			binding.Write (Path.Combine (directory, "Xamarin.AndroidX.Compat.dll"));
			consumer.Write (Path.Combine (directory, "Consumer.dll"));
			var full = BindingCompatibilityProfile.Extract ([Path.Combine (directory, "Consumer.dll")], directory);
			var selected = BindingCompatibilityUsage.Select (full, [Path.Combine (directory, "Consumer.dll")], directory);
			Assert.IsTrue (selected.Evidence.Any (e => e.Use == "subclass"));
			Assert.IsTrue (selected.Evidence.Any (e => e.Use == "member-call"));
			Assert.AreEqual ("Call", selected.Profile.Types.Single ().Members.Single ().Name);
			Assert.IsFalse (selected.Profile.Types.Single ().Members.Any (m => m.Name == "UnusedLegacyOverload"));
		});
	}

	[Test]
	public void SubclassSelectionIncludesImplicitLegacyDispatchInfrastructure ()
	{
		WithFixture ((directory, binding, consumer, type, holder) => {
			type.Methods.Add (new MethodDefinition ("get_ThresholdType",
				MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.SpecialName,
				binding.MainModule.TypeSystem.Object));
			holder.BaseType = consumer.MainModule.ImportReference (type);
			binding.Write (Path.Combine (directory, "Xamarin.AndroidX.Compat.dll"));
			consumer.Write (Path.Combine (directory, "Consumer.dll"));
			var full = BindingCompatibilityProfile.Extract ([Path.Combine (directory, "Consumer.dll")], directory);
			var selected = BindingCompatibilityUsage.Select (full, [Path.Combine (directory, "Consumer.dll")], directory);
			Assert.IsTrue (selected.Profile.Types.Single ().Members.Any (m => m.Name == "get_ThresholdType"));
			Assert.IsTrue (selected.Evidence.Any (e => e.Use == "legacy-interop-dispatch"));
		});
	}

	static void WithFixture (Action<string, AssemblyDefinition, AssemblyDefinition, TypeDefinition, TypeDefinition> action)
	{
		string directory = Path.Combine (TestContext.CurrentContext.WorkDirectory, "compat-profile-" + Guid.NewGuid ().ToString ("N"));
		Directory.CreateDirectory (directory);
		try {
			using var binding = Assembly ();
			using var consumer = Assembly ("Consumer");
			var type = Peer (binding);
			var holder = new TypeDefinition ("Consumer", "Holder", TypeAttributes.Public);
			consumer.MainModule.Types.Add (holder);
			action (directory, binding, consumer, type, holder);
		} finally {
			Directory.Delete (directory, true);
		}
	}
}
