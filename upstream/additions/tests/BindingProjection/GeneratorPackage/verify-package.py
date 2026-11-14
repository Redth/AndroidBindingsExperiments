#!/usr/bin/env python3
"""Verify a restored compiler package against an unmodified installed workload."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import xml.etree.ElementTree as ET
import zipfile


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--feed", type=Path, required=True)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--jdk", type=Path, required=True)
    parser.add_argument("--android-sdk", type=Path, required=True)
    parser.add_argument("--package-version", default="0.1.0-preview.1")
    parser.add_argument("--upgrade-version")
    parser.add_argument("--source", action="append", default=[])
    parser.add_argument("--serial", help="Explicit device serial; omit for host-only checks.")
    parser.add_argument("--rid", default="android-arm64")
    args = parser.parse_args()
    work = args.work_dir.resolve()
    require(not work.exists(), "Use a new, empty session-owned --work-dir.")
    work.mkdir(parents=True)
    fixture = Path(__file__).resolve().parent
    for directory in ("Binding", "Consumer", "Native", "Multi", "App"):
        shutil.copytree(fixture / directory, work / directory)
    shutil.copy2(fixture / "PackageTasks.proj", work)
    environment = dict(os.environ, DOTNET_gcServer="0")
    results = []

    def run(name, arguments, expected=0):
        command = [str(part) for part in arguments]
        started = time.monotonic()
        completed = subprocess.run(command, cwd=work, env=environment,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, check=False)
        (work / f"{name}.log").write_text(completed.stdout)
        results.append({"name": name, "command": command, "exitCode": completed.returncode,
                        "seconds": round(time.monotonic() - started, 3)})
        (work / "results.json").write_text(json.dumps({"status": "in-progress", "results": results}, indent=2) + "\n")
        require((completed.returncode == 0) if expected == 0 else (completed.returncode != 0),
                f"{name} failed unexpectedly; see {work / (name + '.log')}\n" +
                "\n".join(line[:400] for line in completed.stdout.splitlines()[-12:]))
        return completed.stdout

    package = args.feed.resolve() / f"Microsoft.Android.BindingGenerator.Semantic.{args.package_version}.nupkg"
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        for name in ("build/Microsoft.Android.BindingGenerator.Semantic.targets",
                     "build/Microsoft.Android.BindingGenerator.Semantic.Bindings.targets",
                     "tools/tasks/Microsoft.Android.BindingGenerator.Semantic.dll",
                     "tools/generator/generator.dll", "tools/parser/class-parse.dll",
                     "tools/descriptor/jvm-api-descriptor.jar", "THIRD-PARTY-NOTICES.TXT"):
            require(name in names, f"Missing packaged file: {name}")
        require(not any(name.startswith(("lib/", "ref/", "buildTransitive/")) for name in names),
                "Build-only compiler assets must not flow to consumers.")
        require(not any(name.endswith(".jar") and not name.startswith("tools/") for name in names),
                "Descriptor JARs must stay in private tools.")
        require(not any(name.startswith("tools/tasks/Microsoft.Build.") for name in names),
                "Do not ship MSBuild host assemblies beside the task.")

    extension = ".exe" if os.name == "nt" else ""
    classes = work / "classes"
    classes.mkdir()
    run("javac", [args.jdk / "bin" / ("javac" + extension), "-parameters", "-d", classes,
                  *sorted((work / "Native").rglob("*.java"))])
    run("jar", [args.jdk / "bin" / ("jar" + extension), "cf", work / "Binding/preview.jar",
                "-C", classes, "."])
    run("sdk-version", [args.dotnet, "--version"])
    properties = [f"-p:AndroidSdkDirectory={args.android_sdk}",
                  f"-p:JavaSdkDirectory={args.jdk}",
                  f"-p:GeneratorPackageVersion={args.package_version}"]
    sources = ["--source", str(args.feed.resolve())]
    for source in args.source:
        sources += ["--source", source]

    def build(name, project="Consumer/Preview.Consumer.csproj", extra=(), restore=False):
        return run(name, [args.dotnet, "build", project, "-c", "Debug", "-nr:false", "-v:normal",
                          *([] if restore else ["--no-restore"]), *(sources if restore else []),
                          *properties, *extra, f"-bl:{name}.binlog"])

    build("semantic", restore=True)
    query = run("sdk-paths", [args.dotnet, "msbuild", "Binding/Preview.Binding.csproj", "-nologo",
                            "-t:_ResolveMonoAndroidSdks",
                            "-getProperty:AndroidNETSdkVersion,MonoAndroidToolsDirectory,_AndroidSemanticBindingTargetsImported",
                            *properties])
    sdk_properties = json.loads(query[query.index("{"):])["Properties"]
    require(sdk_properties["_AndroidSemanticBindingTargetsImported"].lower() == "true",
            "Package must import after the Android SDK.")
    tools = Path(sdk_properties["MonoAndroidToolsDirectory"])
    sdk_files = [tools / name for name in ("Xamarin.Android.Build.Tasks.dll",
                 "Xamarin.Android.Bindings.Core.targets", "Xamarin.Android.Bindings.ClassParse.targets",
                 "generator.dll", "class-parse.dll")]
    sdk_hashes = {str(path): digest(path) for path in sdk_files}
    base = work / "Binding/obj/Debug/net11.0-android/semantic-bindings"
    manifest = next(base.rglob("Preview.Binding.projitems"))
    root = manifest.parents[2]
    api = root / "api.xml"
    descriptor = root / "api-descriptor.json"
    require(ET.parse(api).getroot().get("api-source") == "class-parse", "Generator needs raw parser facts.")
    require(list(root.rglob("*.semantics.json")), "Missing semantic compilation report.")
    generated = list((root / "generated").rglob("*.cs"))
    require(any("RenamedEcho" in path.read_text() for path in generated), "Metadata was not applied.")
    require(any("namespace PackagePreview" in path.read_text() for path in generated), "Namespace replacement was not applied.")
    keep_rules = root / "generated/semantic-binding.pro"
    require("-keep class net.dot.android.packagetest.Box {" in keep_rules.read_text(), "Missing JNI view retention rules.")
    aars = list((work / "Binding/bin/Debug/net11.0-android").glob("*.aar"))
    require(len(aars) == 1, f"Expected SDK binding AAR: {aars}")
    with zipfile.ZipFile(aars[0]) as archive:
        require(any("-keep class net.dot.android.packagetest.Box {" in archive.read(name).decode()
                    for name in archive.namelist() if "proguard" in name),
                "Binding retention rules did not enter the SDK AAR.")
    times = {path: path.stat().st_mtime_ns for path in (api, descriptor, manifest, *generated)}
    incremental = build("incremental")
    require("Semantic binding tool:" not in incremental, "No-op build reran compiler tools.")
    require(all(path.stat().st_mtime_ns == stamp for path, stamp in times.items()), "No-op build rewrote generated output.")

    generated[0].unlink()
    build("missing-source")
    require(generated[0].exists(), "Deleting one generated source must regenerate it.")
    descriptor_time = descriptor.stat().st_mtime_ns
    time.sleep(1.1)
    os.utime(work / "Binding/Metadata.xml", None)
    changed = build("metadata-change")
    require("Semantic binding tool:" in changed, "Metadata timestamp change did not regenerate.")
    require(descriptor.stat().st_mtime_ns == descriptor_time, "Metadata-only change reran native descriptor.")
    time.sleep(1.1)
    os.utime(work / "Binding/preview.jar", None)
    changed = build("native-change")
    require("descriptor.ApiDescriptorKt" in changed or "-classpath" in changed, "Native change did not regenerate descriptor.")
    legacy = build("legacy", extra=["-p:AndroidBindingGenerator=Legacy"])
    require("Semantic binding tool:" not in legacy, "Legacy selected the package compiler.")
    build("semantic-again")
    invalid = run("invalid-mode", [args.dotnet, "build", "Binding/Preview.Binding.csproj", "--no-restore",
                                  "-nr:false", "-p:AndroidBindingGenerator=Invalid", *properties], expected=1)
    require("XA4350" in invalid, "Invalid selection needs an explicit diagnostic.")
    build("multitarget", "Multi/Preview.Multi.csproj", restore=True, extra=["-m:2"])
    require((work / "Multi/bin/Debug/net10.0/Preview.Multi.dll").exists(), "Non-Android inner build was not preserved.")
    require((work / "Multi/bin/Debug/net11.0-android/Preview.Multi.dll").exists(), "Android inner build did not bind.")
    run("design-time", [args.dotnet, "msbuild", "Binding/Preview.Binding.csproj", "-t:Compile",
                       "-p:DesignTimeBuild=true", "-p:BuildingInsideVisualStudio=true",
                       "-p:SkipCompilerExecution=true", "-nr:false", *properties])

    assets = json.loads((work / "Binding/obj/project.assets.json").read_text())
    package_root = Path(next(iter(assets["packageFolders"])))
    task_assembly = package_root / "microsoft.android.bindinggenerator.semantic" / args.package_version / "tools/tasks/Microsoft.Android.BindingGenerator.Semantic.dll"
    run("strict-package-tasks", [args.dotnet, "msbuild", "PackageTasks.proj", "-t:Check", "-m:2", "-mt",
                               "-nr:false", "-warnaserror:MSB4286,MSB4287,MSB4288",
                               f"-p:PackageTaskAssembly={task_assembly}", f"-p:DotnetHost={args.dotnet}",
                               f"-p:BindingManifest={manifest}",
                               f"-p:BindingOwnershipReport={root / 'generated/ownership.json'}"])
    runtime_package = args.feed.resolve() / "Microsoft.Android.Kotlin.0.1.0-preview.1.nupkg"
    if runtime_package.exists():
        build("runtime-reference", extra=["-p:KotlinProjections=true"], restore=True)
        require(list(base.rglob("*View*.cs")), "Updated runtime reference did not enable generic views.")
        consumer_assets = json.loads((work / "Consumer/obj/project.assets.json").read_text())
        require("Microsoft.Android.Kotlin/0.1.0-preview.1" in consumer_assets["libraries"],
                "Required runtime dependency did not reach downstream consumer.")
        require("Microsoft.Android.BindingGenerator.Semantic/" + args.package_version not in consumer_assets["libraries"],
                "Private compiler package leaked to downstream consumer.")
    if args.upgrade_version:
        build("package-upgrade", extra=[f"-p:GeneratorPackageVersion={args.upgrade_version}"], restore=True)
        require(len(list(base.iterdir())) >= 2, "Package upgrade reused the previous configuration's output directory.")
    device_results = []
    if args.serial:
        require(runtime_package.exists(), "Device projection tests require the local Kotlin runtime package.")
        repo = fixture.parents[2]
        wrapper = repo / "build-tools/gradle" / ("gradlew.bat" if os.name == "nt" else "gradlew")
        gradle_environment = environment
        environment = dict(environment, JAVA_HOME=str(args.jdk), ANDROID_HOME=str(args.android_sdk))
        native_source = package_root / "microsoft.android.kotlin/0.1.0-preview.1/native"
        source_hashes = {str(path): digest(path) for path in native_source.rglob("*") if path.is_file()}
        run("native-runtime", [*([] if os.name == "nt" else ["bash"]), wrapper, "--no-daemon", "--console", "plain",
                              "-p", work / "Native", f"-PdotnetAndroidGradleConfig={repo / 'eng/gradle'}",
                              f"-PdotnetKotlinRuntimeSource={native_source}", "prepareRuntime"])
        after_source_hashes = {str(path): digest(path) for path in native_source.rglob("*") if path.is_file()}
        require(after_source_hashes == source_hashes, "Gradle modified the restored package directory.")
        require(not (native_source / "build").exists(), "Gradle wrote build output into the package cache.")
        adb = args.android_sdk / "platform-tools" / ("adb.exe" if os.name == "nt" else "adb")
        device = [adb, "-s", args.serial]
        run("device-ready", [*device, "shell", "getprop", "sys.boot_completed"])
        for mode, configuration, settings in (
                ("debug", "Debug", []),
                ("release-r8", "Release", ["-p:PublishTrimmed=true", "-p:TrimMode=full", "-p:AndroidLinkTool=r8"]),
                ("nativeaot", "Release", ["-p:PublishAot=true"])):
            run(f"device-{mode}-build", [args.dotnet, "build", "App/Preview.App.csproj", "-c", configuration,
                                       "-nr:false", "-v:minimal", *properties, *sources,
                                       "-p:KotlinProjections=true", f"-p:RuntimeIdentifier={args.rid}",
                                       *settings, f"-bl:device-{mode}.binlog"])
            apks = list((work / "App/bin" / configuration / "net11.0-android" / args.rid).glob("*-Signed.apk"))
            require(len(apks) == 1, f"Expected one signed APK for {mode}: {apks}")
            apk = apks[0]
            if mode == "release-r8":
                require((work / "App/obj/Release/net11.0-android" / args.rid / "r8.json").exists(),
                        "Release check did not actually run R8.")
            with zipfile.ZipFile(apk) as archive:
                require(not any("jvm-api-descriptor" in name or "BindingGenerator.Semantic" in name
                                for name in archive.namelist()), "Compiler tools entered APK payload.")
                if mode == "nativeaot":
                    require(any(name.endswith("/libPreview.App.so") for name in archive.namelist()) and
                            not any(name.endswith("/libcoreclr.so") for name in archive.namelist()),
                            "NativeAOT check packaged the JIT runtime instead of the native application.")
            run(f"device-{mode}-install", [*device, "install", "-r", apk])
            run(f"device-{mode}-stop", [*device, "shell", "am", "force-stop", "net.dot.android.packagetest.app"])
            run(f"device-{mode}-clear-log", [*device, "logcat", "-c"])
            run(f"device-{mode}-launch", [*device, "shell", "am", "start", "-W", "-n",
                                       "net.dot.android.packagetest.app/net.dot.android.packagetest.app.ProbeActivity"])
            deadline = time.monotonic() + 30
            while True:
                completed = subprocess.run([str(p) for p in [*device, "logcat", "-d"]], env=environment,
                                           stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=True)
                log = completed.stdout
                probe_lines = [line for line in log.splitlines() if "BindingPackageProbe" in line]
                if any("PASS " in line or "FAIL " in line for line in probe_lines):
                    break
                (work / f"device-{mode}-logcat.log").write_text(log)
                require(time.monotonic() < deadline, f"{mode} did not report its exact check count; inspect adb logcat.")
                time.sleep(0.5)
            (work / f"device-{mode}-logcat.log").write_text(log)
            require(any("PASS 11/11" in line for line in probe_lines) and
                    not any("FAIL " in line for line in probe_lines),
                    f"{mode} failed JNI checks; see device-{mode}-logcat.log.")
            device_results.append({"mode": mode, "checks": 11, "apkSha256": digest(apk)})
        environment = gradle_environment
    require(all(digest(Path(path)) == value for path, value in sdk_hashes.items()), "Installed workload files changed.")
    report = {"package": str(package), "sha256": digest(package), "sdk": sdk_properties,
              "unmodifiedWorkloadHashes": sdk_hashes, "results": results, "deviceResults": device_results}
    (work / "results.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({"checks": len(results), "status": "passed", "results": str(work / "results.json")}, indent=2))


if __name__ == "__main__":
    main()
