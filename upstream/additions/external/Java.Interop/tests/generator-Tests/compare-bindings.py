#!/usr/bin/env python3
"""Compare opt-in semantic planning with the unchanged binding pipeline."""

import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shlex
import statistics
import subprocess
import sys
import time
import urllib.request
import uuid
import xml.etree.ElementTree as ET
import zipfile


PILOTS = {
    "activity": ("androidx.activity", "activity", "1.11.0", "Xamarin.AndroidX.Activity", "1.11.0"),
    "fragment": ("androidx.fragment", "fragment", "1.8.9", "Xamarin.AndroidX.Fragment", "1.8.9"),
    "kotlin": ("local", "kotlin-value-classes", "fixture", "Kotlin.ValueClassFixture", "fixture"),
    "contracts": ("local", "semantic-contracts", "fixture", "Semantic.ContractFixture", "fixture"),
    "async": ("local", "kotlin-async", "fixture", "Kotlin.AsyncFixture", "fixture"),
}
ACTIVITY_SHA256 = "26726186b34f90b0fe55bbf0ebbf640ade99915bc35f4cb0153ddcf751499bd3"


def download(url, destination):
    with urllib.request.urlopen(url, timeout=120) as response:
        content = response.read()
    destination.write_bytes(content)
    return hashlib.sha256(content).hexdigest()


def run(command, cwd, log):
    start = time.monotonic()
    with log.open("w") as stream:
        result = subprocess.run(command, cwd=cwd, stdout=stream, stderr=subprocess.STDOUT)
    return {
        "exit_code": result.returncode,
        "seconds": time.monotonic() - start,
        "command": command,
        "working_directory": str(cwd),
        "log": str(log),
    }


def diagnostics(path):
    text = path.read_text()
    errors = [line for line in text.splitlines() if re.search(r": error [A-Z]+\d+:", line)]
    warnings = [line for line in text.splitlines() if re.search(r": warning [A-Z]+\d+:", line)]
    return {
        "errors": errors,
        "error_codes": dict(Counter(re.findall(r": error ([A-Z]+\d+):", text))),
        "warning_codes": dict(Counter(re.findall(r": warning ([A-Z]+\d+):", text))),
        "warning_count": len(warnings),
    }


def inventory(directory):
    types, methods, fields = set(), set(), set()
    for path in directory.glob("*.cs"):
        owner = None
        for line in path.read_text().splitlines():
            match = re.search(
                r"""Metadata.xml XPath .*reference: path="/api/package\[@name='([^']*)'\]/(?:class|interface)\[@name='([^']+)'\]""",
                line,
            )
            if match:
                owner = ".".join(part for part in match.groups() if part)
            match = re.search(r'Runtime.Register \("([^"]+)", DoNotGenerateAcw=true', line)
            if match:
                types.add(match.group(1))
            match = re.search(r'\[Register \("([^"]+)", "(\([^"]+)"', line)
            if match and owner:
                methods.add(f"{owner}.{match.group(1)}{match.group(2)}")
            match = re.search(r'\[Register \("([^"]+)"\)\]', line)
            if match and owner:
                fields.add(f"{owner}.{match.group(1)}")
    return {"types": sorted(types), "methods": sorted(methods), "fields": sorted(fields)}


