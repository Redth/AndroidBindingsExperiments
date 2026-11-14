#!/usr/bin/env python3
"""Bounded third-party SDK generation against the existing canonical reference tree."""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import time
import xml.etree.ElementTree as ET

FILES = Path(__file__).resolve().parent
ROOT = Path("${ANDROID_SOURCE}")
OUTPUT = FILES / "third-party-sdk-tests"
spec = importlib.util.spec_from_file_location("third_party_inputs", ROOT / "tests/BindingProjection/Corpus/prepare-inputs.py")
if spec is None or spec.loader is None:
    raise RuntimeError("Cannot load the native input audit.")
inputs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inputs)
PROFILES = {
    "maplibre-opengl": {"groups": ["org.maplibre"], "roots": [
        "org.maplibre.gl:android-sdk-opengl:13.7.1",
    ]},
    "maplibre": {"groups": ["org.maplibre"], "roots": [
        "org.maplibre.gl:android-sdk:13.7.1",
    ]},
    "sentry": {"groups": ["io.sentry"], "roots": [
        "io.sentry:sentry-android:8.62.0",
        "io.sentry:sentry-compose-android:8.62.0",
        "io.sentry:sentry-kotlin-extensions:8.62.0",
    ]},
    "koin": {"groups": ["io.insert-koin"], "roots": [
        "io.insert-koin:koin-core:4.2.2",
        "io.insert-koin:koin-android:4.2.2",
        "io.insert-koin:koin-androidx-compose:4.2.2",
        "io.insert-koin:koin-android-compat:4.2.2",
    ]},
    "revenuecat": {"groups": ["com.revenuecat.purchases"], "roots": [
        "com.revenuecat.purchases:purchases:11.0.1",
        "com.revenuecat.purchases:purchases-ui:11.0.1",
        "com.revenuecat.purchases:purchases-store-amazon:11.0.1",
        "com.revenuecat.purchases:purchases-store-galaxy:11.0.1",
        "com.revenuecat.purchases:purchases-debug-view:11.0.1",
        "com.revenuecat.purchases:purchases-admob:11.0.1",
    ]},
    "revenuecat-noop": {"groups": ["com.revenuecat.purchases"], "roots": [
        "com.revenuecat.purchases:purchases-debug-view-noop:11.0.1",
    ]},
    "revenuecat-custom": {"groups": ["com.revenuecat.purchases"], "roots": [
        "com.revenuecat.purchases:purchases-custom-entitlement-computation:11.0.1",
    ]},
    "revenuecat-nextgen": {"groups": ["com.revenuecat.purchases"], "roots": [
        "com.revenuecat.purchases:purchases-admob-next-gen:11.0.1",
    ]},
    "mapbox": {"groups": ["com.mapbox"], "repository": "https://api.mapbox.com/downloads/v2/releases/maven", "roots": [
        "com.mapbox.maps:android-ndk27:11.34.1",
        "com.mapbox.extension:maps-compose-ndk27:11.34.1",
    ]},
    "stripe": {"groups": ["com.stripe"], "roots": ["com.stripe:stripe-android:23.25.1"]},
    "coil": {"groups": ["io.coil-kt.coil3"], "roots": [
        "io.coil-kt.coil3:coil:3.6.3",
        "io.coil-kt.coil3:coil-compose:3.6.3",
        "io.coil-kt.coil3:coil-network-okhttp:3.6.3",
    ]},
    "networking": {"groups": ["com.squareup.okhttp3", "com.squareup.okio", "com.squareup.retrofit2", "com.squareup.moshi"], "roots": [
        "com.squareup.retrofit2:retrofit:3.0.0",
        "com.squareup.retrofit2:converter-moshi:3.0.0",
        "com.squareup.okhttp3:okhttp:5.5.1",
        "com.squareup.moshi:moshi:1.15.2",
    ]},
}


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n")


def execute(command, log):
    started = time.monotonic()
    with log.open("w") as stream:
        result = subprocess.run([str(value) for value in command], cwd=ROOT, stdout=stream, stderr=subprocess.STDOUT)
    return {"command": [str(value) for value in command], "exitCode": result.returncode,
            "seconds": time.monotonic() - started, "log": str(log)}


def owned(group, profile):
    return any(group == prefix or group.startswith(prefix + ".") for prefix in profile["groups"])


