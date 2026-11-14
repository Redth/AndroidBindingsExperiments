import hashlib
import importlib.util
import io
from pathlib import Path
import tempfile
import unittest
import zipfile


spec = importlib.util.spec_from_file_location("prepare_inputs", Path(__file__).with_name("prepare-inputs.py"))
if spec is None or spec.loader is None:
    raise RuntimeError("Cannot load corpus preflight.")
preflight = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preflight)


class PrepareInputsTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def artifact(self, name, entries=None, group="example", module=None):
        path = self.root / name
        with zipfile.ZipFile(path, "w") as archive:
            for entry, content in (entries or {"example/Type.class": b"fixture"}).items():
                archive.writestr(entry, content)
        return {"group": group, "artifact": module or path.stem, "version": "1.0", "file": str(path),
                "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}

    def test_configuration_keeps_all_groups_versions_and_dependency_only_records(self):
        records = [
            {"groupId": "androidx.example", "artifactId": "one", "version": "1"},
            {"groupId": "com.google.firebase", "artifactId": "two", "version": "2", "dependencyOnly": True},
            {"groupId": "com.squareup.okhttp3", "artifactId": "three", "version": "3", "type": "no-bindings"},
        ]
        config = [{"artifacts": records[:1]}, {"artifacts": records[1:] + [records[1]]}]
        self.assertEqual(records + [records[1]], preflight.configuration_records(config))

    def test_configuration_coverage_requires_every_original_record(self):
        records = [
            {"groupId": "androidx.example", "artifactId": "one", "version": "1"},
            {"groupId": "com.google.firebase", "artifactId": "two", "version": "2"},
            {"groupId": "com.google.mlkit", "artifactId": "three", "version": "3"},
        ]
        config = [{"artifacts": records}]
        complete = preflight.configuration_coverage(config, {"records": [{"requested": record} for record in records]})
        self.assertTrue(complete["complete"])
        self.assertEqual(3, complete["coveredRecords"])
        missing = preflight.configuration_coverage(config, {"records": [{"requested": records[0]}]})
        self.assertFalse(missing["complete"])
        self.assertEqual(2, len(missing["missingRecords"]))
        self.assertEqual(1, missing["expectedMavenGroups"]["com.google.firebase"])

    def test_equal_record_count_cannot_hide_a_missing_or_replaced_artifact(self):
        first = {"groupId": "com.google.firebase", "artifactId": "one", "version": "1"}
        second = dict(first, artifactId="two")
        coverage = preflight.configuration_coverage([{"artifacts": [first, second]}],
                                                    {"records": [{"requested": first}, {"requested": first}]})
        self.assertFalse(coverage["complete"])
        self.assertEqual(second, coverage["missingRecords"][0]["record"])
        self.assertEqual(first, coverage["unexpectedRecords"][0]["record"])

    def test_requested_flags_are_not_silently_discarded(self):
        record = {"groupId": "com.google.firebase", "artifactId": "one", "version": "1", "dependencyOnly": True}
        coverage = preflight.configuration_coverage([{"artifacts": [record]}],
                                                    {"records": [{"requested": dict(record, dependencyOnly=False)}]})
        self.assertFalse(coverage["complete"])

    def test_invalid_configuration_coordinates_fail_instead_of_being_filtered_out(self):
        for value in (None, "", " ", 1):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "valid version"):
                preflight.configuration_records([{"artifacts": [
                    {"groupId": "com.google.firebase", "artifactId": "one", "version": value},
                ]}])

    def test_repeated_archive_records_do_not_create_false_class_conflicts(self):
        artifact = self.artifact("one.jar")
        result = preflight.analyze([artifact, dict(artifact)])
        self.assertTrue(result["coherent"])
        self.assertEqual(1, result["uniqueArchives"])
        self.assertEqual(1, len(result["repeatedArchiveRecords"]))

    def test_equal_archives_under_one_module_are_deduplicated(self):
        first = self.artifact("one.jar", module="module")
        second = dict(first, file=str(self.root / "two.jar"))
        Path(second["file"]).write_bytes(Path(first["file"]).read_bytes())
        result = preflight.analyze([first, second])
        self.assertEqual(1, result["uniqueArchives"])
        self.assertTrue(result["coherent"])

    def test_distinct_modules_cannot_silently_own_the_same_binary_name(self):
        for identical in (False, True):
            with self.subTest(identical=identical):
                first = self.artifact("one.jar")
                second = self.artifact("two.jar", {"example/Type.class": b"fixture" if identical else b"different"})
                result = preflight.analyze([first, second])
                self.assertFalse(result["coherent"])
                self.assertEqual(2, len(result["conflicts"][0]["definitions"]))
                self.assertEqual("identical-definition" if identical else "different-definitions", result["conflicts"][0]["kind"])

    def test_android_variant_aliases_are_explicit_and_do_not_pick_class_winners(self):
        android = self.artifact("module-android.jar")
        jvm = self.artifact("module-jvm.jar")
        result = preflight.analyze([android, jvm])
        self.assertEqual({"example:module-jvm": "example:module-android:1.0"}, result["androidVariantAliases"])
        self.assertFalse(result["coherent"])

    def test_changed_archives_are_rejected(self):
        artifact = self.artifact("one.jar")
        Path(artifact["file"]).write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "changed after resolution"):
            preflight.analyze([artifact])

    def test_aar_reads_embedded_jars_but_not_metadata_classes(self):
        nested = io.BytesIO()
        with zipfile.ZipFile(nested, "w") as jar:
            jar.writestr("example/Type.class", b"fixture")
            jar.writestr("module-info.class", b"module")
            jar.writestr("META-INF/versions/9/example/Type.class", b"version")
        artifact = self.artifact("one.aar", {"classes.jar": nested.getvalue()})
        result = preflight.analyze([artifact])
        self.assertTrue(result["coherent"])
        self.assertEqual(1, result["nativeClassNames"])


if __name__ == "__main__":
    unittest.main()
