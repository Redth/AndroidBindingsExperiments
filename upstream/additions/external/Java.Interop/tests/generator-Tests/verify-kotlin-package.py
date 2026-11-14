#!/usr/bin/env python3
"""Verify the optional package and compile its source in one resolved Gradle graph."""

import argparse
import hashlib
import importlib
import json
import os
from pathlib import Path
import subprocess
import time
import uuid
import xml.etree.ElementTree as ET
import zipfile


comparison = importlib.import_module("compare-bindings")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--fixture-jar", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--device-serial", required=True)
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[4]
    directory = args.output.resolve() / ("package-" + uuid.uuid4().hex[:12])
    directory.mkdir(parents=True)
    package = args.package.resolve()
    extracted = directory / "package"
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        if any(name.endswith((".aar", ".jar")) for name in names):
            raise RuntimeError("The support package must ship native source, not library binaries.")
        expected = "buildTransitive/Microsoft.Android.Kotlin.props"
        if expected not in names:
            raise RuntimeError("Missing canonical buildTransitive props.")
        nuspec = ET.fromstring(archive.read("Microsoft.Android.Kotlin.nuspec"))
        metadata = next(child for child in nuspec if child.tag.endswith("metadata"))
        version = next(child.text for child in metadata if child.tag.endswith("version"))
        dependencies = [node.get("id") for node in nuspec.iter() if node.tag.endswith("dependency")]
        if dependencies:
            raise RuntimeError(f"The opaque runtime unexpectedly depends on managed packages: {dependencies}")
        archive.extractall(extracted)
    native = extracted / "native"
    source_hashes = {str(path.relative_to(native)): digest(path) for path in native.rglob("*") if path.is_file()}
    graph = directory / "graph"
    graph.mkdir()
    config = (native / "gradle").as_posix()
    (graph / "settings.gradle").write_text(f"""
pluginManagement {{
    apply from: '{config}/plugin-repositories.gradle', to: pluginManagement
    plugins {{ id 'org.jetbrains.kotlin.jvm' version '2.4.21' }}
}}
dependencyResolutionManagement {{
    apply from: '{config}/dependency-repositories.gradle', to: dependencyResolutionManagement
}}
rootProject.name = 'kotlin-package-consumer'
include ':runtime'
project(':runtime').projectDir = file('{native.as_posix()}')
""")
    (graph / "build.gradle").write_text("""
plugins { id 'java-library' }
dependencies {
    implementation project(':runtime')
    implementation 'org.jetbrains.kotlin:kotlin-stdlib:2.4.0'
    implementation 'org.jetbrains.kotlinx:kotlinx-coroutines-core-jvm:1.9.0'
}
tasks.register('nativeInputs') {
    dependsOn ':runtime:jar'
    doLast {
        def values = configurations.runtimeClasspath.resolvedConfiguration.resolvedArtifacts.collect {
            [group: it.moduleVersion.id.group, artifact: it.moduleVersion.id.name,
             version: it.moduleVersion.id.version, file: it.file.absolutePath]
        }
        file('native-inputs.json').text = groovy.json.JsonOutput.prettyPrint(groovy.json.JsonOutput.toJson(values))
    }
}
""")
    gradle = comparison.run(
        [str(repo / "build-tools/gradle/gradlew"), "--no-daemon", "--project-cache-dir",
         str(directory / "gradle-cache"), "-p", str(graph), "nativeInputs"],
        directory, directory / "native-source-build.log",
    )
    if gradle["exit_code"] != 0:
        raise RuntimeError(f"Native source graph failed: {gradle['log']}")
    if source_hashes != {str(path.relative_to(native)): digest(path) for path in native.rglob("*") if path.is_file()}:
        raise RuntimeError("Building the included source project modified its package directory.")
    incremental = comparison.run(
        gradle["command"], directory, directory / "native-source-incremental.log")
    if incremental["exit_code"] != 0 or ":runtime:compileKotlin UP-TO-DATE" not in Path(incremental["log"]).read_text():
        raise RuntimeError(f"The unchanged native source project did not build incrementally: {incremental['log']}")
    inputs = json.loads((graph / "native-inputs.json").read_text())
    selected = [item for item in inputs if item["group"] == "org.jetbrains.kotlin" and item["artifact"] == "kotlin-stdlib"]
    if len(selected) != 1 or selected[0]["version"] != "2.4.0":
        raise RuntimeError("The bridge did not participate in the consumer's unified Kotlin version selection.")
    sdk = os.environ.get("ANDROID_HOME")
    if not sdk:
        raise RuntimeError("Set ANDROID_HOME for package device validation.")
    adb = [str(Path(sdk) / "platform-tools/adb"), "-s", args.device_serial]
    abi = subprocess.check_output(adb + ["shell", "getprop", "ro.product.cpu.abi"], text=True).strip()
    runtime = {"arm64-v8a": "android-arm64", "x86_64": "android-x64"}[abi]
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for key, value in {
        "TargetFramework": "net11.0-android", "OutputType": "Exe", "RuntimeIdentifier": runtime,
        "SupportedOSPlatformVersion": "24", "Nullable": "enable", "AllowUnsafeBlocks": "true",
        "EnableDefaultCompileItems": "false", "EmbedAssembliesIntoApk": "true",
        "ApplicationId": "com.dotnet.bindingcompiler.kotlinpackage",
        "RestoreAdditionalProjectSources": str(package.parent),
    }.items():
        ET.SubElement(properties, key).text = value
    items = ET.SubElement(project, "ItemGroup")
    ET.SubElement(items, "PackageReference", Include="Microsoft.Android.Kotlin", Version=version)
    for item in inputs:
        ET.SubElement(items, "AndroidLibrary", Include=item["file"], Bind="false")
    ET.SubElement(items, "AndroidLibrary", Include=str(args.fixture_jar.resolve()), Bind="false")
    fixture = Path(__file__).parent / "TestInputs/SemanticDeviceProbe/KotlinPackageProbeActivity.cs"
    ET.SubElement(items, "Compile", Include=str(fixture))
    identity = uuid.uuid4().hex
    (directory / "ProbeRunIdentity.cs").write_text(
        f'namespace BindingCompilerSpike {{ static class ProbeRunIdentity {{ public const string Value = "{identity}"; }} }}\n')
    ET.SubElement(items, "Compile", Include=str(directory / "ProbeRunIdentity.cs"))
    target = ET.SubElement(project, "Target", Name="VerifySourcePackage", BeforeTargets="PrepareForBuild")
    ET.SubElement(target, "Error",
                  Condition="!Exists('$(MicrosoftAndroidKotlinNativeProjectDirectory)/src/main/kotlin/net/dot/android/kotlin/CoroutineBridge.kt')",
                  Text="The package did not expose its canonical native source directory.")
    ET.indent(project)
    ET.ElementTree(project).write(directory / "Consumer.csproj", encoding="unicode")
    build = comparison.run(
        [str(repo / "dotnet-local.sh"), "build", "Consumer.csproj", "-t:Install", "-c", "Debug",
         "-p:AdbTarget=-s " + args.device_serial, "-v:minimal"],
        directory, directory / "build-install.log",
    )
    if build["exit_code"] != 0:
        raise RuntimeError(f"Package consumer build failed: {build['log']}")
    launch = comparison.run(adb + ["shell", "am", "start", "-W", "-n",
        "com.dotnet.bindingcompiler.kotlinpackage/net.dot.android.bindingcompiler.KotlinPackageProbeActivity"],
        directory, directory / "launch.log")
    if launch["exit_code"] != 0 or "Error" in Path(launch["log"]).read_text():
        raise RuntimeError(f"Package consumer launch failed: {launch['log']}")
    results = []
    for _ in range(120):
        logs = subprocess.check_output(adb + ["logcat", "-d", "-s", "KotlinPackageProbe:V", "*:S"], text=True)
        results = [line.split(identity + " ", 1)[1] for line in logs.splitlines() if identity + " " in line]
        if any(line.startswith(("DONE ", "FAIL ")) for line in results):
            break
        time.sleep(0.5)
    evidence = {
        "package": str(package), "package_sha256": digest(package), "native_source_build": gradle,
        "native_source_hashes": source_hashes, "native_incremental_build": incremental,
        "native_inputs": inputs, "native_hashes": {item["file"]: digest(Path(item["file"])) for item in inputs},
        "build": build, "launch": launch, "results": results, "identity": identity,
    }
    (directory / "results.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print("Evidence:", directory, flush=True)
    if not results or results[-1] != "DONE 3" or len([r for r in results if r.startswith("PASS ")]) != 3:
        raise RuntimeError("The source-only package round trip did not pass all three checks.")
    print("Source-only NuGet round trip: 3/3 passed, no Kotlin CLR dependencies.", flush=True)


if __name__ == "__main__":
    main()
