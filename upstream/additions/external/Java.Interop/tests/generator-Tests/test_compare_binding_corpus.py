import importlib
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile
import io


corpus = importlib.import_module("compare-binding-corpus")
closure = importlib.import_module("compare-binding-closure")


class CorpusTests(unittest.TestCase):
    def test_maven_repository_selection_honors_the_config_and_ci_mirror(self):
        artifact = {"groupId": "org.example", "artifactId": "library", "version": "1"}
        with patch.dict(corpus.os.environ, {"RUNNINGONCI": ""}):
            google = corpus.maven_base(artifact)
            central = corpus.maven_base(dict(artifact, mavenRepositoryType="MavenCentral"))
            self.assertNotEqual(google, central)
            self.assertTrue(central.endswith("/org/example/library/1/library-1"))
        with patch.dict(corpus.os.environ, {"RUNNINGONCI": "true"}):
            self.assertEqual(corpus.maven_base(artifact), corpus.maven_base(dict(artifact, mavenRepositoryType="MavenCentral")))
            self.assertIn("dotnet-public-maven", corpus.maven_base(artifact))
        with self.assertRaisesRegex(corpus.CorpusInputError, "Unknown Maven repository"):
            corpus.maven_base(dict(artifact, mavenRepositoryType="unknown"))

    def test_published_relocations_preserve_original_coordinates_and_default_unchanged_parts(self):
        artifact = {"groupId": "old.group", "artifactId": "library", "version": "1"}
        pom = ET.fromstring("""
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <distributionManagement><relocation><groupId>new.group</groupId><version>2</version></relocation></distributionManagement>
            </project>
            """)
        self.assertEqual({"groupId": "new.group", "artifactId": "library", "version": "2"},
                         corpus.published_relocation(artifact, pom))
        self.assertEqual("old.group", artifact["groupId"])
        self.assertIsNone(corpus.published_relocation(artifact, ET.fromstring("<project />")))
        with self.assertRaisesRegex(corpus.CorpusInputError, "Self-referential"):
            corpus.published_relocation(artifact, ET.fromstring(
                "<project><distributionManagement><relocation /></distributionManagement></project>"))

    def test_closure_excludes_published_android_bindings_before_loading_support(self):
        assets = {
            "targets": {"net11.0-android37.0": {
                "Xamarin.AndroidX.Core/1.0": {"compile": {"lib/Core.dll": {}}},
                "Xamarin.GooglePlayServices.Base/1.0": {"compile": {"lib/Base.dll": {}}},
            }},
            "libraries": {}, "packageFolders": {},
        }
        self.assertEqual([], closure.support_references(assets, "net11.0-android"))

    def test_closure_does_not_import_another_target_framework(self):
        assets = {
            "targets": {
                "net10.0-android36.0": {"Unavailable/1.0": {"compile": {"lib/Unavailable.dll": {}}}},
                "net11.0-android37.0": {},
            },
            "libraries": {}, "packageFolders": {},
        }
        self.assertEqual([], closure.support_references(assets, "net11.0-android"))
        with self.assertRaisesRegex(corpus.CorpusInputError, "No support-reference assets"):
            closure.support_references(assets, "net12.0-android")

    def test_closure_requires_support_files_and_rejects_conflicting_identities(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            assets = {
                "targets": {"net11.0-android37.0": {
                    "SupportA/1.0": {"compile": {"lib/Shared.dll": {}}},
                    "SupportB/1.0": {"compile": {"lib/Shared.dll": {}}},
                }},
                "libraries": {
                    "SupportA/1.0": {"type": "package", "path": "supporta/1.0"},
                    "SupportB/1.0": {"type": "package", "path": "supportb/1.0"},
                },
                "packageFolders": {str(root): {}},
            }
            with self.assertRaisesRegex(corpus.CorpusInputError, "Missing support reference"):
                closure.support_references(assets, "net11.0-android")
            first = root / "supporta/1.0/lib/Shared.dll"
            second = root / "supportb/1.0/lib/Shared.dll"
            first.parent.mkdir(parents=True)
            second.parent.mkdir(parents=True)
            first.write_bytes(b"first")
            second.write_bytes(b"second")
            with self.assertRaisesRegex(corpus.CorpusInputError, "Conflicting support reference"):
                closure.support_references(assets, "net11.0-android")
            second.write_bytes(first.read_bytes())
            self.assertEqual(1, len(closure.support_references(assets, "net11.0-android")))

    def test_compatible_downgrade_promotes_within_all_declared_ranges(self):
        assets = {
            "logs": [{"code": "NU1605", "libraryId": "Core"}],
            "targets": {"net11.0-android": {
                "Parent/2.0": {"dependencies": {"Core": "[1.5.1.3,1.5.2)"}},
                "Other/2.0": {"dependencies": {"core": "[1.5.1.10,1.5.2)"}},
            }},
        }
        result = corpus.compatible_dependency_promotions(
            assets, [{"id": "Core", "version": "[1.5.1.2,1.5.2)"}]
        )
        self.assertEqual("1.5.1.10", result[0]["version"])
        self.assertEqual(3, len(result[0]["constraints"]))
        self.assertEqual("[1.5.1.2,1.5.2)", result[0]["original_range"])

    def test_incompatible_downgrade_is_not_overridden(self):
        assets = {
            "logs": [{"code": "NU1605", "libraryId": "Core"}],
            "targets": {"net11.0-android": {
                "Parent/2.0": {"dependencies": {"Core": "[1.6.0.3,1.6.1)"}},
            }},
        }
        with self.assertRaisesRegex(corpus.CorpusInputError, "No shared declared"):
            corpus.compatible_dependency_promotions(
                assets, [{"id": "Core", "version": "[1.2.0.2,1.2.1)"}]
            )

    def test_exact_and_exclusive_bounds_are_not_relaxed(self):
        for constraint in ("[1.0]", "[1.0,2.0)", "(2.0,3.0)"):
            with self.subTest(constraint=constraint):
                assets = {
                    "logs": [{"code": "NU1605", "libraryId": "Core"}],
                    "targets": {"net11.0-android": {
                        "Parent/2.0": {"dependencies": {"Core": "2.0"}},
                    }},
                }
                with self.assertRaises(corpus.CorpusInputError):
                    corpus.compatible_dependency_promotions(
                        assets, [{"id": "Core", "version": constraint}]
                    )

    def test_prerelease_reconciliation_fails_explicitly(self):
        with self.assertRaisesRegex(corpus.CorpusInputError, "non-stable"):
            corpus.stable_range("[1.0-preview,2.0)")

    def test_successful_restore_does_not_change_dependency_selection(self):
        self.assertEqual([], corpus.compatible_dependency_promotions(
            {"logs": []}, [{"id": "Core", "version": "1.0"}]
        ))

    def test_scope_keeps_every_group_and_explicit_nonbinding_and_dependency_only_records(self):
        artifacts = [
            {"groupId": "androidx.example", "artifactId": "one", "type": "no-bindings"},
            {"groupId": "com.google.android.gms", "artifactId": "two", "dependencyOnly": True},
            {"groupId": "com.google.firebase", "artifactId": "three"},
            {"groupId": "androidx-not-a-group", "artifactId": "four"},
            {"groupId": "com.google.mlkit", "artifactId": "five"},
            {"groupId": "com.squareup.okhttp3", "artifactId": "six"},
        ]
        self.assertEqual(artifacts, corpus.select_artifacts([{"artifacts": artifacts}]))

    def test_scope_preserves_record_order_versions_duplicates_and_multiple_solutions(self):
        first = {"groupId": "com.google.firebase", "artifactId": "example", "version": "1"}
        second = dict(first, version="2")
        self.assertEqual([first, second, first], corpus.select_artifacts([
            {"artifacts": [first, second]}, {"artifacts": [first]},
        ]))

    def test_android_groups_rank_numerically_and_exclude_future_net_versions(self):
        nuspec = ET.fromstring("""
            <package xmlns="urn:nuget"><metadata><dependencies>
            <group targetFramework="net12.0-android38.0"><dependency id="future" /></group>
            <group targetFramework="net9.0-android35.0"><dependency id="older" /></group>
            <group targetFramework="net10.0-android36.0"><dependency id="selected" /></group>
            <group targetFramework="netstandard2.1"><dependency id="standard" /></group>
            </dependencies></metadata></package>
        """)
        dependencies, group = corpus.dependency_group(nuspec, "net11.0-android")
        self.assertEqual("net10.0-android36.0", group)
        self.assertEqual("selected", dependencies[0].get("id"))

    def test_monoandroid_group_versions_are_not_sorted_lexically(self):
        nuspec = ET.fromstring("""
            <package><metadata><dependencies>
            <group targetFramework="MonoAndroid9.0" />
            <group targetFramework="MonoAndroid13.0" />
            </dependencies></metadata></package>
        """)
        self.assertEqual("MonoAndroid13.0", corpus.dependency_group(nuspec, "net11.0-android")[1])

    def test_incompatible_dependency_groups_fail_instead_of_becoming_empty(self):
        nuspec = ET.fromstring("""
            <package><metadata><dependencies><group targetFramework="net12.0-android38.0" /></dependencies></metadata></package>
        """)
        with self.assertRaises(corpus.CorpusInputError):
            corpus.dependency_group(nuspec, "net11.0-android")

    def test_ungrouped_dependencies_are_retained(self):
        nuspec = ET.fromstring("""
            <package><metadata><dependencies><dependency id="retained" version="1.0" /></dependencies></metadata></package>
        """)
        dependencies, group = corpus.dependency_group(nuspec, "net11.0-android")
        self.assertEqual("ungrouped", group)
        self.assertEqual("retained", dependencies[0].get("id"))

    def test_aar_counts_classes_in_classes_and_embedded_libraries_only(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as directory:
            jar = io.BytesIO()
            with zipfile.ZipFile(jar, "w") as archive:
                archive.writestr("example/One.class", b"fixture")
                archive.writestr("module-metadata.json", b"{}")
            aar = Path(directory) / "fixture.aar"
            with zipfile.ZipFile(aar, "w") as archive:
                archive.writestr("classes.jar", jar.getvalue())
                archive.writestr("libs/another.jar", jar.getvalue())
                archive.writestr("lint.jar", jar.getvalue())
            self.assertEqual(2, corpus.class_count(aar))

    def test_api_review_does_not_accept_missing_supported_or_unverified_members(self):
        decisions = [
            {"DeclaringType": "example.Service", "NativeSlot": "", "Classification": "Public", "Reason": "supported"},
            {"DeclaringType": "example.Service", "NativeSlot": "read()I", "Classification": "Public", "Reason": "supported"},
            {"DeclaringType": "example.Service", "NativeSlot": "unknown()I", "Classification": "Unverified", "Reason": "unknown"},
        ]
        delta = {"lost_methods": ["example.Service.read()I", "example.Service.unknown()I", "example.Service.missing()I"]}
        review = corpus.review_accessibility_changes(delta, decisions)
        self.assertEqual(delta["lost_methods"], review["unexplained_losses"]["methods"])
        self.assertEqual([], review["expected_profile_exclusions"]["methods"])

    def test_api_review_requires_explicit_exclusion_or_restricted_enclosing_type(self):
        decisions = [
            {"DeclaringType": "example.Service", "NativeSlot": "internal()I", "Classification": "Excluded", "Reason": "kotlin-internal"},
            {"DeclaringType": "example.Internal.Stub", "NativeSlot": "", "Classification": "Infrastructure", "Reason": "restricted-parent"},
        ]
        delta = {"lost_methods": ["example.Service.internal()I", "example.Internal.Stub.inherited()I"]}
        review = corpus.review_accessibility_changes(delta, decisions)
        self.assertEqual([], review["unexplained_losses"]["methods"])
        self.assertEqual(2, len(review["expected_profile_exclusions"]["methods"]))

    def test_api_review_normalizes_constructor_inventory_names(self):
        decisions = [
            {"DeclaringType": "example.Service", "NativeSlot": "<init>(I)V", "Classification": "Excluded", "Reason": "kotlin-internal"},
        ]
        delta = {"lost_methods": ["example.Service..ctor(I)V", "example.Service..ctor()V"]}
        review = corpus.review_accessibility_changes(delta, decisions)
        self.assertEqual(["example.Service..ctor()V"], review["unexplained_losses"]["methods"])
        self.assertEqual("example.Service..ctor(I)V", review["expected_profile_exclusions"]["methods"][0]["identity"])


if __name__ == "__main__":
    unittest.main()