def create_inputs(directory, pilot, framework, configuration, runtime_assembly=None, kotlin_classes=None):
    group, artifact, version, package, package_version = PILOTS[pilot]
    if pilot == "async":
        classes = kotlin_classes or Path(__file__).parent / f"obj/{configuration}-net10.0/kotlin-gradle/classes"
        files = sorted((classes / "net/dot/android/kotlinasync").glob("*.class"))
        if not files:
            raise RuntimeError("Build generator-Tests.csproj first to compile the Kotlin async fixture.")
        archive = directory / "kotlin-async.jar"
        with zipfile.ZipFile(archive, "w") as jar:
            for path in files:
                jar.write(path, path.relative_to(classes))
        dependencies = [
            {"id": "Xamarin.Kotlin.StdLib", "version": "2.4.0.1"},
            {"id": "Xamarin.KotlinX.Coroutines.Core.Jvm", "version": "1.9.0.3"},
            {"id": "Xamarin.KotlinX.Coroutines.Android", "version": "1.9.0.3"},
        ]
        manifest = {
            "maven_coordinate": "local:kotlin-async",
            "archive_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
            "fixture_class_sha256": {str(path.relative_to(classes)): hashlib.sha256(path.read_bytes()).hexdigest() for path in files},
            "dependency_source": "Kotlin stdlib and coroutines support bindings; fixture and bridge compiled from source",
            "dependencies": dependencies,
            "managed_runtime_source_sha256": hashlib.sha256(
                (Path(__file__).parent / "TestInputs/SemanticDeviceProbe/KotlinCoroutines.cs").read_bytes()
            ).hexdigest(),
        }
        if runtime_assembly:
            manifest["managed_runtime_assembly"] = str(runtime_assembly)
            manifest["managed_runtime_sha256"] = hashlib.sha256(runtime_assembly.read_bytes()).hexdigest()
            repo = Path(__file__).resolve().parents[4]
            archives = list((repo / f"src/Microsoft.Android.Kotlin/obj/{configuration}/{framework}/gradle").glob(
                "*/outputs/aar/Microsoft.Android.Kotlin.aar"))
            if len(archives) != 1:
                raise RuntimeError("Build Microsoft.Android.Kotlin.csproj first; expected one source-built native bridge.")
            manifest["managed_runtime_native_archive"] = str(archives[0])
            manifest["managed_runtime_native_sha256"] = hashlib.sha256(archives[0].read_bytes()).hexdigest()
    elif pilot == "contracts":
        classes = directory / "fixture-classes"
        fixture = Path(__file__).parent / "TestInputs/SemanticDeviceProbe/ContractProbe.java"
        compilation = run(
            ["javac", "--release", "8", "-d", str(classes), str(fixture)],
            directory, directory / "fixture-javac.log",
        )
        if compilation["exit_code"] != 0:
            raise RuntimeError(f"Native contract fixture compilation failed; inspect {compilation['log']}.")
        archive = directory / "semantic-contracts.jar"
        with zipfile.ZipFile(archive, "w") as jar:
            for path in sorted(classes.rglob("*.class")):
                jar.write(path, path.relative_to(classes))
        dependencies = []
        manifest = {
            "maven_coordinate": "local:semantic-contracts",
            "archive_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
            "fixture_source_sha256": hashlib.sha256(fixture.read_bytes()).hexdigest(),
            "fixture_compilation": compilation,
            "dependency_source": "Platform bindings from the locally built Android SDK",
            "dependencies": dependencies,
        }
    elif pilot == "kotlin":
        classes = Path(__file__).parent / f"obj/{configuration}-net10.0/kotlin-gradle/classes"
        archive = directory / "kotlin-value-classes.jar"
        hashes = {}
        with zipfile.ZipFile(archive, "w") as jar:
            for name in ["MyColor", "MyAlpha", "MyDp", "Widgets"]:
                path = classes / f"xat/bytecode/tests/{name}.class"
                if not path.exists():
                    raise RuntimeError("Build generator-Tests.csproj first to compile the real Kotlin fixture.")
                jar.write(path, path.relative_to(classes))
                hashes[str(path.relative_to(classes))] = hashlib.sha256(path.read_bytes()).hexdigest()
        dependencies = [{"id": "Xamarin.Kotlin.StdLib", "version": "2.2.20"}]
        manifest = {
            "maven_coordinate": "local:kotlin-value-classes",
            "archive_sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
            "fixture_class_sha256": hashes,
            "dependency_source": "Kotlin standard-library binding for reference resolution",
            "dependencies": dependencies,
        }
    else:
        archive = directory / f"{artifact}-{version}.aar"
        archive_url = f"https://dl.google.com/dl/android/maven2/{group.replace('.', '/')}/{artifact}/{version}/{archive.name}"
        archive_hash = download(archive_url, archive)
        if pilot == "activity" and archive_hash != ACTIVITY_SHA256:
            raise RuntimeError("Activity archive does not match the pinned SHA-256.")

        nuspec = directory / "dependencies.nuspec"
        nuspec_url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/{package_version}/{package.lower()}.nuspec"
        nuspec_hash = download(nuspec_url, nuspec)
        namespace = {"n": ET.fromstring(nuspec.read_bytes()).tag.split("}")[0].removeprefix("{")}
        groups = ET.parse(nuspec).findall(".//n:group", namespace)
        selected = next(g for g in groups if g.get("targetFramework") == "net10.0-android36.0")
        dependencies = [{"id": d.get("id"), "version": d.get("version")} for d in selected]
        manifest = {
            "maven_coordinate": f"{group}:{artifact}:{version}",
            "archive_url": archive_url,
            "archive_sha256": archive_hash,
            "dependency_source": f"{package}/{package_version} (published managed dependency graph, not the Maven minimum graph)",
            "nuspec_url": nuspec_url,
            "nuspec_sha256": nuspec_hash,
            "dependencies": dependencies,
        }

    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in {
        "TargetFramework": framework,
        "AssemblyName": "BindingComparison",
        "IsBindingProject": "true",
        "Nullable": "enable",
        "EnableDefaultCompileItems": "false",
        "EnableDefaultAndroidItems": "false",
        "RestorePackagesWithLockFile": "true",
    }.items():
        ET.SubElement(properties, name).text = value
    items = ET.SubElement(project, "ItemGroup")
    ET.SubElement(items, "AndroidLibrary", Include=archive.name, Bind="true", Pack="false")
    for dependency in dependencies:
        ET.SubElement(items, "PackageReference", Include=dependency["id"], Version=dependency["version"])
    if runtime_assembly:
        reference = ET.SubElement(items, "Reference", Include=runtime_assembly.stem)
        ET.SubElement(reference, "HintPath").text = str(runtime_assembly)
    ET.indent(project)
    ET.ElementTree(project).write(directory / "Inputs.csproj", encoding="unicode")
    (directory / "inputs.json").write_text(json.dumps(manifest, indent=2) + "\n")
    return manifest


