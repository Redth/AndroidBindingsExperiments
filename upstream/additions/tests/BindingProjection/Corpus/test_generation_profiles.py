import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
import zipfile


spec = importlib.util.spec_from_file_location("group_generation", Path(__file__).with_name("generate-assemblies.py"))
if spec is None or spec.loader is None:
    raise RuntimeError("Cannot load grouped generation.")
generation = importlib.util.module_from_spec(spec)
spec.loader.exec_module(generation)


class GenerationProfilesTests(unittest.TestCase):
    def test_consolidated_baseline_prefers_modern_roots_and_accounts_for_every_record(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary).resolve()
            coordinates = [
                ("com.google.firebase", "firebase-ml-vision", "example/Shared.class"),
                ("com.google.mlkit", "barcode-scanning", "example/Shared.class"),
                ("androidx.lifecycle", "lifecycle-common", "example/Lifecycle.class"),
            ]
            artifacts, records, graph = [], [], []
            for index, (group, module, java) in enumerate(coordinates):
                path = root / (module + ".jar")
                with zipfile.ZipFile(path, "w") as archive:
                    archive.writestr(java, str(index).encode())
                artifact = {"group": group, "artifact": module, "version": "1", "file": str(path),
                            "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                artifacts.append(artifact)
                records.append({"requested": {"groupId": group, "artifactId": module, "version": "1"},
                                "selected": artifact})
                graph.append({"coordinate": f"{group}:{module}:1",
                              "dependencies": ["androidx.lifecycle:lifecycle-common:1"] if index < 2 else []})
            config = root / "config.json"
            config.write_text(json.dumps([{"artifacts": [record["requested"] for record in records]}]))
            (root / "native-inputs.json").write_text(json.dumps(artifacts))
            (root / "selection.json").write_text(json.dumps({
                "configurationSha256": hashlib.sha256(config.read_bytes()).hexdigest(), "records": records,
            }))
            (root / "dependency-graph.json").write_text(json.dumps(graph))
            profiles, _ = generation.profiles(root, modern_baseline=True)
            self.assertIn("com.google.mlkit:barcode-scanning:1", profiles[0]["roots"])
            self.assertNotIn("com.google.firebase:firebase-ml-vision:1", profiles[0]["roots"])
            coverage = generation.baseline_coverage(root, profiles[0], artifacts)
            self.assertEqual(3, coverage["originalRecords"])
            self.assertEqual(2, coverage["includedRecords"])
            self.assertEqual("mutually-exclusive-native-alternative", coverage["records"][0]["status"])
            self.assertEqual(1, coverage["records"][0]["conflictingNativeNames"])
            with patch.object(generation.sys, "argv", ["generate-assemblies.py", "--resolved", str(root),
                    "--config", str(config), "--output", str(root / "output"), "--consolidated", "--plan-only"]):
                self.assertEqual(0, generation.main())
            self.assertFalse((root / "output/alternate-02").exists())
            self.assertEqual(3, len(json.loads((root / "output/baseline-coverage.json").read_text())["records"]))

    def test_catalog_mode_preserves_full_coverage_and_never_partitions_shared_owners(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary).resolve()
            record = {"groupId": "com.google.firebase", "artifactId": "example", "version": "1"}
            config = root / "config.json"
            config.write_text(json.dumps([{"artifacts": [record]}]))
            (root / "selection.json").write_text(json.dumps({
                "configurationSha256": hashlib.sha256(config.read_bytes()).hexdigest(),
                "records": [{"requested": record}],
            }))
            commands = []
            with patch.object(generation.sys, "argv", ["generate-assemblies.py", "--resolved", str(root),
                    "--config", str(config), "--output", str(root / "catalog"), "--catalog-only", "--reuse-descriptor"]), \
                    patch.dict(generation.os.environ, {"JAVA_HOME": str(root)}), \
                    patch.object(generation.Path, "is_file", return_value=True), \
                    patch.object(generation, "execute", side_effect=lambda command, log: commands.append(command)), \
                    patch.object(generation, "profiles", side_effect=AssertionError("Catalog mode must not partition owners by profile.")):
                self.assertEqual(0, generation.main())
            self.assertEqual(1, len(commands))
            self.assertIn("--catalog", commands[0])
            self.assertFalse((root / "catalog/profiles.json").exists())

    def test_source_groups_use_established_canonical_owners(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary).resolve()
            commands = []
            with patch.object(generation, "execute", side_effect=lambda command, log: commands.append(command)), \
                    patch.object(generation, "compile_source_implementations"):
                generation.compile_source_groups(root, root / "runtime.dll", root / "emitter.dll", root / "owners.json")
            self.assertEqual(["--owners", root / "owners.json"], commands[0][-2:])

    def test_source_groups_pass_the_artifact_assembly_layout(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary).resolve()
            commands = []
            with patch.object(generation, "execute", side_effect=lambda command, log: commands.append(command)), \
                    patch.object(generation, "compile_source_implementations"):
                generation.compile_source_groups(root, root / "runtime.dll", root / "emitter.dll", per_artifact=True)
            self.assertIn("--artifacts", commands[0])

    def test_source_groups_preserve_referenced_canonical_owners(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary).resolve()
            report = root / "obj/generated/api.xml.referenced-owners.json"
            report.parent.mkdir(parents=True)
            report.write_text(json.dumps({"SchemaVersion": 1, "Types": [
                {"JavaType": "org/json/JSONObject", "ManagedType": "Org.Json.JSONObject", "Assembly": "Mono.Android"},
            ]}))
            commands = []
            with patch.object(generation, "execute", side_effect=lambda command, log: commands.append(command)), \
                    patch.object(generation, "compile_source_implementations"):
                generation.compile_source_groups(root, root / "runtime.dll", root / "emitter.dll", root / "owners.json")
            self.assertEqual(["--referenced-owners", report], commands[0][-2:])

    def test_source_groups_remove_only_previously_owned_obsolete_outputs(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary).resolve()
            for name in ("contracts", "assemblies"):
                folder = root / name
                folder.mkdir()
                (folder / "Old.dll").write_bytes(b"old")
                (folder / "Unrelated.dll").write_bytes(b"keep")
            ownership = root / "contracts/ownership.json"
            ownership.write_text(json.dumps({"Assemblies": [{"Assembly": "Old"}]}))
            def emit(command, log):
                ownership.write_text(json.dumps({"Assemblies": [{"Assembly": "Current"}]}))
            with patch.object(generation, "execute", side_effect=emit), \
                    patch.object(generation, "compile_source_implementations"):
                generation.compile_source_groups(root, root / "runtime.dll", root / "emitter.dll")
            for name in ("contracts", "assemblies"):
                self.assertFalse((root / name / "Old.dll").exists())
                self.assertTrue((root / name / "Unrelated.dll").exists())

    def test_generation_rejects_an_old_partial_selection_of_the_same_config(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary).resolve()
            records = [
                {"groupId": "androidx.example", "artifactId": "one", "version": "1"},
                {"groupId": "com.google.firebase", "artifactId": "two", "version": "2"},
            ]
            config = root / "config.json"
            config.write_text(json.dumps([{"artifacts": records}]))
            (root / "selection.json").write_text(json.dumps({
                "configurationSha256": hashlib.sha256(config.read_bytes()).hexdigest(),
                "records": [{"requested": records[0]}],
            }))
            output = root / "output"
            with patch.object(generation.sys, "argv", ["generate-assemblies.py", "--resolved", str(root),
                                                     "--config", str(config), "--output", str(output), "--plan-only"]):
                with self.assertRaisesRegex(RuntimeError, "every config.json record"):
                    generation.main()
            coverage = json.loads((output / "configuration-coverage.json").read_text())
            self.assertTrue(coverage["configurationMatches"])
            self.assertFalse(coverage["complete"])
            self.assertEqual(1, len(coverage["missingRecords"]))

    def test_source_groups_keep_canonical_identities_and_unique_project_names(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary).resolve()
            (root / "contracts").mkdir()
            assemblies = [{"Assembly": name, "Version": "2.3.4.5", "SourceFiles": [str(root / (name + ".cs"))]}
                          for name in ("Android.Maven.Example.First", "Android.Maven.Example.Second")]
            (root / "contracts/ownership.json").write_text(json.dumps({"Assemblies": assemblies}))
            with patch.object(generation, "execute"):
                generation.compile_source_implementations(root, root / "runtime.dll", root / "emitter.dll")
            solution = ET.parse(root / "Groups.slnx")
            paths = [Path(project.attrib["Path"]) for project in solution.findall("Project")]
            self.assertEqual(2, len({path.name for path in paths}))
            for path in paths:
                project = ET.parse(path)
                self.assertEqual(path.stem, project.findtext("PropertyGroup/AssemblyName"))
                self.assertEqual("2.3.4.5", project.findtext("PropertyGroup/AssemblyVersion"))
                self.assertEqual("false", project.findtext("PropertyGroup/AndroidGenerateResourceDesigner"))
                self.assertNotIn(path.stem, [item.attrib["Include"] for item in project.findall("ItemGroup/Reference")])

    def test_bootstrap_bounds_analyzer_cost_without_suppressing_compiler_diagnostics(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary)
            (root / "native-inputs.json").write_text("[]")
            generation.project(root, root / "Microsoft.Android.Kotlin.dll")
            project = ET.parse(root / "Bootstrap.csproj")
            self.assertEqual("false", project.findtext("PropertyGroup/RunAnalyzersDuringBuild"))
            self.assertIsNone(project.find("PropertyGroup/NoWarn"))
            self.assertEqual("true", project.findtext("PropertyGroup/_AndroidExperimentalGenericFacades"))

    def test_conflicting_roots_are_covered_without_merging_their_definitions(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary)
            artifacts = []
            for name in ("first", "second"):
                path = root / (name + ".jar")
                with zipfile.ZipFile(path, "w") as archive:
                    archive.writestr("example/Shared.class", name.encode())
                artifacts.append({"group": "example", "artifact": name, "version": "1",
                                  "file": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
            selected = [{"requested": {"version": version}, "selected": artifact}
                        for artifact in artifacts for version in ("old", "new")]
            (root / "native-inputs.json").write_text(json.dumps(artifacts))
            (root / "selection.json").write_text(json.dumps({"records": selected}))
            (root / "dependency-graph.json").write_text(json.dumps([
                {"coordinate": "example:first:1", "dependencies": []},
                {"coordinate": "example:second:1", "dependencies": []},
            ]))
            profiles, _ = generation.profiles(root)
            self.assertEqual(2, len(profiles))
            self.assertEqual(set(range(4)), {index for profile in profiles for index in profile["records"]})
            self.assertTrue(all(len(profile["roots"]) == 1 for profile in profiles))

    def test_root_with_conflicting_transitives_is_re_resolved_in_isolation(self):
        scratch = Path(__file__).parent / "obj"
        scratch.mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(dir=scratch) as temporary:
            root = Path(temporary)
            artifacts = []
            for name in ("first", "second"):
                path = root / (name + ".jar")
                with zipfile.ZipFile(path, "w") as archive:
                    archive.writestr("example/Shared.class", name.encode())
                artifacts.append({"group": "example", "artifact": name, "version": "1",
                                  "file": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
            (root / "native-inputs.json").write_text(json.dumps(artifacts))
            (root / "selection.json").write_text(json.dumps({"records": [
                {"selected": {"group": "example", "artifact": "root", "version": "1"}},
            ]}))
            (root / "dependency-graph.json").write_text(json.dumps([
                {"coordinate": "example:root:1", "dependencies": ["example:first:1", "example:second:1"]},
                {"coordinate": "example:first:1", "dependencies": []},
                {"coordinate": "example:second:1", "dependencies": []},
            ]))
            profiles, _ = generation.profiles(root)
            self.assertEqual(1, len(profiles))
            self.assertTrue(profiles[0]["isolatedResolution"])
            self.assertEqual([0], profiles[0]["records"])
