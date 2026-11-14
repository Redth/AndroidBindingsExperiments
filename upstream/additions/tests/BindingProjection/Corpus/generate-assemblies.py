#!/usr/bin/env python3
"""Generate complete Maven-group binding profiles without merging native conflicts."""

import argparse
import collections
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[3]
spec = importlib.util.spec_from_file_location("corpus_inputs", Path(__file__).with_name("prepare-inputs.py"))
if spec is None or spec.loader is None:
    raise RuntimeError("Cannot load native input audit.")
inputs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inputs)


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".writing")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def execute(command, log):
    log.parent.mkdir(parents=True, exist_ok=True)
    print("Running", " ".join(str(x) for x in command[:4]), "->", log, flush=True)
    with log.open("w") as stream:
        result = subprocess.run([str(x) for x in command], cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f"Command failed ({result.returncode}): {command}\n" + "\n".join(log.read_text().splitlines()[-25:]))


def profiles(directory, modern_baseline=False):
    selection = json.loads((directory / "selection.json").read_text())
    graph = {node["coordinate"]: set(node["dependencies"]) for node in json.loads((directory / "dependency-graph.json").read_text())}
    audit = inputs.analyze(json.loads((directory / "native-inputs.json").read_text()))
    conflicts = collections.defaultdict(set)
    for conflict in audit["conflicts"]:
        modules = {definition["coordinate"] for definition in conflict["definitions"]}
        for module in modules:
            conflicts[module].update(modules - {module})

    def closure(root):
        seen, pending = set(), [root]
        while pending:
            node = pending.pop()
            if node not in seen:
                seen.add(node)
                pending.extend(graph.get(node, ()))
        return seen

    def compatible(modules):
        return not any(conflicts[module] & modules for module in modules)

    roots = {inputs.coordinate(record["selected"]) for record in selection["records"]}
    valid, isolated = [], []
    for root in sorted(roots):
        modules = closure(root)
        (valid if compatible(modules) else isolated).append((root, modules))
    def priority(item):
        root = item[0]
        modern = ("play-services-ads:", "play-services-ads-api:", "play-services-games-v2:")
        legacy = ("play-services-ads-base:", "play-services-drive:", "play-services-tagmanager-v4-impl:")
        if modern_baseline:
            modern_family = root.startswith(("com.google.mlkit:", "com.google.ai.edge.litert:litert:")) or \
                root.startswith("com.google.android.play:") and root.split(":")[1] not in ("core", "core-ktx")
            old_play = root.startswith(("com.google.android.play:core:", "com.google.android.play:core-ktx:"))
            return (0 if modern_family or any(value in root for value in modern) else
                    2 if old_play or any(value in root for value in legacy) else 1, root)
        return (0 if any(value in root for value in modern) else 2 if any(value in root for value in legacy) else 1, root)

    result = []
    for root, modules in sorted(valid, key=priority):
        for profile in result:
            merged = set(profile["modules"]) | modules
            if compatible(merged):
                profile["modules"] = sorted(merged)
                profile["roots"].append(root)
                break
        else:
            result.append({"name": "primary" if not result else f"alternate-{len(result) + 1:02}",
                           "roots": [root], "modules": sorted(modules), "isolatedResolution": False})
    for root, _ in isolated:
        result.append({"name": root.split(":")[1], "roots": [root], "modules": [], "isolatedResolution": True})
    covered = {root for profile in result for root in profile["roots"]}
    if covered != roots:
        raise RuntimeError("Profile partition lost original selected roots.")
    for profile in result:
        profile["records"] = [index for index, record in enumerate(selection["records"])
                              if inputs.coordinate(record["selected"]) in profile["roots"]]
    return result, audit["inputs"]


