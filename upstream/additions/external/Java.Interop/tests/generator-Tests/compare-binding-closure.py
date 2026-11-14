#!/usr/bin/env python3
"""Compare one-assembly projections of an already-resolved native dependency closure."""

import argparse
import importlib
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET


corpus = importlib.import_module("compare-binding-corpus")
comparison = importlib.import_module("compare-bindings")


def published_android_binding(name):
    return name.lower().startswith(("xamarin.androidx.", "xamarin.googleplayservices."))


def support_references(assets, framework):
    references = set()
    targets = [libraries for target, libraries in assets["targets"].items()
               if target.split("/")[0].startswith(framework)]
    if not targets:
        raise corpus.CorpusInputError(f"No support-reference assets for {framework}.")
    for libraries in targets:
        for owner, library in libraries.items():
            if published_android_binding(owner.split("/")[0]):
                continue
            metadata = assets["libraries"][owner]
            if metadata["type"] != "package":
                continue
            for asset in library.get("compile", {}):
                if not asset.endswith(".dll"):
                    continue
                candidates = [
                    Path(folder) / metadata["path"] / asset for folder in assets["packageFolders"]
                ]
                path = next((candidate for candidate in candidates if candidate.exists()), None)
                if path is None:
                    raise corpus.CorpusInputError(f"Missing support reference: {owner}/{asset}")
                references.add(str(path))
    by_name = {}
    for reference in sorted(references):
        name = Path(reference).name.lower()
        if name in by_name and corpus.digest(Path(by_name[name])) != corpus.digest(Path(reference)):
            raise corpus.CorpusInputError(f"Conflicting support reference versions for {name}.")
        by_name[name] = reference
    return list(by_name.values())


