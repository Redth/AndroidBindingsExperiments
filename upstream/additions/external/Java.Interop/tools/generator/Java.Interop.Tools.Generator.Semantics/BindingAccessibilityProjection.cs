using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace Java.Interop.Tools.Generator.Semantics
{
	public sealed record BindingAccessibilityDecision (
		string DeclaringType, string NativeSlot, string Classification,
		string SourceVisibility, string ManagedVisibility, string Reason);

	public sealed class BindingAccessibilityProjection
	{
		readonly Dictionary<string, XElement> types;
		readonly Dictionary<(string Type, string Slot), BindingAccessibilityDecision> decisions;
		readonly HashSet<(string Type, string Slot)> referencedContracts;
		readonly HashSet<(string Type, string Slot)> protectedContracts;

		public IReadOnlyList<BindingAccessibilityDecision> Decisions { get; }

		public BindingAccessibilityProjection (XDocument facts, IEnumerable<(string Type, string Slot)> referencedContracts = null,
			IEnumerable<(string Type, string Slot)> protectedContracts = null)
		{
			if (facts == null)
				throw new ArgumentNullException (nameof (facts));
			this.referencedContracts = referencedContracts == null ? new HashSet<(string Type, string Slot)> () :
				new HashSet<(string Type, string Slot)> (referencedContracts);
			this.protectedContracts = protectedContracts == null ? new HashSet<(string Type, string Slot)> () :
				new HashSet<(string Type, string Slot)> (protectedContracts);
			types = facts.Root.Elements ("package").SelectMany (p => p.Elements ()
				.Where (t => t.Name == "class" || t.Name == "interface")
				.Select (t => (Name: BindingTypeDefinition.GetJavaName (p, t), Type: t)))
				.ToDictionary (p => p.Name, p => p.Type, StringComparer.Ordinal);
			decisions = new Dictionary<(string Type, string Slot), BindingAccessibilityDecision> ();
			foreach (var pair in types.OrderBy (p => p.Key, StringComparer.Ordinal)) {
				var type = ClassifyType (pair.Key, pair.Value);
				decisions.Add ((pair.Key, ""), type);
				foreach (var member in pair.Value.Elements ().Where (e => e.Name == "method" || e.Name == "constructor" || e.Name == "field"))
					decisions.Add ((pair.Key, Slot (member)), ClassifyMember (pair.Key, pair.Value, member, type));
			}
			Decisions = decisions.Values.OrderBy (d => d.DeclaringType, StringComparer.Ordinal)
				.ThenBy (d => d.NativeSlot, StringComparer.Ordinal).ToList ().AsReadOnly ();
		}

		public void Apply (XDocument lowered)
		{
			foreach (var package in lowered.Root.Elements ("package")) {
				foreach (var type in package.Elements ().Where (t => t.Name == "class" || t.Name == "interface")) {
					var name = BindingTypeDefinition.GetJavaName (package, type);
					Apply (type, decisions [(name, "")]);
					foreach (var member in type.Elements ().Where (e => e.Name == "method" || e.Name == "constructor" || e.Name == "field"))
						if (decisions.TryGetValue ((name, Slot (member)), out var decision))
							Apply (member, decision);
				}
			}
		}

		static void Apply (XElement element, BindingAccessibilityDecision decision)
		{
			if (element.Attribute ("api-source-visibility") == null)
				element.SetAttributeValue ("api-source-visibility", decision.SourceVisibility);
			element.SetAttributeValue ("visibility", decision.ManagedVisibility);
			element.SetAttributeValue ("api-classification", decision.Classification);
			element.SetAttributeValue ("api-classification-reason", decision.Reason);
			if (decision.Classification == "Infrastructure" && decision.ManagedVisibility != "private" &&
				(element.Name == "method" || element.Name == "class" || element.Name == "interface")) {
				var attributes = (string) element.Attribute ("customAttributes") ?? "";
				const string hidden = BindingPublisherPolicy.HiddenAttribute;
				if (!attributes.Contains (hidden)) {
					element.SetAttributeValue ("customAttributes", attributes + hidden);
					if (decision.Reason.StartsWith ("google-", StringComparison.Ordinal))
						element.SetAttributeValue ("api-publisher-hidden-attribute", "true");
				}
			}
		}

		BindingAccessibilityDecision ClassifyType (string name, XElement type)
		{
			var visibility = (string) type.Attribute ("visibility") ?? "";
			var restriction = Restriction (type);
			if (visibility != "public" && visibility != "protected") {
				return Decision (name, "", visibility, visibility == "" || visibility == "kotlin-internal" ? "private" : visibility, "Excluded",
					(string) type.Attribute ("jvm-visibility") == "public" ? "kotlin-source-internal-peer" : "non-public-source-type");
			}
			var owner = GetEnclosingType (name);
			if (owner != null) {
				var enclosing = ClassifyType (owner, types [owner]);
				if (enclosing.Classification == "Excluded")
					return Decision (name, "", visibility, "private", "Excluded", "non-public-enclosing-type");
				if (enclosing.Classification == "Infrastructure")
					return Decision (name, "", visibility, "internal", "Infrastructure", "non-public-enclosing-type");
			}
			if (restriction == "restricted")
				return Decision (name, "", visibility, "internal", "Infrastructure", RestrictionReason (type));
			var publisher = BindingPublisherPolicy.InfrastructureReason (name, type);
			if (publisher.Length > 0)
				return Decision (name, "", visibility, "internal", "Infrastructure", publisher);
			if (restriction == "protected")
				return Decision (name, "", visibility, owner == null ? visibility : "protected", "Protected", RestrictionReason (type));
			if ((string) type.Attribute ("kotlin-metadata-status") == "unsupported" || restriction == "unknown")
				return Decision (name, "", visibility, visibility, "Unverified", "unclassified-source-contract");
			return Decision (name, "", visibility, visibility, visibility == "protected" ? "Protected" : "Public", "supported-source-api");
		}

		BindingAccessibilityDecision ClassifyMember (string name, XElement type, XElement member, BindingAccessibilityDecision owner)
		{
			var slot = Slot (member);
			var visibility = (string) member.Attribute ("visibility") ?? "";
			if (visibility != "public" && visibility != "protected")
				return Decision (name, slot, visibility, visibility, "Excluded", "non-public-source-member");
			var restriction = Restriction (member);
			if (owner.Classification == "Excluded")
				return Decision (name, slot, visibility, visibility, "Excluded", "non-public-declaring-type");
			if (owner.Classification == "Infrastructure")
				return Decision (name, slot, visibility, visibility, "Infrastructure", "restricted-declaring-type");
			var publisher = BindingPublisherPolicy.InfrastructureReason (name, member);
			if (restriction == "restricted" || publisher.Length > 0) {
				var is_method = member.Name == "method";
				var is_static = (string) member.Attribute ("static") == "true";
				if (is_method && !is_static && RequiresPublicContract (name, member, new HashSet<string> (StringComparer.Ordinal)))
					return Decision (name, slot, visibility, "public", "Infrastructure", "required-public-inherited-contract");
				if (is_method && !is_static && RequiresPublicContract (name, member, new HashSet<string> (StringComparer.Ordinal), protectedContract: true))
					return Decision (name, slot, visibility, "protected", "Infrastructure", "required-protected-inherited-contract");
				var implementation_contract = is_method && !is_static &&
					(type.Name == "interface" || (string) member.Attribute ("final") != "true");
				return Decision (name, slot, visibility,
					implementation_contract ? type.Name == "interface" ? "public" : "protected" : "private",
					"Infrastructure", implementation_contract ? "required-native-virtual-contract" :
						restriction == "restricted" ? RestrictionReason (member) : publisher);
			}
			if (restriction == "protected") {
				var inherited_public = member.Name == "method" &&
					RequiresPublicContract (name, member, new HashSet<string> (StringComparer.Ordinal));
				return Decision (name, slot, visibility, inherited_public ? "public" : "protected", "Protected",
					inherited_public ? "inherited-public-contract-cannot-be-narrowed" : RestrictionReason (member));
			}
			if (restriction == "unknown" || owner.Classification == "Unverified")
				return Decision (name, slot, visibility, visibility, "Unverified", "unclassified-source-contract");
			return Decision (name, slot, visibility, visibility, visibility == "protected" ? "Protected" : "Public",
				(string) member.Attribute ("synthetic") == "true" ? "retained-native-bridge" : "supported-source-api");
		}

		bool RequiresPublicContract (string name, XElement method, HashSet<string> visited, bool protectedContract = false)
		{
			if ((protectedContract ? protectedContracts : referencedContracts).Contains ((name, Slot (method))))
				return true;
			if (!visited.Add (name) || !types.TryGetValue (name, out var type))
				return false;
			var parents = type.Elements ("implements").Select (e => (string) e.Attribute ("name"))
				.Concat (new [] { (string) type.Attribute ("extends") });
			foreach (var raw in parents.Where (p => !string.IsNullOrEmpty (p))) {
				var parent_name = raw.Split ('<') [0];
				if (!types.TryGetValue (parent_name, out var parent))
					continue;
				if (ClassifyType (parent_name, parent).Classification is "Public" or "Protected" or "Unverified" &&
					parent.Elements ("method").Any (m => (protectedContract ?
							(string) m.Attribute ("visibility") == "protected" || (string) m.Attribute ("visibility") == "public" && Restriction (m) == "protected" :
							(string) m.Attribute ("visibility") == "public" && Restriction (m) != "protected" && Restriction (m) != "restricted") &&
						(string) m.Attribute ("static") != "true" &&
						(string) m.Attribute ("name") == (string) method.Attribute ("name") &&
						ParameterDescriptor (m) == ParameterDescriptor (method)))
					return true;
				if (RequiresPublicContract (parent_name, method, visited, protectedContract))
					return true;
			}
			return false;
		}

		string GetEnclosingType (string name)
		{
			var jni = (string) types [name].Attribute ("jni-signature");
			if (jni != null && !jni.Contains ('$'))
				return null;
			for (var dot = name.LastIndexOf ('.'); dot >= 0; dot = name.LastIndexOf ('.', dot - 1)) {
				var candidate = name.Substring (0, dot);
				if (types.ContainsKey (candidate))
					return candidate;
				if (dot == 0)
					break;
			}
			return null;
		}

		static string Restriction (XElement element)
		{
			if ((string) element.Attribute ("api-internal") == "true")
				return "restricted";
			var testing = (string) element.Attribute ("api-visible-for-testing");
			if (testing == "protected")
				return "protected";
			if (testing == "private" || testing == "package" || testing == "none")
				return "restricted";
			if (testing != null)
				return "unknown";
			var scopes = ((string) element.Attribute ("annotated-visibility") ?? "")
				.Split (new [] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
			if (scopes.Any (s => s == "LIBRARY" || s == "LIBRARY_GROUP" || s == "LIBRARY_GROUP_PREFIX" ||
				s == "GROUP_ID" || s == "TESTS"))
				return "restricted";
			if (scopes.Contains ("SUBCLASSES"))
				return "protected";
			return scopes.Any (s => s != "module-info") ? "unknown" : "";
		}

		static string RestrictionReason (XElement element) =>
			(string) element.Attribute ("api-internal") == "true" ? "annotated-internal-api" :
			element.Attribute ("api-visible-for-testing") != null ? "visible-for-testing-intended-access" : "androidx-restrict-to-scope";

		static string Slot (XElement member) =>
			(member.Name == "constructor" ? "<init>" : (string) member.Attribute ("name") ?? "") +
			(member.Name == "field" ? ":" : "") + ((string) member.Attribute ("jni-signature") ?? "");

		static string ParameterDescriptor (XElement method)
		{
			var descriptor = (string) method.Attribute ("jni-signature") ?? "";
			var end = descriptor.IndexOf (')');
			return end < 0 ? descriptor : descriptor.Substring (0, end + 1);
		}

		static BindingAccessibilityDecision Decision (string type, string slot, string source, string managed, string classification, string reason) =>
			new BindingAccessibilityDecision (type, slot, classification, source, managed, reason);
	}
}