def baseline_coverage(resolved, baseline, artifacts):
    selection = json.loads((resolved / "selection.json").read_text())
    graph = {node["coordinate"]: node["dependencies"] for node in json.loads((resolved / "dependency-graph.json").read_text())}
    conflicts = inputs.analyze(artifacts)["conflicts"]
    modules = set(baseline["modules"])
    records = []
    for index, record in enumerate(selection["records"]):
        root = inputs.coordinate(record["selected"])
        pending, closure = [root], set()
        while pending:
            node = pending.pop()
            if node not in closure:
                closure.add(node)
                pending.extend(graph.get(node, ()))
        internal = [c for c in conflicts if sum(d["coordinate"] in closure for d in c["definitions"]) > 1]
        blockers = [c for c in conflicts if any(d["coordinate"] in closure - modules for d in c["definitions"]) and
                    any(d["coordinate"] in modules for d in c["definitions"])]
        included = root in modules
        if not included and not internal and not blockers:
            raise RuntimeError(f"Excluded root has no recorded native incompatibility: {root}")
        records.append({
            "index": index, "requested": record["requested"], "selected": record["selected"],
            "status": "included" if included else "incompatible-selected-closure" if internal else "mutually-exclusive-native-alternative",
            "variantAlias": record.get("variantAlias"), "publishedRelocation": record.get("publishedRelocation"),
            "superseded": record.get("superseded", False),
            "conflictingNativeNames": len(internal or blockers),
            "examples": (internal or blockers)[:5] if not included else [],
        })
    return {"schemaVersion": 1, "originalRecords": len(records), "includedRecords": sum(r["status"] == "included" for r in records),
            "policy": "One shared implementation tree; modern GMS Ads/Games, ML Kit and modular Play APIs preferred. Native alternatives are explicit, not duplicate managed trees.",
            "records": records}


def gradle(config, output, roots):
    return ["bash", ROOT / "build-tools/gradle/gradlew", "--no-daemon", "--console", "plain",
            "-p", ROOT / "tests/BindingProjection/Corpus/Native", "nativeInputs", "--write-locks",
            f"-PbindingCorpusConfig={config}", f"-PbindingCorpusOutput={output}",
            "-PbindingCorpusIncludeRoots=" + ",".join(":".join(root.split(":")[:2]) for root in roots)]


def project(directory, runtime, source_groups=False):
    tree = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(tree, "PropertyGroup")
    for key, value in {
        "TargetFramework": "net11.0-android", "AssemblyName": "Android.BindingCorpus.Bootstrap",
        "IsBindingProject": "true", "AllowUnsafeBlocks": "true", "Nullable": "enable",
        "UseSharedCompilation": "false", "EnableDefaultCompileItems": "false",
        "RunAnalyzersDuringBuild": "false",
        "EnableDefaultAndroidItems": "false", "_AndroidExperimentalSemanticModel": "true",
        "_AndroidExperimentalKotlinAsync": "true", "_AndroidExperimentalGenericFacades": "true",
        "_AndroidExperimentalApiDescriptor": str(directory / "api-descriptor.json"),
    }.items():
        ET.SubElement(properties, key).text = value
    items = ET.SubElement(tree, "ItemGroup")
    for artifact in json.loads((directory / "native-inputs.json").read_text()):
        ET.SubElement(items, "AndroidLibrary", Include=artifact["file"], Bind="true", Pack="false")
    reference = ET.SubElement(items, "Reference", Include="Microsoft.Android.Kotlin")
    ET.SubElement(reference, "HintPath").text = str(runtime)
    if source_groups:
        ET.SubElement(items, "InternalsVisibleTo", Include="Android.BindingCorpus.Inspection")
    ET.indent(tree)
    ET.ElementTree(tree).write(directory / "Bootstrap.csproj", encoding="unicode")

