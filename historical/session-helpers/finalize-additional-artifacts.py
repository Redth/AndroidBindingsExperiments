#!/usr/bin/env python3
"""Finalize per-artifact SDK trees and verify their runtime-tested bytes."""

import hashlib
import json
from pathlib import Path
import shutil
import subprocess

FILES = Path(__file__).resolve().parent
ROOT = Path("${ANDROID_SOURCE}")
TESTS = FILES / "third-party-sdk-tests"
EMITTER = ROOT / "external/Java.Interop/tools/grouped-bindings/bin/Debug/grouped-bindings.dll"


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command, log):
    with log.open("w") as stream:
        subprocess.run([str(value) for value in command], cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT, check=True)


def main():
    for name in ("maplibre-opengl", "sentry", "koin"):
        directory = TESTS / name
        assembly = next((directory / "bin/Debug/net11.0-android").glob("ThirdParty.*.dll"))
        output = directory / "artifact-assemblies"
        references = list((directory / "obj").rglob("*.referenced-owners.json"))
        if len(references) != 1:
            raise RuntimeError("Expected one referenced canonical owner report: " + name)
        command = [ROOT / "dotnet-local.sh", "--roll-forward", "Major", EMITTER, "--artifacts",
                   assembly, directory / "resolved/native-inputs.json", output, "--referenced-owners", references[0]]
        run(command, directory / "artifact-emission.log")
        registry = directory / "artifact-owners.json"
        shutil.copy2(output / "ownership.json", registry)
        run(command + ["--owners", registry], directory / "artifact-registry-replay.log")
        run([ROOT / "dotnet-local.sh", "--roll-forward", "Major", EMITTER, "--verify", output],
            directory / "artifact-verification.log")
        ownership = json.loads((output / "ownership.json").read_text())
        owned = {row["Assembly"] for row in ownership["Assemblies"]}
        nonartifact = [row["Assembly"] for row in ownership["Assemblies"] if row.get("MavenArtifact") is None and
                       row["Assembly"] != "Android.Maven.BindingSupport"]
        if nonartifact:
            raise RuntimeError("Unexpected group-level binding owners: " + str(nonartifact))
        stale = [reference for row in ownership["Assemblies"] for reference in row["References"]
                 if reference.startswith("ThirdParty.") or reference.startswith("Android.Maven.") and reference not in owned]
        if stale:
            raise RuntimeError("Legacy/monolithic binding dependency remains: " + str(sorted(set(stale))))
        for mode in ("artifact-device", "artifact-device-aot"):
            result = json.loads((directory / mode / "result.json").read_text())
            if result.get("passed") != 6 or result["results"][-1] != "DONE 6":
                raise RuntimeError("Incomplete runtime evidence: " + name + "/" + mode)
            for filename, digest in result["bindingAssembliesSha256"].items():
                if sha(output / filename) != digest:
                    raise RuntimeError("Runtime-tested artifact bytes changed: " + name + "/" + filename)
        print(name, len(ownership["Assemblies"]), "artifact/support assemblies, registry replay and runtime DLL hashes verified.")


if __name__ == "__main__":
    main()