def prepare(name):
    profile = PROFILES[name]
    directory = OUTPUT / name
    directory.mkdir(parents=True, exist_ok=True)
    config = [{"artifacts": [dict(zip(("groupId", "artifactId", "version"), root.split(":")))
                              for root in profile["roots"]]}]
    save(directory / "config.json", config)
    project = directory / "gradle"
    project.mkdir(exist_ok=True)
    source = ROOT / "tests/BindingProjection/Corpus/Native/build.gradle.kts"
    link = project / "build.gradle.kts"
    if not link.exists():
        link.symlink_to(source)
    settings = f"""pluginManagement {{
    apply(from = "{ROOT}/eng/gradle/plugin-repositories.gradle", to = this)
}}
dependencyResolutionManagement {{
    apply(from = "{ROOT}/eng/gradle/dependency-repositories.gradle", to = this)
"""
    if profile.get("repository"):
        settings += """    repositories {
        maven {
            url = uri(providers.gradleProperty("sdkRepository").get())
            content { includeGroupByRegex("com\\\\.mapbox(\\\\..*)?") }
        }
    }
"""
    settings += f"""}}
rootProject.name = "third-party-{name}"
include(":jvm-api-descriptor")
project(":jvm-api-descriptor").projectDir = file("{ROOT}/external/Java.Interop/tools/jvm-api-descriptor")
"""
    (project / "settings.gradle.kts").write_text(settings)
    command = [ROOT / "build-tools/gradle/gradlew", "--no-daemon", "--console", "plain", "-p", project,
               "nativeInputs", "--write-locks",
               "-PbindingCorpusConfig=" + str(directory / "config.json"),
               "-PbindingCorpusOutput=" + str(directory / "resolved")]
    if profile.get("repository"):
        command.append("-PsdkRepository=" + profile["repository"])
    result = execute(command, directory / "resolve.log")
    return directory, result