def device_probe(dotnet, comparison, serial, configuration="Debug", nativeaot=False):
    summary = json.loads(comparison.read_text())
    coordinate = summary["inputs"]["maven_coordinate"]
    if coordinate == "androidx.activity:activity:1.11.0":
        source, activity, archive, expected_checks = (
            "ProbeActivity.cs", "ProbeActivity", "activity-1.11.0.aar", 13,
        )
    elif coordinate == "local:semantic-contracts":
        source, activity, archive, expected_checks = (
            "ContractProbeActivity.cs", "ContractProbeActivity", "semantic-contracts.jar", 9,
        )
    elif coordinate == "local:kotlin-async":
        source, activity, archive, expected_checks = (
            "KotlinAsyncProbeActivity.cs", "KotlinAsyncProbeActivity", "kotlin-async.jar", 25,
        )
        if "managed_runtime_assembly" in summary["inputs"]:
            activity, expected_checks = "KotlinProductionProbeActivity", 56
    else:
        raise RuntimeError("No device probe exists for this pilot.")
    if coordinate != "local:kotlin-async" and (configuration != "Debug" or nativeaot):
        raise RuntimeError("Optimized probes in this driver currently support only the async pilot.")
    if summary["modes"]["semantic"]["compilation"]["exit_code"] != 0:
        raise RuntimeError("The semantic bindings must compile before running the device probe.")
    sdk = os.environ.get("ANDROID_HOME")
    if not sdk:
        raise RuntimeError("Set ANDROID_HOME to the Android SDK containing adb.")
    adb = [str(Path(sdk) / "platform-tools/adb"), "-s", serial]
    abi = subprocess.check_output(adb + ["shell", "getprop", "ro.product.cpu.abi"], text=True).strip()
    runtime = {"arm64-v8a": "android-arm64", "x86_64": "android-x64", "armeabi-v7a": "android-arm", "x86": "android-x86"}[abi]
    directory = comparison.parent / f"device-{uuid.uuid4().hex[:8]}"
    directory.mkdir()
    identity = uuid.uuid4().hex
    identity_file = directory / "ProbeRunIdentity.cs"
    identity_file.write_text(f'namespace BindingCompilerSpike {{ static class ProbeRunIdentity {{ public const string Value = "{identity}"; }} }}\n')
    fixtures = Path(__file__).parent / "TestInputs/SemanticDeviceProbe"
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    package = "com.dotnet.bindingcompiler.kotlinasync" if coordinate == "local:kotlin-async" else "com.dotnet.bindingcompiler.spike"
    for name, value in {
        "TargetFramework": "net11.0-android",
        "SupportedOSPlatformVersion": "24",
        "OutputType": "Exe",
        "RuntimeIdentifier": runtime,
        "ApplicationId": package,
        "EnableDefaultCompileItems": "false",
        "AllowUnsafeBlocks": "true",
        "Nullable": "enable",
        "EmbedAssembliesIntoApk": "true",
        "TrimMode": "full" if configuration == "Release" else "partial",
        "AndroidLinkTool": "r8" if configuration == "Release" else "",
        "PublishAot": str(nativeaot).lower(),
    }.items():
        ET.SubElement(properties, name).text = value
    items = ET.SubElement(project, "ItemGroup")
    for dependency in summary["inputs"]["dependencies"]:
        ET.SubElement(items, "PackageReference", Include=dependency["id"], Version=dependency["version"])
    binding = Path(summary["modes"]["semantic"].get("assembly",
        str(Path(summary["modes"]["semantic"]["directory"]) / "BindingComparison.dll")))
    reference = ET.SubElement(items, "Reference", Include=binding.stem)
    ET.SubElement(reference, "HintPath").text = str(binding)
    ET.SubElement(items, "AndroidLibrary", Include=str(comparison.parent / archive), Bind="false")
    for path, digest in summary["inputs"].get("native_dependency_archives", {}).items():
        if hashlib.sha256(Path(path).read_bytes()).hexdigest() != digest:
            raise RuntimeError(f"A resolved native dependency changed: {path}")
        ET.SubElement(items, "AndroidLibrary", Include=path, Bind="false")
    ET.SubElement(items, "Compile", Include=str(fixtures / source))
    if "managed_runtime_assembly" in summary["inputs"]:
        ET.SubElement(items, "Compile", Include=str(fixtures / "KotlinProductionProbeActivity.cs"))
        ET.SubElement(items, "Compile", Include=str(fixtures / "KotlinFlowBenchmarks.cs"))
    ET.SubElement(items, "Compile", Include=str(identity_file))
    if "managed_runtime_assembly" in summary["inputs"]:
        runtime_assembly = Path(summary["inputs"]["managed_runtime_assembly"])
        native_archive = Path(summary["inputs"]["managed_runtime_native_archive"])
        if hashlib.sha256(runtime_assembly.read_bytes()).hexdigest() != summary["inputs"]["managed_runtime_sha256"]:
            raise RuntimeError("The canonical runtime changed; generate a new comparison before running the probe.")
        if hashlib.sha256(native_archive.read_bytes()).hexdigest() != summary["inputs"]["managed_runtime_native_sha256"]:
            raise RuntimeError("The canonical native bridge changed; generate a new comparison before running the probe.")
        runtime_reference = ET.SubElement(items, "Reference", Include=runtime_assembly.stem)
        ET.SubElement(runtime_reference, "HintPath").text = str(runtime_assembly)
        ET.SubElement(items, "AndroidLibrary", Include=str(native_archive), Bind="false")
    if coordinate == "androidx.activity:activity:1.11.0":
        ET.SubElement(items, "AndroidJavaSource", Include=str(fixtures / "BridgeProbe.java"), Bind="false")
    ET.indent(project)
    ET.ElementTree(project).write(directory / "DeviceProbe.csproj", encoding="unicode")
    build = run([dotnet, "build", "DeviceProbe.csproj", "-t:Install", "-c", configuration, f"-p:AdbTarget=-s {serial}", "-v:minimal"], directory, directory / "build-install.log")
    if build["exit_code"] != 0:
        raise RuntimeError(f"Device probe build/install failed; inspect {build['log']}.")
    run(adb + ["shell", "am", "force-stop", package], directory, directory / "force-stop.log")
    launch = run(adb + ["shell", "am", "start", "-W", "-n", f"{package}/net.dot.android.bindingcompiler.{activity}"], directory, directory / "launch.log")
    if launch["exit_code"] != 0 or re.search(r"^Error", Path(launch["log"]).read_text(), re.M):
        raise RuntimeError(f"Device probe launch failed; inspect {launch['log']}.")
    result = ""
    for _ in range(240):
        if coordinate == "local:kotlin-async":
            read = subprocess.run(adb + ["logcat", "-d", "-s", "BindingCompilerSpike:V", "*:S"], capture_output=True, text=True)
            lines = [line.split(identity + " ", 1)[1] for line in read.stdout.splitlines() if identity + " " in line]
            result = identity + "\n" + "\n".join(lines)
        else:
            read = subprocess.run(adb + ["shell", "run-as", package, "cat", "files/binding-compiler-results.txt"], capture_output=True, text=True)
            if read.returncode == 0 and read.stdout.startswith(identity):
                result = read.stdout
        if "\nDONE " in result or "\nFAIL " in result:
            break
        time.sleep(0.5)
    (directory / "results.txt").write_text(result)
    run(adb + ["logcat", "-d", "-s", "BindingCompilerSpike:V", "*:S"], directory, directory / "logcat.log")
    if not result.splitlines() or result.splitlines()[-1] != f"DONE {expected_checks}" or "\nFAIL " in result:
        raise RuntimeError(f"Device probe did not pass all {expected_checks} checks; inspect {directory}.")
    if coordinate == "local:kotlin-async" and len(set(line for line in result.splitlines() if line.startswith("PASS "))) != expected_checks:
        raise RuntimeError(f"Device probe did not report {expected_checks} distinct passing checks; inspect {directory}.")
    probe = {
        "serial": serial, "abi": abi, "configuration": configuration, "nativeaot": nativeaot, "build": build,
        "launch": launch, "results": result, "directory": str(directory),
        "fixture_source_sha256": hashlib.sha256((fixtures / source).read_bytes()).hexdigest(),
    }
    summary["device_probe"] = probe
    summary.setdefault("device_probes", []).append(probe)
    comparison.write_text(json.dumps(summary, indent=2) + "\n")
    print(f"Android device probe: {expected_checks}/{expected_checks} passed; evidence: {directory}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, help="Persistent comparison evidence directory.")
    parser.add_argument("--pilot", choices=PILOTS, default="activity")
    parser.add_argument("--configuration", choices=["Debug", "Release"], default="Debug")
    parser.add_argument("--framework", default="net11.0-android")
    parser.add_argument("--iterations", type=int, default=3, help="Measured generations per mode after a warm-up.")
    parser.add_argument("--device-serial", help="Also install and run the pilot's dispatch probe on this adb device.")
    parser.add_argument("--probe-comparison", type=Path, help="Run only the device probe using an existing comparison.json.")
    parser.add_argument("--probe-configuration", choices=["Debug", "Release"], default="Debug")
    parser.add_argument("--probe-nativeaot", action="store_true")
    parser.add_argument("--runtime-assembly", type=Path,
                        help="Canonical Kotlin runtime DLL; async pilot otherwise uses the original source-only prototype.")
    parser.add_argument("--kotlin-classes", type=Path, help="Precompiled async fixture classes for Kotlin compiler compatibility runs.")
    args = parser.parse_args()
    if args.iterations < 1:
        parser.error("--iterations must be positive")
    if args.probe_nativeaot and args.probe_configuration != "Release":
        parser.error("--probe-nativeaot requires --probe-configuration Release")

    repo = Path(__file__).resolve().parents[4]
    dotnet = str(repo / "dotnet-local.sh")
    if args.probe_comparison:
        if not args.device_serial:
            parser.error("--probe-comparison requires --device-serial")
        device_probe(dotnet, args.probe_comparison.resolve(), args.device_serial, args.probe_configuration, args.probe_nativeaot)
        return 0
    if not args.output:
        parser.error("--output is required for a new comparison")
    if args.pilot == "async" and not args.runtime_assembly:
        args.runtime_assembly = repo / f"src/Microsoft.Android.Kotlin/bin/{args.configuration}/{args.framework}/Microsoft.Android.Kotlin.dll"
    if args.runtime_assembly and not args.runtime_assembly.is_file():
        parser.error("Build src/Microsoft.Android.Kotlin/Microsoft.Android.Kotlin.csproj before the async comparison.")
    if args.device_serial and args.pilot not in ("activity", "contracts", "async"):
        parser.error("--device-serial supports only the Activity, native-contract and async pilots")
    tools = list((repo / f"bin/{args.configuration}/lib/packs").glob("Microsoft.Android.Sdk.*/*/tools/generator.dll"))
    if len(tools) != 1:
        raise RuntimeError("Build the local generator/Android SDK first; expected one generator tool.")
    generator = str(tools[0])
    sdk_version = subprocess.check_output([dotnet, "--version"], text=True).strip()
    sdks = subprocess.check_output([dotnet, "--list-sdks"], text=True)
    sdk_directory = next(Path(directory) / version for version, directory in re.findall(r"^(\S+) \[([^]]+)\]$", sdks, re.M) if version == sdk_version)
    csc = str(sdk_directory / "Roslyn/bincore/csc.dll")
    output = args.output.resolve() / f"{args.pilot}-{datetime.now(timezone.utc):%Y%m%dT%H%M%SZ}-{uuid.uuid4().hex[:8]}"
    output.mkdir(parents=True)
    if args.runtime_assembly and args.pilot != "async":
        parser.error("--runtime-assembly currently applies only to --pilot async")
    manifest = create_inputs(output, args.pilot, args.framework, args.configuration,
                             args.runtime_assembly.resolve() if args.runtime_assembly else None,
                             args.kotlin_classes.resolve() if args.kotlin_classes else None)

    # The SDK supplies references and raw class-parse input. Its C# compilation
    # may fail: those failures are the baseline being investigated, not success.
    bootstrap = run([dotnet, "build", "Inputs.csproj", "-c", args.configuration, "-v:minimal"], output, output / "sdk-input-build.log")
    intermediate = output / f"obj/{args.configuration}/{args.framework}"
    source = intermediate / "api.xml.class-parse"
    response = intermediate / "generated/src/generator.rsp"
    if not source.exists() or not response.exists():
        raise RuntimeError(f"SDK input preparation failed before generation; inspect {bootstrap['log']}.")
    options = shlex.split(response.read_text())
    references = [option.removeprefix("--ref=") for option in options if option.startswith("--ref=")]
    primary = PILOTS[args.pilot][3].lower()
    if any(Path(reference).stem.lower() == primary for reference in references):
        raise RuntimeError("The reference set contains the published binding being regenerated.")
    (output / "references.json").write_text(json.dumps(references, indent=2) + "\n")
    (output / "reference-hashes.json").write_text(json.dumps({
        reference: hashlib.sha256(Path(reference).read_bytes()).hexdigest() for reference in references
    }, indent=2) + "\n")

    generations = {"legacy": [], "semantic": []}
    sources = {}
    for iteration in range(args.iterations + 1):
        for mode in (["legacy", "semantic"] if iteration % 2 == 0 else ["semantic", "legacy"]):
            directory = output / f"{mode}-{iteration}"
            directory.mkdir()
            command = [
                dotnet, generator, f"@{response}", str(source),
                f"--csdir={directory}", f"--xml-adjuster-output={directory}/api.xml",
                f"--type-map-report={directory}/type-map.txt",
            ]
            if mode == "semantic":
                command.append("--experimental-semantic-model")
                if args.pilot == "async":
                    command.append("--experimental-kotlin-async")
            result = run(command, output, directory / "generation.log")
            if result["exit_code"] != 0:
                raise RuntimeError(f"{mode} generation failed; inspect {result['log']}.")
            result["warm_up"] = iteration == 0
            result.update(diagnostics(directory / "generation.log"))
            generations[mode].append(result)
            sources[mode] = directory

    summary = {
        "inputs": manifest,
        "sdk_preparation": bootstrap,
        "sdk_version": sdk_version,
        "generator_sha256": hashlib.sha256(Path(generator).read_bytes()).hexdigest(),
        "driver_sha256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "class_parse_sha256": hashlib.sha256(source.read_bytes()).hexdigest(),
        "modes": {},
    }
    for mode, directory in sources.items():
        command = [
            dotnet, csc, "-nologo", "-noconfig", "-nostdlib+", "-target:library",
            "-unsafe", "-nullable:enable", "-langversion:preview",
            f"-out:{directory}/BindingComparison.dll",
        ]
        command += [f"-r:{reference}" for reference in references]
        command += [str(path) for path in sorted(directory.glob("*.cs"))]
        if args.pilot == "async" and mode == "semantic" and not args.runtime_assembly:
            command.append(str(Path(__file__).parent / "TestInputs/SemanticDeviceProbe/KotlinCoroutines.cs"))
        compilation = run(command, output, directory / "compilation.log")
        compilation.update(diagnostics(directory / "compilation.log"))
        api = inventory(directory)
        (directory / "inventory.json").write_text(json.dumps(api, indent=2) + "\n")
        summary["modes"][mode] = {
            "directory": str(directory),
            "generations": generations[mode],
            "median_generation_seconds": statistics.median(g["seconds"] for g in generations[mode] if not g["warm_up"]),
            "compilation": compilation,
            "registered_type_count": len(api["types"]),
            "registered_method_count": len(api["methods"]),
            "registered_field_count": len(api["fields"]),
        }
        if args.pilot == "kotlin":
            text = "\n".join(path.read_text() for path in directory.glob("*.cs"))
            summary["modes"][mode]["kotlin_value_struct_count"] = text.count("readonly partial struct")
            summary["modes"][mode]["kotlin_tint_overloads"] = re.findall(r"public[^\n]* Tint \([^\n]*", text)
    legacy = inventory(sources["legacy"])
    semantic = inventory(sources["semantic"])
    summary["coverage_delta"] = {
        f"{change}_{kind}": sorted(operation(set(semantic[kind]), set(legacy[kind])))
        for kind in ["types", "methods", "fields"]
        for change, operation in [("added", set.difference), ("lost", lambda new, old: old - new)]
    }
    (output / "comparison.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(f"Evidence: {output / 'comparison.json'}")
    for mode, data in summary["modes"].items():
        print(f"{mode}: {len(data['compilation']['errors'])} C# errors, {data['registered_type_count']} types, "
              f"{data['registered_method_count']} method registrations, {data['registered_field_count']} fields, "
              f"{data['median_generation_seconds']:.3f}s median generation")
    if args.pilot != "kotlin" and any(summary["coverage_delta"][f"lost_{kind}"] for kind in ["types", "methods", "fields"]):
        print("The semantic pipeline lost registered APIs; inspect coverage_delta.", file=sys.stderr)
        return 1
    if args.pilot == "kotlin":
        semantic = summary["modes"]["semantic"]
        if semantic["kotlin_value_struct_count"] != 3 or len(semantic["kotlin_tint_overloads"]) != 3:
            print("The semantic pipeline did not preserve all three value-class structs and Tint overloads.", file=sys.stderr)
            return 1
    if args.device_serial:
        device_probe(dotnet, output / "comparison.json", args.device_serial, args.probe_configuration, args.probe_nativeaot)
    return 0 if summary["modes"]["semantic"]["compilation"]["exit_code"] == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
