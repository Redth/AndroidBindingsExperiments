#!/usr/bin/env python3
"""Audit resolved corpus archives before assigning canonical Maven-group owners."""

import argparse
import collections
import hashlib
import io
import json
from pathlib import Path
import zipfile


def digest(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for data in iter(lambda: stream.read(65536), b""):
            digest.update(data)
    return digest.hexdigest()


def coordinate(artifact):
    return ":".join(artifact[key] for key in ("group", "artifact", "version"))


def configuration_records(config):
    records = []
    for solution in config:
        for artifact in solution["artifacts"]:
            for key in ("groupId", "artifactId", "version"):
                if not isinstance(artifact.get(key), str) or not artifact[key].strip():
                    raise ValueError(f"Corpus record has no valid {key}: {artifact}")
            records.append(dict(artifact))
    return records


def configuration_coverage(config, selection):
    expected = configuration_records(config)
    actual = [record["requested"] for record in selection["records"]]

    def key(record):
        return json.dumps(record, sort_keys=True, separators=(",", ":"))

    missing = collections.Counter(map(key, expected)) - collections.Counter(map(key, actual))
    unexpected = collections.Counter(map(key, actual)) - collections.Counter(map(key, expected))
    return {
        "schemaVersion": 1,
        "complete": not missing and not unexpected,
        "originalRecords": len(expected),
        "coveredRecords": len(actual),
        "expectedMavenGroups": dict(sorted(collections.Counter(record["groupId"] for record in expected).items())),
        "coveredMavenGroups": dict(sorted(collections.Counter(record["groupId"] for record in actual).items())),
        "missingRecords": [{"record": json.loads(record), "count": count} for record, count in sorted(missing.items())],
        "unexpectedRecords": [{"record": json.loads(record), "count": count} for record, count in sorted(unexpected.items())],
    }


def classes(artifact):
    path = Path(artifact["file"])
    with zipfile.ZipFile(path) as archive:
        if path.suffix == ".jar":
            yield from jar_classes(archive)
        elif path.suffix == ".aar":
            for entry in archive.namelist():
                if entry == "classes.jar" or entry.startswith("libs/") and entry.endswith(".jar"):
                    with zipfile.ZipFile(io.BytesIO(archive.read(entry))) as jar:
                        yield from jar_classes(jar)
        else:
            raise ValueError(f"Unsupported native archive: {path}")


def jar_classes(archive):
    for entry in archive.infolist():
        if entry.filename.endswith(".class") and entry.filename != "module-info.class" and not entry.filename.startswith("META-INF/"):
            yield entry.filename[:-6], hashlib.sha256(archive.read(entry)).hexdigest()


def analyze(artifacts):
    unique = {}
    repeated = []
    for artifact in artifacts:
        actual = digest(Path(artifact["file"]))
        if artifact.get("sha256") != actual:
            raise ValueError(f"Native archive changed after resolution: {artifact['file']}")
        key = coordinate(artifact), actual
        if key in unique:
            repeated.append({"coordinate": key[0], "file": artifact["file"], "sameAs": unique[key]["file"]})
        else:
            unique[key] = artifact
    inputs = sorted(unique.values(), key=lambda artifact: (coordinate(artifact), artifact["file"]))
    modules = {(artifact["group"], artifact["artifact"]): artifact for artifact in inputs}
    aliases = {}
    for (group, module), artifact in modules.items():
        if module.endswith("-jvm"):
            android = modules.get((group, module[:-4] + "-android"))
            if android is not None:
                aliases[f"{group}:{module}"] = coordinate(android)
    owners = collections.defaultdict(list)
    counts = collections.Counter()
    for artifact in inputs:
        owner = coordinate(artifact)
        for name, sha256 in classes(artifact):
            owners[name].append({"coordinate": owner, "mavenGroup": artifact["group"], "sha256": sha256})
            counts[artifact["group"]] += 1
    conflicts = [{
        "javaType": name,
        "kind": "identical-definition" if len({owner["sha256"] for owner in definitions}) == 1 else "different-definitions",
        "definitions": definitions,
    } for name, definitions in sorted(owners.items()) if len(definitions) > 1]
    return {
        "schemaVersion": 1,
        "coherent": not conflicts,
        "inputArchiveRecords": len(artifacts),
        "uniqueArchives": len(inputs),
        "repeatedArchiveRecords": repeated,
        "nativeClassNames": len(owners),
        "mavenGroups": dict(sorted(counts.items())),
        "androidVariantAliases": dict(sorted(aliases.items())),
        "conflicts": conflicts,
        "inputs": inputs,
    }


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".writing")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-inputs", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--config", type=Path)
    parser.add_argument("--selection", type=Path)
    args = parser.parse_args()
    if bool(args.config) != bool(args.selection):
        parser.error("--config and --selection must be supplied together.")
    if args.config:
        selection = json.loads(args.selection.read_text())
        coverage = configuration_coverage(json.loads(args.config.read_text()), selection)
        coverage["configurationSha256"] = digest(args.config)
        coverage["configurationMatches"] = selection.get("configurationSha256") == coverage["configurationSha256"]
        save(args.output / "configuration-coverage.json", coverage)
        if not coverage["complete"] or not coverage["configurationMatches"]:
            print(f"The resolved corpus does not cover the complete config.json. See {args.output / 'configuration-coverage.json'}.")
            return 1
    result = analyze(json.loads(args.native_inputs.read_text()))
    save(args.output / "input-audit.json", result)
    save(args.output / "android-variant-aliases.json", result["androidVariantAliases"])
    pairs = collections.Counter(tuple(owner["coordinate"] for owner in conflict["definitions"]) for conflict in result["conflicts"])
    save(args.output / "conflict-summary.json", [{"artifacts": pair, "classNames": count} for pair, count in pairs.most_common()])
    print(f"{result['uniqueArchives']} unique archives; {result['nativeClassNames']} JVM names; {len(result['conflicts'])} conflicting names.")
    if result["conflicts"]:
        print(f"No canonical class definitions were selected for conflicting names. See {args.output / 'input-audit.json'}.")
    return 0 if result["coherent"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