def project(directory, profile, artifacts, independent=False):
    old = FILES / "consolidated-corpus/tree/assemblies"
    owners = json.loads((old / "ownership.json").read_text())
    excluded = {row["Assembly"] for row in owners["Assemblies"] if owned(row["MavenGroup"], profile)}
    if independent:
        excluded.update(row["Assembly"] for row in owners["Assemblies"])
    baseline = {(row["group"], row["artifact"]): row for row in
                json.loads((FILES / "consolidated-corpus/tree/native-inputs.json").read_text())}
    changed = {row["group"] for row in artifacts if (row["group"], row["artifact"]) in baseline and
               row["sha256"] != baseline[(row["group"], row["artifact"])]["sha256"]}
    excluded.update(row["Assembly"] for row in owners["Assemblies"] if row["MavenGroup"] in changed)
    while True:
        dependents = {row["Assembly"] for row in owners["Assemblies"]
                      if any(reference in excluded for reference in row["References"])}
        if dependents.issubset(excluded):
            break
        excluded.update(dependents)
    references = directory / "references"
    references.mkdir(exist_ok=True)
    for path in references.glob("*.dll"):
        if not path.is_symlink():
            raise RuntimeError("Unexpected staged reference: " + str(path))
        path.unlink()
    for assembly in sorted(old.glob("*.dll")):
        if assembly.stem not in excluded and assembly.stem != "Microsoft.Android.Kotlin":
            (references / assembly.name).symlink_to(assembly)
    tree = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(tree, "PropertyGroup")
    for key, value in {
        "TargetFramework": "net11.0-android", "AssemblyName": "ThirdParty." + directory.name.replace("-", "."),
        "IsBindingProject": "true", "AllowUnsafeBlocks": "true", "Nullable": "enable",
        "UseSharedCompilation": "false", "EnableDefaultCompileItems": "false", "RunAnalyzersDuringBuild": "false",
        "EnableDefaultAndroidItems": "false", "AndroidGenerateResourceDesigner": "false",
        "_AndroidExperimentalSemanticModel": "true", "_AndroidExperimentalGenericFacades": "true",
        "_AndroidExperimentalKotlinAsync": "true", "_AndroidExperimentalApiDescriptor": str(directory / "api-descriptor.json"),
    }.items():
        ET.SubElement(properties, key).text = value
    items = ET.SubElement(tree, "ItemGroup")
    for row in artifacts:
        ET.SubElement(items, "AndroidLibrary", Include=row["file"], Bind="true", Pack="false")
    for assembly in sorted(references.glob("*.dll")):
        reference = ET.SubElement(items, "Reference", Include=str(assembly))
        ET.SubElement(reference, "Private").text = "false"
    reference = ET.SubElement(items, "Reference",
        Include=str(ROOT / "src/Microsoft.Android.Kotlin/bin/Debug/net11.0-android/Microsoft.Android.Kotlin.dll"))
    ET.SubElement(reference, "Private").text = "false"
    ET.indent(tree)
    ET.ElementTree(tree).write(directory / "Bindings.csproj", encoding="unicode")
    return excluded


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", action="append", choices=PROFILES)
    parser.add_argument("--resolve-only", action="store_true")
    parser.add_argument("--reuse-resolution", action="store_true")
    parser.add_argument("--independent", action="store_true",
                        help="Regenerate the entire selected graph, without references to prior corpus binding DLLs.")
    args = parser.parse_args()
    OUTPUT.mkdir(exist_ok=True)
    save(OUTPUT / "matrix.json", PROFILES)
    failed = False
    for name in args.profile or PROFILES:
        profile = PROFILES[name]
        directory = OUTPUT / name
        result = json.loads((directory / "result.json").read_text()) if (directory / "result.json").is_file() else {}
        result.update({"profile": name, "roots": profile["roots"],
                  "policy": "Vendor groups, version-changed shared groups, and managed dependents of replaced owners are regenerated without per-library transforms. Compatible remaining canonical JNI owners are reused; this is an isolated validation profile, not a merged distribution."}
        )
        if not args.reuse_resolution:
            print("Resolving", name, flush=True)
            directory, result["resolution"] = prepare(name)
            save(directory / "result.json", result)
            if result["resolution"]["exitCode"]:
                print("Resolution failed:", name, flush=True)
                failed = True
                continue
        artifacts = json.loads((directory / "resolved/native-inputs.json").read_text())
        result["nativeArchives"] = len(artifacts)
        audit = inputs.analyze(artifacts)
        save(directory / "native-audit.json", audit)
        result["nativeCoherent"] = audit["coherent"]
        if not audit["coherent"]:
            save(directory / "result.json", result)
            print(name, "has native conflicts:", len(audit["conflicts"]), flush=True)
            failed = True
            continue
        if args.resolve_only:
            save(directory / "result.json", result)
            print(name, "resolved", len(artifacts), "archives", flush=True)
            continue
        baseline = {(row["group"], row["artifact"]): row for row in
                    json.loads((FILES / "consolidated-corpus/tree/native-inputs.json").read_text())}
        result["sharedNativeVersionDifferences"] = [{
            "coordinate": row["group"] + ":" + row["artifact"],
            "baseline": baseline[(row["group"], row["artifact"])]["version"], "selected": row["version"],
        } for row in artifacts if (row["group"], row["artifact"]) in baseline and
            row["sha256"] != baseline[(row["group"], row["artifact"])]["sha256"] and not owned(row["group"], profile)]
        print("Describing", name, flush=True)
        descriptor = [Path(os.environ["JAVA_HOME"]) / "bin/java", "-Xmx3g", "-cp",
                      str(ROOT / "external/Java.Interop/tools/jvm-api-descriptor/build/install/jvm-api-descriptor/lib") + "/*",
                      "net.dot.android.descriptor.ApiDescriptorKt", "--manifest", directory / "resolved/native-inputs.json",
                      "--output", directory / "api-descriptor.json"]
        result["descriptor"] = execute(descriptor, directory / "descriptor.log")
        if result["descriptor"]["exitCode"]:
            save(directory / "result.json", result)
            failed = True
            continue
        result["independentNativeGraph"] = args.independent
        result["excludedCanonicalAssemblies"] = sorted(project(directory, profile, artifacts, args.independent))
        print("Building", name, flush=True)
        result["build"] = execute([ROOT / "dotnet-local.sh", "build", directory / "Bindings.csproj",
                                   "-c", "Debug", "-v:q", "-nr:false"], directory / "build.log")
        text = (directory / "build.log").read_text()
        errors = sorted(set(line.strip() for line in text.splitlines() if re.search(r": error [A-Z]+\d+:", line)))
        primary_errors = [line for line in errors if ": error XABGN0000:" not in line]
        if primary_errors:
            errors = primary_errors
        result["errors"] = errors
        result["generatedSources"] = len(list((directory / "obj/Debug/net11.0-android/generated/src").glob("*.cs")))
        save(directory / "result.json", result)
        failed |= result["build"]["exitCode"] != 0
        print(name, "PASS" if result["build"]["exitCode"] == 0 else "FAIL", len(errors), "distinct errors", flush=True)
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(main())
