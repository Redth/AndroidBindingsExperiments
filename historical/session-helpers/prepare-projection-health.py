#!/usr/bin/env python3
"""Prepare a small Health Connect regeneration against the existing canonical tree."""

import json
from pathlib import Path
import xml.etree.ElementTree as ET

FILES = Path(__file__).resolve().parent
ROOT = Path("${ANDROID_SOURCE}")
OUTPUT = FILES / "projection-health"
OUTPUT.mkdir(exist_ok=True)
artifacts = json.loads((FILES / "consolidated-corpus/tree/native-inputs.json").read_text())
graph = {row["coordinate"]: row["dependencies"] for row in
         json.loads((FILES / "canonical-corpus/resolved/dependency-graph.json").read_text())}
included = set()
pending = [f"{row['group']}:{row['artifact']}:{row['version']}" for row in artifacts
           if row["group"] == "androidx.health.connect"]
while pending:
    coordinate = pending.pop()
    if coordinate not in included:
        included.add(coordinate)
        pending.extend(graph.get(coordinate, []))
chosen = [row for row in artifacts if f"{row['group']}:{row['artifact']}:{row['version']}" in included]
(OUTPUT / "native-inputs.json").write_text(json.dumps(chosen, indent=2) + "\n")
project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
properties = ET.SubElement(project, "PropertyGroup")
for key, value in {
    "TargetFramework": "net11.0-android", "AssemblyName": "Android.Maven.Androidx.Health.Connect",
    "IsBindingProject": "true", "AllowUnsafeBlocks": "true", "Nullable": "enable",
    "UseSharedCompilation": "false", "EnableDefaultCompileItems": "false",
    "_AndroidExperimentalSemanticModel": "true", "_AndroidExperimentalGenericFacades": "true",
    "_AndroidExperimentalKotlinAsync": "true", "_AndroidExperimentalApiDescriptor": str(OUTPUT / "api-descriptor.json"),
}.items():
    ET.SubElement(properties, key).text = value
items = ET.SubElement(project, "ItemGroup")
for row in chosen:
    ET.SubElement(items, "AndroidLibrary", Include=row["file"],
                  Bind="true" if row["group"] == "androidx.health.connect" else "false", Pack="false")
assemblies = FILES / "consolidated-corpus/tree/assemblies"
ET.SubElement(items, "Reference", Include=str(assemblies / "*.dll"),
              Exclude=";".join(str(assemblies / name) for name in
                               ("Android.Maven.Androidx.Health.Connect.dll", "Microsoft.Android.Kotlin.dll")))
ET.SubElement(items, "ProjectReference", Include=str(ROOT / "src/Microsoft.Android.Kotlin/Microsoft.Android.Kotlin.csproj"))
ET.indent(project)
ET.ElementTree(project).write(OUTPUT / "Health.Connect.csproj", encoding="unicode")
print(f"Prepared {len(chosen)} pinned native inputs; only the Health Connect group is regenerated.")
