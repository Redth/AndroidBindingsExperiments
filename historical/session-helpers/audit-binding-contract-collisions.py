#!/usr/bin/env python3
"""Reproduce the distinction between raw JVM collisions and public API evidence."""

import collections
import json
from pathlib import Path
import xml.etree.ElementTree as ET


FILES = Path(__file__).resolve().parent
PROFILES = ("primary", "alternate-02", "play-services-ads-lite", "play-services-gass")


def main():
    audit = json.loads((FILES / "maven-group-corpus/preflight/input-audit.json").read_text())
    inputs = {record["javaType"]: record for record in audit["conflicts"]}
    observations = collections.defaultdict(list)
    for profile in PROFILES:
        path = FILES / "maven-group-assemblies" / profile / "obj/Debug/net11.0-android/api.xml.class-parse"
        for _, package in ET.iterparse(path, events=("end",)):
            if package.tag != "package":
                continue
            for node in package:
                name = package.attrib["name"].replace(".", "/") + "/" + node.attrib.get("name", "").replace(".", "$")
                if name not in inputs:
                    continue
                observations[name].append({
                    "profile": profile,
                    "kind": node.tag,
                    "superclass": node.attrib.get("extends"),
                    "visibility": node.attrib.get("visibility"),
                    "interfaces": [item.attrib for item in node if item.tag == "implements"],
                    "methods": [item.attrib for item in node if item.tag in ("constructor", "method")],
                    "fields": [item.attrib for item in node if item.tag == "field"],
                })
            package.clear()
    records = []
    for name in sorted(inputs):
        variants = observations[name]
        short = name.rsplit("/", 1)[-1].rsplit("$", 1)[-1]
        records.append({
            "javaType": name,
            "matchesLegacyZzObfuscationDefault": short.startswith("zz"),
            "kindConflict": len({item["kind"] for item in variants}) > 1,
            "superclassConflict": len({item["superclass"] for item in variants}) > 1,
            "nativeDefinitions": inputs[name]["definitions"],
            "observations": variants,
        })
    kinds = [item for item in records if item["kindConflict"]]
    metrics = {
        "rawNativeDuplicateNames": len(records),
        "namesMatchingLegacyZzObfuscationDefault": sum(item["matchesLegacyZzObfuscationDefault"] for item in records),
        "otherNamesNotAutomaticallyClassifiedAsSupported": sum(not item["matchesLegacyZzObfuscationDefault"] for item in records),
        "classInterfaceConflicts": len(kinds),
        "classInterfaceConflictsMatchingLegacyZzDefault": sum(item["matchesLegacyZzObfuscationDefault"] for item in kinds),
        "nonZzSuperclassConflicts": [item["javaType"] for item in records if item["superclassConflict"]
                                   and not item["matchesLegacyZzObfuscationDefault"]],
    }
    result = {
        "schemaVersion": 1,
        "scope": "These are raw native facts, not an audit declaring every non-zz type to be supported API.",
        "policyCaution": "Existing metadata has explicit obfuscated=false exceptions. Do not blanket-delete zz declarations or their required callback/ABI closure.",
        "configurationRevision": "2393441675a6d82bb817ab7aa2df698c7ec929ce",
        "metrics": metrics,
        "records": records,
    }
    output = FILES / "canonical-binding-contract-evidence.json"
    output.write_text(json.dumps(result, indent=2) + "\n")
    print(json.dumps(metrics, indent=2))
    print(output)


if __name__ == "__main__":
    main()
