#!/usr/bin/env python3
"""Use exact consolidated DLLs with a precompiled fixture-only consumer."""

import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile


FILES = Path(__file__).resolve().parent
ROOT = Path("${ANDROID_SOURCE}")
TREE = FILES / "consolidated-corpus/tree"
OUTPUT = FILES / "consolidated-corpus/runtime"


def load(path):
    return json.loads(path.read_text())


def main():
    ownership = load(TREE / "assemblies/ownership.json")
    assemblies = {assembly["Assembly"]: assembly for assembly in ownership["Assemblies"]}
    fixture_name = "Android.Maven.Net.Dot.Android.Reference"
    fixture_directory = FILES / "maven-group-reference-final"
    fixture = fixture_directory / (fixture_name + ".dll")
    if not fixture.is_file():
        raise RuntimeError("The precompiled fixture-only consumer is missing.")
    fixture_metadata = next(assembly for assembly in load(fixture_directory / "ownership.json")["Assemblies"]
                            if assembly["Assembly"] == fixture_name)
    roots = ["Android.Maven.Androidx.Activity"] + fixture_metadata["References"]
    selected, pending = set(), list(roots)
    while pending:
        name = pending.pop()
        if name in selected or name not in assemblies:
            if name.startswith("Android.Maven.") and name not in assemblies and name != fixture_name:
                raise RuntimeError(f"The precompiled consumer needs a missing canonical assembly: {name}")
            continue
        selected.add(name)
        pending.extend(assemblies[name]["References"])
    OUTPUT.mkdir(exist_ok=True)
    runtime = OUTPUT / "assemblies"
    runtime.mkdir(exist_ok=True)
    hashes = {}
    for name in sorted(selected):
        source = TREE / "assemblies" / (name + ".dll")
        destination = runtime / source.name
        if destination.exists() or destination.is_symlink():
            if destination.resolve() != source.resolve():
                raise RuntimeError(f"Unexpected runtime selection output: {destination}")
        else:
            destination.symlink_to(source)
        hashes[name] = hashlib.sha256(source.read_bytes()).hexdigest()

    graph = {node["coordinate"]: node["dependencies"] for node in
             load(FILES / "canonical-corpus/resolved/dependency-graph.json")}
    artifacts = load(TREE / "native-inputs.json")
    modules = {f"{artifact['group']}:{artifact['artifact']}": f"{artifact['group']}:{artifact['artifact']}:{artifact['version']}"
               for artifact in artifacts}
    native_peers = {row["JavaType"] for row in ownership["Types"]
                    if row["Assembly"] in selected and row["JavaType"]}
    catalog = load(TREE / "canonical-owners.json")
    native_roots = {definition["Coordinate"] for row in catalog["Contracts"] if row["JavaType"] in native_peers
                    for definition in row["Definitions"]}
    native_roots.update(modules[name] for name in [
        "androidx.activity:activity", "androidx.datastore:datastore-preferences-android",
        "org.jetbrains.kotlin:kotlin-stdlib", "org.jetbrains.kotlinx:kotlinx-coroutines-android",
    ])
    included, pending = set(), list(native_roots)
    while pending:
        name = pending.pop()
        if name not in included:
            included.add(name)
            pending.extend(graph.get(name, []))
    chosen = [artifact for artifact in artifacts
              if f"{artifact['group']}:{artifact['artifact']}:{artifact['version']}" in included]
    old_native = ET.parse(FILES / "maven-group-smoke-native.props")
    extras = [Path(item.attrib["Include"]) for item in old_native.findall("ItemGroup/AndroidLibrary")
              if Path(item.attrib["Include"]).name in ("Reference.Native.aar", "dotnet-kotlin-runtime.jar")]
    if len(extras) != 2 or not all(path.is_file() for path in extras):
        raise RuntimeError("The fixture and source-built Kotlin bridge native inputs are missing.")
    project = ET.Element("Project")
    minimum_sdk = 24
    for artifact in chosen:
        path = Path(artifact["file"])
        if path.suffix == ".aar":
            with zipfile.ZipFile(path) as archive:
                manifest = ET.fromstring(archive.read("AndroidManifest.xml"))
                sdk = manifest.find("uses-sdk")
                if sdk is not None:
                    value = sdk.get("{http://schemas.android.com/apk/res/android}minSdkVersion")
                    if value:
                        minimum_sdk = max(minimum_sdk, int(value))
    properties = ET.SubElement(project, "PropertyGroup")
    ET.SubElement(properties, "SupportedOSPlatformVersion").text = str(minimum_sdk)
    ET.SubElement(properties, "JavaMaximumHeapSize").text = "4G"
    items = ET.SubElement(project, "ItemGroup")
    for path in [Path(artifact["file"]) for artifact in chosen] + extras:
        ET.SubElement(items, "AndroidLibrary", Include=str(path), Bind="false")
    ET.indent(project)
    ET.ElementTree(project).write(OUTPUT / "native.props", encoding="unicode")
    (OUTPUT / "selection.json").write_text(json.dumps({
        "policy": "Exact delivered DLLs, selected by their actual assembly-reference closure; no framework bindings regenerated.",
        "fixtureAssembly": str(fixture), "fixtureSha256": hashlib.sha256(fixture.read_bytes()).hexdigest(),
        "assemblies": hashes, "nativeInputs": chosen, "nativeExtras": [str(path) for path in extras],
        "minimumSdkVersion": minimum_sdk,
    }, indent=2) + "\n")
    print(f"Selected {len(selected)} exact consolidated DLLs and {len(chosen)} native archives.")
    print(f"Fixture: {fixture}")
    print(f"Runtime native props: {OUTPUT / 'native.props'}")


if __name__ == "__main__":
    main()
