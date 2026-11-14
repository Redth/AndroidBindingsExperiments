import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
from types import SimpleNamespace
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / "workbench.py"
SPEC = importlib.util.spec_from_file_location("workbench", SCRIPT)
workbench = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(workbench)


class SnapshotTests(unittest.TestCase):
    def setUp(self):
        scratch = workbench.ROOT / ".artifacts" / "python-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.upstream = self.root / "upstream"
        self.upstream.mkdir()
        self.patch = self.upstream / "patches" / "android.patch"
        self.patch.parent.mkdir()
        self.patch.write_text("test patch\n", encoding="utf-8")
        self.addition = self.upstream / "additions" / "new.cs"
        self.addition.parent.mkdir()
        self.addition.write_text("source\n", encoding="utf-8")
        self.manifest = {
            "schemaVersion": 1,
            "repository": workbench.SOURCE_URL,
            "commit": workbench.SOURCE_COMMIT,
            "patches": [{"path": "patches/android.patch", "sha256": workbench.digest(self.patch)}],
            "additionsRoot": "additions",
            "additions": [{
                "path": "new.cs", "sha256": workbench.digest(self.addition), "mode": "100644",
            }],
            "trackedOutputs": [],
        }
        self.write_manifest()
        self.override = mock.patch.object(workbench, "UPSTREAM", self.upstream)
        self.override.start()
        self.addCleanup(self.override.stop)

    def write_manifest(self):
        (self.upstream / "manifest.json").write_text(json.dumps(self.manifest), encoding="utf-8")

    def test_modified_addition_cannot_bootstrap(self):
        self.addition.write_text("changed after export\n", encoding="utf-8")
        source = self.root / "checkout"
        with self.assertRaisesRegex(workbench.WorkbenchError, "Checksum mismatch"):
            workbench.bootstrap(source)
        self.assertFalse(source.exists())

    def test_unlisted_source_is_not_silently_omitted(self):
        (self.upstream / "additions" / "forgotten.cs").write_text("unlisted", encoding="utf-8")
        with self.assertRaisesRegex(workbench.WorkbenchError, "inventory differs"):
            workbench.snapshot_files()

    def test_traversal_is_rejected_before_fetching(self):
        self.manifest["additions"][0]["path"] = "../../outside.cs"
        self.write_manifest()
        with self.assertRaisesRegex(workbench.WorkbenchError, "Unsafe snapshot path"):
            workbench.snapshot_files()

    def test_nonempty_checkout_is_never_overwritten(self):
        source = self.root / "checkout"
        source.mkdir()
        preserved = source / "user-file"
        preserved.write_text("keep this", encoding="utf-8")
        with mock.patch.object(workbench, "run") as run:
            with self.assertRaisesRegex(workbench.WorkbenchError, "Refusing to overwrite"):
                workbench.bootstrap(source)
        run.assert_not_called()
        self.assertEqual(preserved.read_text(encoding="utf-8"), "keep this")

    def test_declared_worktree_line_endings_are_accepted_but_edits_are_not(self):
        source = self.root / "checkout"
        source.mkdir()
        (source / "new.cs").write_bytes(self.addition.read_bytes())
        original = b"line\r\n"
        self.manifest["trackedOutputs"] = [{
            "path": "tracked.cs",
            "sha256": hashlib.sha256(b"line\n").hexdigest(),
            "worktreeSha256": hashlib.sha256(original).hexdigest(),
        }]
        self.write_manifest()
        tracked = source / "tracked.cs"
        tracked.write_bytes(original)
        workbench.verify_materialized(source)
        tracked.write_bytes(b"edited\r\n")
        with self.assertRaisesRegex(workbench.WorkbenchError, "differs from export"):
            workbench.verify_materialized(source)

    def test_symlink_addition_is_not_followed(self):
        self.addition.unlink()
        self.addition.symlink_to(self.patch)
        with self.assertRaisesRegex(workbench.WorkbenchError, "unsupported symlink"):
            workbench.snapshot_files()

    def test_packing_cannot_claim_original_provenance_after_an_unrelated_source_edit(self):
        source = self.root / "checkout"
        source.mkdir()
        (source / "new.cs").write_bytes(self.addition.read_bytes())
        with mock.patch.object(workbench, "run", return_value="unrelated.cs\0") as run:
            with self.assertRaisesRegex(workbench.WorkbenchError, "source inventory differs"):
                workbench.pack(SimpleNamespace(source=source))
        self.assertEqual(run.call_count, 1)

    def test_restored_package_bytes_must_match_the_local_feed(self):
        package = self.root / f"{workbench.PACKAGE}.{workbench.VERSION}.nupkg"
        package.write_bytes(b"fresh package")
        cache = self.root / "cache"
        restored = cache / workbench.PACKAGE.lower() / workbench.VERSION / package.name.lower()
        restored.parent.mkdir(parents=True)
        restored.write_bytes(b"older package under the same ID and version")
        with self.assertRaisesRegex(workbench.WorkbenchError, "does not match"):
            workbench.verify_restored_package(package, cache)
        restored.write_bytes(package.read_bytes())
        workbench.verify_restored_package(package, cache)


if __name__ == "__main__":
    unittest.main()