def device_probe(comparison_file, serial, configuration="Debug", nativeaot=False):
    summary = json.loads(comparison_file.read_text())
    if summary["modes"]["semantic"].get("compilation", {}).get("exit_code") != 0:
        raise corpus.CorpusInputError("The regenerated closure must compile before device validation.")
    if "androidx.emoji2:emoji2-emojipicker:1.7.0" in summary["inputs"]["artifacts"]:
        fixture_name = "ClosureProbeActivity.cs"
    elif all(coordinate in summary["inputs"]["artifacts"] for coordinate in (
        "com.squareup.okio:okio-jvm:3.9.1", "com.squareup.okhttp3:okhttp:4.12.0",
        "io.reactivex.rxjava2:rxjava:2.2.21", "io.reactivex.rxjava3:rxjava:3.1.10",
        "com.google.code.gson:gson:2.11.0", "com.google.protobuf:protobuf-javalite:3.25.5",
    )):
        fixture_name = "SupportClosureProbeActivity.cs"
    else:
        raise corpus.CorpusInputError("No matching device fixture exists for this resolved native closure.")
    sdk = os.environ.get("ANDROID_HOME")
    if not sdk:
        raise corpus.CorpusInputError("Set ANDROID_HOME for device validation.")
    adb = [str(Path(sdk) / "platform-tools/adb"), "-s", serial]
    abi = subprocess.check_output(adb + ["shell", "getprop", "ro.product.cpu.abi"], text=True).strip()
    runtime = {"arm64-v8a": "android-arm64", "x86_64": "android-x64",
               "armeabi-v7a": "android-arm", "x86": "android-x86"}[abi]
    directory = comparison_file.parent / ("device-" + uuid.uuid4().hex[:8])
    directory.mkdir()
    identity = uuid.uuid4().hex
    fixture = Path(__file__).parent / "TestInputs/SemanticDeviceProbe" / fixture_name
    shutil.copyfile(fixture, directory / fixture.name)
    (directory / "ProbeRunIdentity.cs").write_text(
        'namespace BindingCompilerSpike { static class ProbeRunIdentity { public const string Value = "'
        + identity + '"; } }\n'
    )
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in {
        "TargetFramework": summary["inputs"]["tools"]["framework"], "OutputType": "Exe",
        "RootNamespace": "BindingCompilerSpike", "RuntimeIdentifier": runtime,
        "ApplicationId": "com.dotnet.bindingcompiler.closure", "SupportedOSPlatformVersion": "24",
        "AllowUnsafeBlocks": "true", "Nullable": "enable", "EmbedAssembliesIntoApk": "true",
    }.items():
        ET.SubElement(properties, name).text = value
    if configuration == "Release":
        ET.SubElement(properties, "TrimMode").text = "full"
        ET.SubElement(properties, "AndroidLinkTool").text = "r8"
    if nativeaot:
        ET.SubElement(properties, "PublishAot").text = "true"
    items = ET.SubElement(project, "ItemGroup")
    support_directory = directory / "support"
    support_directory.mkdir()
    references = []
    for path, digest in summary["inputs"]["support_reference_hashes"].items():
        source = Path(path)
        if corpus.digest(source) != digest:
            raise corpus.CorpusInputError(f"Support reference changed after comparison: {source}")
        destination = support_directory / source.name
        shutil.copyfile(source, destination)
        references.append(str(destination))
    references.append(str(comparison_file.parent / "semantic/BindingClosure.dll"))
    for path in references:
        reference = ET.SubElement(items, "Reference", Include=Path(path).stem)
        ET.SubElement(reference, "HintPath").text = path
        metadata = ET.SubElement(items, "AndroidCustomMetaDataForReferences", Include=Path(path).stem)
        ET.SubElement(metadata, "AndroidSkipResourceExtraction").text = "true"
    for artifact in summary["inputs"]["artifacts"].values():
        if corpus.digest(Path(artifact["file"])) != artifact["sha256"]:
            raise corpus.CorpusInputError(f"Native archive changed after comparison: {artifact['file']}")
        ET.SubElement(items, "AndroidLibrary", Include=artifact["file"], Bind="false")
    ET.indent(project)
    ET.ElementTree(project).write(directory / "DeviceProbe.csproj", encoding="unicode")
    build = corpus.execute(
        [summary["inputs"]["tools"]["dotnet"], "build", "DeviceProbe.csproj", "-t:Install",
         "-c", configuration, f"-p:AdbTarget=-s {serial}", "-v:minimal"],
        directory, directory / "build-install.log",
    )
    if build["exit_code"] != 0:
        raise corpus.CorpusInputError(f"Closure device build failed: {build['log']}")
    launch = corpus.execute(
        adb + ["shell", "am", "start", "-S", "-W", "-n",
               "com.dotnet.bindingcompiler.closure/net.dot.android.bindingcompiler.ClosureProbeActivity"],
        directory, directory / "launch.log",
    )
    if launch["exit_code"] != 0 or "Error" in Path(launch["log"]).read_text():
        raise corpus.CorpusInputError(f"Closure device launch failed: {launch['log']}")
    results = ""
    for _ in range(120):
        read = subprocess.run(
            adb + ["logcat", "-d", "-v", "raw", "-s", "BindingClosureProbe:V", "*:S"],
            capture_output=True, text=True,
        )
        if read.returncode == 0:
            lines = [line[len(identity) + 1:] for line in read.stdout.splitlines()
                     if line.startswith(identity + " ")]
            results = identity + "\n" + "\n".join(lines)
            if "\nDONE " in results or "\nFAIL " in results:
                break
        time.sleep(0.5)
    (directory / "results.txt").write_text(results)
    corpus.execute(adb + ["logcat", "-d", "-s", "BindingClosureProbe:V", "AndroidRuntime:E", "*:S"],
                   directory, directory / "logcat.log")
    passed = [line for line in results.splitlines() if line.startswith("PASS ")]
    if not results.splitlines() or results.splitlines()[-1] != "DONE 6" or len(set(passed)) != 6 or len(passed) != 6 or "\nFAIL " in results:
        raise corpus.CorpusInputError(f"Closure device checks failed: {directory}")
    evidence = {
        "directory": str(directory), "identity": identity, "serial": serial, "abi": abi,
        "configuration": configuration, "nativeaot": nativeaot,
        "build": build, "launch": launch, "results": results,
        "fixture_sha256": corpus.digest(directory / fixture.name),
    }
    summary.setdefault("device_probes", []).append(evidence)
    corpus.save(directory / "device-results.json", evidence)
    corpus.save(comparison_file, summary)
    print(f"Regenerated closure Android probe: 6/6 passed; evidence: {directory}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-inputs", type=Path,
                        help="Resolved artifact JSON: group, artifact, version, file.")
    parser.add_argument("--support-assets", type=Path,
                        help="NuGet assets supplying only non-AndroidX/non-GMS support bindings.")
    parser.add_argument("--tools-from", type=Path,
                        help="Corpus summary containing an immutable generator snapshot.")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--probe-comparison", type=Path)
    parser.add_argument("--device-serial")
    parser.add_argument("--probe-configuration", choices=["Debug", "Release"], default="Debug")
    parser.add_argument("--probe-nativeaot", action="store_true")
    parser.add_argument("--regenerate-support", action="store_true",
                        help="Regenerate every resolved JVM archive without published managed support bindings.")
    parser.add_argument("--kotlin-runtime", type=Path,
                        help="Canonical runtime DLL; also enable Kotlin conveniences throughout the semantic closure.")
    args = parser.parse_args()
    if args.probe_nativeaot and args.probe_configuration != "Release":
        parser.error("--probe-nativeaot requires --probe-configuration Release")
    if args.probe_comparison:
        if not args.device_serial:
            parser.error("--probe-comparison requires --device-serial")
        device_probe(args.probe_comparison.resolve(), args.device_serial, args.probe_configuration, args.probe_nativeaot)
        return 0
    if not all((args.native_inputs, args.tools_from, args.output)) or (not args.regenerate_support and not args.support_assets):
        parser.error("A new comparison requires --native-inputs, --tools-from, --output, and either --support-assets or --regenerate-support")
    tools = json.loads(args.tools_from.read_text())["tools"]
    directory = args.output.resolve() / ("closure-" + uuid.uuid4().hex[:12])
    directory.mkdir(parents=True)
    shutil.copyfile(__file__, directory / Path(__file__).name)
    shutil.copyfile(args.native_inputs, directory / "native-inputs.json")
    support = [] if args.regenerate_support else support_references(json.loads(args.support_assets.read_text()), tools["framework"])
    if args.kotlin_runtime:
        support.append(str(args.kotlin_runtime.resolve()))
    artifacts = {}
    for artifact in json.loads(args.native_inputs.read_text()):
        coordinate = ":".join(artifact[key] for key in ("group", "artifact", "version"))
        path = Path(artifact["file"])
        digest = corpus.digest(path)
        if coordinate in artifacts:
            if artifacts[coordinate]["sha256"] != digest:
                raise corpus.CorpusInputError(f"Different archives for resolved coordinate {coordinate}.")
            continue
        bind = artifact.get("bind", args.regenerate_support or artifact["group"].startswith("androidx.") or artifact["group"] == "com.google.android.gms")
        artifacts[coordinate] = dict(artifact, sha256=digest, bind=bind)
    if not any(artifact["bind"] for artifact in artifacts.values()):
        raise corpus.CorpusInputError("The closure contains no AndroidX/GMS artifacts to regenerate.")
    manifest = {
        "reference_mode": ("Regenerated complete native closure; no published managed support bindings." if args.regenerate_support
                           else "Regenerated AndroidX/GMS closure in one assembly; published non-AndroidX support bindings."),
        "artifacts": artifacts,
        "support_reference_hashes": {path: corpus.digest(Path(path)) for path in support},
        "tools": tools,
    }
    corpus.save(directory / "inputs.json", manifest)
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in {
        "TargetFramework": tools["framework"], "AssemblyName": "BindingClosure",
        "IsBindingProject": "true", "Nullable": "enable",
        "EnableDefaultCompileItems": "false", "EnableDefaultAndroidItems": "false",
    }.items():
        ET.SubElement(properties, name).text = value
    items = ET.SubElement(project, "ItemGroup")
    for artifact in artifacts.values():
        ET.SubElement(items, "AndroidLibrary", Include=artifact["file"],
                      Bind=str(artifact["bind"]).lower(), Pack="false")
    for path in support:
        reference = ET.SubElement(items, "Reference", Include=Path(path).stem)
        ET.SubElement(reference, "HintPath").text = path
    ET.indent(project)
    ET.ElementTree(project).write(directory / "Inputs.csproj", encoding="unicode")
    bootstrap = corpus.execute(
        [tools["dotnet"], "build", "Inputs.csproj", "-c", tools["configuration"], "-v:minimal", "-nr:false"],
        directory, directory / "sdk-input-build.log",
    )
    intermediate = directory / f"obj/{tools['configuration']}/{tools['framework']}"
    source = intermediate / "api.xml.class-parse"
    response = intermediate / "generated/src/generator.rsp"
    if not source.exists() or not response.exists():
        raise corpus.CorpusInputError(f"Closure input preparation failed: {bootstrap['log']}")
    references = list(dict.fromkeys(
        option.removeprefix("--ref=") for option in shlex.split(response.read_text())
        if option.startswith("--ref=")
    ))
    if any(published_android_binding(Path(path).stem) for path in references):
        raise corpus.CorpusInputError("A published AndroidX/GMS binding leaked into the regenerated closure.")
    summary = {
        "inputs": manifest, "sdk_preparation": bootstrap, "raw_api_sha256": corpus.digest(source),
        "reference_hashes": {path: corpus.digest(Path(path)) for path in references},
        "modes": {},
    }
    print(f"Evidence: {directory}", flush=True)
    for mode in ("legacy", "semantic"):
        output = directory / mode
        output.mkdir()
        command = [
            tools["dotnet"], tools["generator"], "@" + str(response), str(source),
            "--csdir=" + str(output), "--xml-adjuster-output=" + str(output / "api.xml"),
            "--type-map-report=" + str(output / "type-map.txt"),
        ]
        if mode == "semantic":
            command.append("--experimental-semantic-model")
            if args.kotlin_runtime:
                command.append("--experimental-kotlin-async")
        generation = corpus.execute(command, directory, output / "generation.log")
        data = {"generation": generation}
        summary["modes"][mode] = data
        corpus.save(directory / "comparison.json", summary)
        if generation["exit_code"] != 0:
            continue
        sources = sorted(output.glob("*.cs"))
        if not sources:
            raise corpus.CorpusInputError(f"{mode} generated no closure sources.")
        arguments = [
            "-nologo", "-nostdlib+", "-target:library", "-unsafe", "-nullable:enable",
            "-langversion:preview", "-out:" + str(output / "BindingClosure.dll"),
        ] + ["-r:" + path for path in references] + [str(path) for path in sources]
        compiler_response = output / "compile.rsp"
        compiler_response.write_text("\n".join('"' + argument + '"' for argument in arguments) + "\n")
        data["compilation"] = corpus.execute(
            [tools["dotnet"], tools["csc"], "-noconfig", "@" + str(compiler_response)],
            directory, output / "compilation.log",
        )
        inventory = comparison.inventory(output)
        corpus.save(output / "inventory.json", inventory)
        data["registered_counts"] = {key: len(value) for key, value in inventory.items()}
        corpus.save(directory / "comparison.json", summary)
        print(f"{mode}: {data['compilation']['exit_code']}, {data['compilation']['error_codes']}", flush=True)
    if summary["modes"]["semantic"].get("compilation", {}).get("exit_code") != 0:
        return 1
    if args.device_serial:
        device_probe(directory / "comparison.json", args.device_serial, args.probe_configuration, args.probe_nativeaot)
    return 0


if __name__ == "__main__":
    sys.exit(main())