def compile_source_groups(directory, runtime, emitter, canonical_owners=None, per_artifact=False):
    contracts = directory / "contracts"
    previous_names = set()
    if (contracts / "ownership.json").is_file():
        previous_names = {row["Assembly"] for row in json.loads((contracts / "ownership.json").read_text())["Assemblies"]}
    command = [ROOT / "dotnet-local.sh", "--roll-forward", "Major", emitter, "--contracts",
             directory / "bin/Debug/net11.0-android/Android.BindingCorpus.Bootstrap.dll",
             directory / "native-inputs.json", directory / "obj/Debug/net11.0-android/generated/src", contracts]
    if per_artifact:
        command.insert(4, "--artifacts")
    if canonical_owners is not None:
        command.extend(["--owners", canonical_owners])
    referenced_owners = sorted((directory / "obj").rglob("*.referenced-owners.json"))
    if len(referenced_owners) > 1:
        raise RuntimeError(f"Ambiguous referenced canonical ownership reports: {referenced_owners}")
    if referenced_owners:
        command.extend(["--referenced-owners", referenced_owners[0]])
    execute(command, directory / "contracts.log")
    if previous_names:
        names = {row["Assembly"] for row in json.loads((contracts / "ownership.json").read_text())["Assemblies"]}
        for name in sorted(previous_names - names):
            if Path(name).name != name or "\\" in name:
                raise RuntimeError(f"Invalid previous generated assembly name: {name}")
            for folder in (contracts, directory / "assemblies"):
                path = folder / (name + ".dll")
                if path.is_file():
                    print("Removing obsolete generated assembly", path, flush=True)
                    path.unlink()
    compile_source_implementations(directory, runtime, emitter)


def compile_source_implementations(directory, runtime, emitter):
    contracts = directory / "contracts"
    ownership = json.loads((contracts / "ownership.json").read_text())
    assemblies = ownership["Assemblies"]
    solution = ET.Element("Solution")
    for assembly in assemblies:
        name = assembly["Assembly"]
        group = directory / "groups" / name
        group.mkdir(parents=True, exist_ok=True)
        tree = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
        properties = ET.SubElement(tree, "PropertyGroup")
        for key, value in {
            "TargetFramework": "net11.0-android", "AssemblyName": name,
            "AssemblyVersion": assembly.get("Version", "1.0.0.0"),
            "Nullable": "enable", "AllowUnsafeBlocks": "true", "EnableDefaultCompileItems": "false",
            "EnableDefaultAndroidItems": "false", "RunAnalyzersDuringBuild": "false",
            "AndroidGenerateResourceDesigner": "false",
            "UseSharedCompilation": "false", "AppendTargetFrameworkToOutputPath": "false",
            "OutputPath": str(directory / "assemblies"),
        }.items():
            ET.SubElement(properties, key).text = value
        items = ET.SubElement(tree, "ItemGroup")
        for source in assembly["SourceFiles"]:
            ET.SubElement(items, "Compile", Include=source)
        for other in assemblies:
            if other["Assembly"] == name:
                continue
            reference = ET.SubElement(items, "Reference", Include=other["Assembly"])
            ET.SubElement(reference, "HintPath").text = str(contracts / (other["Assembly"] + ".dll"))
            ET.SubElement(reference, "Private").text = "false"
            ET.SubElement(items, "InternalsVisibleTo", Include=other["Assembly"])
        reference = ET.SubElement(items, "Reference", Include="Microsoft.Android.Kotlin")
        ET.SubElement(reference, "HintPath").text = str(runtime)
        ET.SubElement(reference, "Private").text = "false"
        ET.indent(tree)
        project_path = group / (name + ".csproj")
        ET.ElementTree(tree).write(project_path, encoding="unicode")
        ET.SubElement(solution, "Project", Path=str(project_path))
    solution_path = directory / "Groups.slnx"
    ET.indent(solution)
    ET.ElementTree(solution).write(solution_path, encoding="unicode")
    execute([ROOT / "dotnet-local.sh", "build", solution_path, "-c", "Debug", "-m:2", "-v:q", "-nr:false"],
            directory / "groups-build.log")
    execute([ROOT / "dotnet-local.sh", "--roll-forward", "Major", emitter, "--finalize",
             directory / "assemblies", contracts / "ownership.json"], directory / "verify.log")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--resolved", required=True, type=Path)
    parser.add_argument("--config", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--profile", action="append")
    parser.add_argument("--plan-only", action="store_true")
    parser.add_argument("--split-only", action="store_true")
    parser.add_argument("--source-groups", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--reuse-descriptor", action="store_true")
    parser.add_argument("--canonical-owners", type=Path, help="Reuse established canonical type/assembly owners instead of reassigning them from native packaging.")
    parser.add_argument("--catalog-only", action="store_true", help="Classify all native inputs and plan one public owner per Java type without generating per-profile DLLs.")
    parser.add_argument("--consolidated", action="store_true", help="Build one compatible modern tree, with complete original-record accounting and no alternative DLL bundles.")
    parser.add_argument("--assembly-granularity", choices=("artifact", "group"), default="artifact",
                        help="One assembly per Maven artifact (default), or the earlier Maven-group layout.")
    args = parser.parse_args()
    resolved, config, output = args.resolved.resolve(), args.config.resolve(), args.output.resolve()
    selection = json.loads((resolved / "selection.json").read_text())
    coverage = inputs.configuration_coverage(json.loads(config.read_text()), selection)
    coverage["configurationSha256"] = inputs.digest(config)
    coverage["configurationMatches"] = selection.get("configurationSha256") == coverage["configurationSha256"]
    save(output / "configuration-coverage.json", coverage)
    if not coverage["complete"] or not coverage["configurationMatches"]:
        raise RuntimeError(f"The resolved corpus does not cover every config.json record. See {output / 'configuration-coverage.json'}.")
    if args.catalog_only:
        java = Path(os.environ["JAVA_HOME"]) / "bin/java"
        tool = ROOT / "external/Java.Interop/tools/jvm-api-descriptor/build/install/jvm-api-descriptor/lib"
        emitter = ROOT / "external/Java.Interop/tools/grouped-bindings/bin/Debug/grouped-bindings.dll"
        if not emitter.is_file():
            raise RuntimeError("Build tools/grouped-bindings/grouped-bindings.csproj first.")
        if not args.reuse_descriptor:
            execute([java, "-Xmx5g", "-cp", str(tool) + "/*", "net.dot.android.descriptor.ApiDescriptorKt",
                     "--manifest", resolved / "native-inputs.json", "--output-directory", output / "descriptors"],
                    output / "descriptors.log")
        command = [ROOT / "dotnet-local.sh", "--roll-forward", "Major", emitter, "--catalog",
                   resolved / "native-inputs.json", output / "descriptors", output / "canonical-owners.json"]
        if args.assembly_granularity == "artifact":
            command.insert(4, "--artifacts")
        if args.canonical_owners:
            command.append(args.canonical_owners.resolve())
        execute(command, output / "catalog.log")
        print("Audited all configured native inputs; canonical owners and explicit unresolved contracts:", output / "canonical-owners.json")
        return 0
    if args.canonical_owners and (not args.source_groups or not args.consolidated and (not args.profile or len(args.profile) != 1)):
        raise RuntimeError("Canonical implementation generation requires --source-groups and exactly one --profile native baseline.")
    if args.consolidated and (args.profile or not args.source_groups):
        raise RuntimeError("--consolidated requires source-group compilation and chooses its own single native baseline.")
    plan, artifacts = profiles(resolved, modern_baseline=args.consolidated)
    save(output / "profiles.json", {"schemaVersion": 1, "originalRecords": sum(len(profile["records"]) for profile in plan),
         "policy": "Modern primary profile plus compatible alternates; no duplicate native definitions merged.",
         "profiles": plan})
    if args.consolidated:
        save(output / "baseline-coverage.json", baseline_coverage(resolved, plan[0], artifacts))
        plan = [dict(plan[0], name="tree")]
    if args.plan_only:
        print(json.dumps(plan, indent=2))
        return 0
    runtime = ROOT / "src/Microsoft.Android.Kotlin/bin/Debug/net11.0-android/Microsoft.Android.Kotlin.dll"
    if not runtime.is_file():
        raise RuntimeError("Build the canonical Microsoft.Android.Kotlin runtime first.")
    java = Path(os.environ["JAVA_HOME"]) / "bin/java"
    tool = ROOT / "external/Java.Interop/tools/jvm-api-descriptor/build/install/jvm-api-descriptor/lib"
    emitter = ROOT / "external/Java.Interop/tools/grouped-bindings/bin/Debug/grouped-bindings.dll"
    if not emitter.is_file():
        raise RuntimeError("Build tools/grouped-bindings/grouped-bindings.csproj first.")
    for profile in plan:
        if args.profile and profile["name"] not in args.profile:
            continue
        directory = output / profile["name"]
        directory.mkdir(parents=True, exist_ok=True)
        if not args.split_only:
            if profile["isolatedResolution"]:
                execute(gradle(config, directory, profile["roots"]), directory / "resolve.log")
            else:
                chosen = [artifact for artifact in artifacts if inputs.coordinate(artifact) in profile["modules"]]
                save(directory / "native-inputs.json", chosen)
            audit = inputs.analyze(json.loads((directory / "native-inputs.json").read_text()))
            save(directory / "input-audit.json", audit)
            if not audit["coherent"]:
                raise RuntimeError(f"Profile remains natively incoherent: {profile['name']}")
            if not args.reuse_descriptor:
                execute([java, "-Xmx5g", "-cp", str(tool) + "/*", "net.dot.android.descriptor.ApiDescriptorKt",
                         "--manifest", directory / "native-inputs.json", "--output", directory / "api-descriptor.json"],
                        directory / "descriptor.log")
            if args.consolidated:
                if not args.reuse_descriptor:
                    execute([java, "-Xmx5g", "-cp", str(tool) + "/*", "net.dot.android.descriptor.ApiDescriptorKt",
                             "--manifest", directory / "native-inputs.json", "--output-directory", directory / "descriptors"],
                            directory / "descriptor-index.log")
                command = [ROOT / "dotnet-local.sh", "--roll-forward", "Major", emitter, "--catalog",
                           directory / "native-inputs.json", directory / "descriptors", directory / "canonical-owners.json"]
                if args.assembly_granularity == "artifact":
                    command.insert(4, "--artifacts")
                if args.canonical_owners:
                    command.append(args.canonical_owners.resolve())
                execute(command, directory / "canonical-owners.log")
            project(directory, runtime, args.source_groups)
            command = [ROOT / "dotnet-local.sh", "build", directory / "Bootstrap.csproj", "-c", "Debug", "-v:q", "-nr:false"]
            if args.source_groups:
                command.append("-p:ProduceOnlyReferenceAssembly=true")
            execute(command, directory / "build.log")
        if args.source_groups:
            canonical_owners = directory / "canonical-owners.json" if args.consolidated else args.canonical_owners
            compile_source_groups(directory, runtime, emitter, canonical_owners, args.assembly_granularity == "artifact")
            print("Compiled and verified source groups", profile["name"], flush=True)
            continue
        assembly = directory / "bin/Debug/net11.0-android/Android.BindingCorpus.Bootstrap.dll"
        command = [ROOT / "dotnet-local.sh", "--roll-forward", "Major", emitter, assembly,
                   directory / "native-inputs.json", directory / "assemblies"]
        if args.assembly_granularity == "artifact":
            command.insert(4, "--artifacts")
        execute(command, directory / "group.log")
        execute([ROOT / "dotnet-local.sh", "--roll-forward", "Major", emitter, "--verify",
                 directory / "assemblies"], directory / "verify.log")
        print("Generated and verified profile", profile["name"], flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
