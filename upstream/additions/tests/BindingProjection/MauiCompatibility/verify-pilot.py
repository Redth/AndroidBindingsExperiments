#!/usr/bin/env python3
"""Reproduce a usage-selected MAUI binding compatibility pilot with unchanged binaries."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import zipfile


def require(value, message):
    if not value:
        raise RuntimeError(message)


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--stock-dotnet", required=True)
    parser.add_argument("--jdk", type=Path, required=True)
    parser.add_argument("--android-sdk", type=Path, required=True)
    parser.add_argument("--serial", help="Explicit owned device, not the default ADB device.")
    parser.add_argument("--source", action="append", default=[])
    args = parser.parse_args()
    fixture = Path(__file__).resolve().parent
    repo = fixture.parents[2]
    root = args.work_dir.resolve()
    require(not root.exists(), "Use a fresh session-owned work directory.")
    root.mkdir(parents=True)
    environment = dict(os.environ, DOTNET_gcServer="0", DOTNET_ROLL_FORWARD="Major")
    results = []
    def run(name, command, cwd=root):
        started = time.monotonic()
        command = [str(c) for c in command]
        result = subprocess.run(command, cwd=cwd, env=environment, text=True,
                                stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
        (root / f"{name}.log").write_text(result.stdout)
        results.append({"name": name, "command": command, "cwd": str(cwd), "exitCode": result.returncode,
                        "seconds": round(time.monotonic() - started, 3)})
        (root / "progress.json").write_text(json.dumps(results, indent=2) + "\n")
        require(result.returncode == 0, f"{name} failed: {root / (name + '.log')}\n" +
                "\n".join(line[:500] for line in result.stdout.splitlines()[-12:]))
        return result.stdout

    local = repo / "dotnet-local.sh"
    local_host = repo / "bin/Debug/dotnet/dotnet"
    sources = []
    for source in args.source:
        sources += ["--source", source]
    props = [f"-p:AndroidSdkDirectory={args.android_sdk}", f"-p:JavaSdkDirectory={args.jdk}"]
    for folder in ("Baseline", "Legacy", "App", "Regenerated"):
        shutil.copytree(fixture / folder, root / folder)
    for folder in ("native", "frozen", "replacement", "generated"):
        (root / folder).mkdir()
    run("tool-build", [local, "build", "external/Java.Interop/tools/binding-compatibility/binding-compatibility.csproj",
                       "-nr:false", "-v:quiet"], repo)
    tool = repo / "external/Java.Interop/tools/binding-compatibility/bin/Debug/binding-compatibility.dll"
    run("baseline-restore", [args.stock_dotnet, "restore", "Baseline.csproj", *sources, "-v:quiet"], root / "Baseline")
    run("snapshot", [args.stock_dotnet, "msbuild", "Baseline.csproj", "-t:SnapshotInputs", "-nr:false", "-v:minimal", *props], root / "Baseline")
    refs = root / "Baseline/references"
    maui = sorted(refs.glob("Microsoft.Maui*.dll"))
    require(len(maui) >= 5, "Expected the resolved MAUI Android assembly set, not just one source API list.")
    run("full-profile", [local_host, tool, "extract", root / "maui-profile.json", refs, *maui])
    run("old-consumer", [args.stock_dotnet, "build", "Legacy.Consumer.csproj", "-c", "Debug", "-nr:false", "-v:minimal", *sources, *props], root / "Legacy")
    old_consumer = root / "frozen/Legacy.Consumer.dll"
    shutil.copy2(root / "Legacy/bin/Debug/net11.0-android/Legacy.Consumer.dll", old_consumer)
    frozen = {str(path): sha(path) for path in [old_consumer, *maui]}
    assets = json.loads((root / "Baseline/obj/project.assets.json").read_text())
    cache = Path(next(iter(assets["packageFolders"])))
    native = cache / "xamarin.androidx.swiperefreshlayout/1.2.0.3/aar/androidx.swiperefreshlayout.swiperefreshlayout.aar"
    with zipfile.ZipFile(native) as archive:
        (root / "native/swipe.jar").write_bytes(archive.read("classes.jar"))
    run("select-profile", [local_host, tool, "select", root / "maui-profile.json",
                           root / "maui-selective-profile.json", refs, *maui, old_consumer])
    tools = Path(run("tool-path", [local, "msbuild", "external/Java.Interop/tools/generator/generator.csproj",
                                   "-getProperty:TargetPath", "-nologo"], repo).strip()).parent
    run("class-parse", [local_host, tools / "class-parse.dll", f"--o={root / 'native/api.xml'}", root / "native/swipe.jar"])
    descriptor = repo / "external/Java.Interop/tools/jvm-api-descriptor/build/install/jvm-api-descriptor/lib"
    require((descriptor / "jvm-api-descriptor.jar").exists(), "Build jvm-api-descriptor installDist first.")
    java = args.jdk / "bin" / ("java.exe" if os.name == "nt" else "java")
    run("descriptor", [java, "-classpath", str(descriptor / "*"), "net.dot.android.descriptor.ApiDescriptorKt",
                       "--archive", root / "native/swipe.jar", "--output", root / "native/descriptor.json"])
    references = sorted(path for path in refs.glob("*.dll") if path.name != "Xamarin.AndroidX.SwipeRefreshLayout.dll")
    run("generate", [local_host, tools / "generator.dll", root / "native/api.xml",
                     "--experimental-semantic-model", f"--experimental-api-descriptor={root / 'native/descriptor.json'}",
                     f"--experimental-compatibility-profile={root / 'maui-selective-profile.json'}",
                     "--assembly=Xamarin.AndroidX.SwipeRefreshLayout", "--global", "--public", "--api-level=37",
                     "--codegen-target=XAJavaInterop1",
                     "--lang-features=interface-constants,default-interface-methods,nested-interface-types,nullable-reference-types",
                     f"--csdir={root / 'generated'}", *[f"--ref={path}" for path in references]])
    run("replacement-build", [args.stock_dotnet, "build", "Regenerated.csproj", "-c", "Debug", "-nr:false", "-v:minimal", *props, *sources], root / "Regenerated")
    replacement = root / "replacement/Xamarin.AndroidX.SwipeRefreshLayout.dll"
    shutil.copy2(root / "Regenerated/bin/Debug/net11.0-android/Xamarin.AndroidX.SwipeRefreshLayout.dll", replacement)
    run("contract-verify", [local_host, tool, "verify", root / "maui-selective-profile.json", replacement])
    require(sha(replacement) != sha(refs / replacement.name), "The delivered implementation must really be regenerated.")
    device_results = []
    if args.serial:
        adb = args.android_sdk / "platform-tools" / ("adb.exe" if os.name == "nt" else "adb")
        device = [adb, "-s", args.serial]
        require(run("boot", [*device, "shell", "getprop", "sys.boot_completed"]).strip() == "1", "Owned emulator is not ready.")
        for mode, configuration, extra in (
                ("debug", "Debug", []),
                ("release-r8", "Release", ["-p:PublishTrimmed=true", "-p:TrimMode=full", "-p:AndroidLinkTool=r8"]),
                ("nativeaot", "Release", ["-p:PublishAot=true", "-p:AndroidLinkTool=r8"])):
            host = args.stock_dotnet
            run(f"{mode}-build", [host, "build", "Compat.App.csproj", "-c", configuration, "-nr:false",
                                  "-v:minimal", "-p:RuntimeIdentifier=android-arm64", *extra, *props, *sources,
                                  f"-bl:{root / (mode + '.binlog')}"], root / "App")
            output = root / "App/bin" / configuration / "net11.0-android/android-arm64"
            require(sha(output / replacement.name) == sha(replacement), "App did not select the regenerated assembly.")
            require(sha(output / old_consumer.name) == frozen[str(old_consumer)], "App changed the precompiled consumer.")
            apk = next(output.glob("*-Signed.apk"))
            with zipfile.ZipFile(apk) as archive:
                if mode == "nativeaot":
                    require(any(n.endswith("/libCompat.App.so") for n in archive.namelist()) and
                            not any(n.endswith("/libcoreclr.so") for n in archive.namelist()), "Not a NativeAOT APK.")
            if mode == "release-r8":
                require((root / "App/obj/Release/net11.0-android/android-arm64/r8.json").exists(), "R8 did not run.")
            run(f"{mode}-install", [*device, "install", "-r", apk])
            run(f"{mode}-stop", [*device, "shell", "am", "force-stop", "net.dot.android.compatpilot"])
            run(f"{mode}-clear", [*device, "logcat", "-c"])
            run(f"{mode}-launch", [*device, "shell", "am", "start", "-W", "-n",
                                   "net.dot.android.compatpilot/net.dot.android.compatpilot.ProbeActivity"])
            deadline = time.monotonic() + 40
            while True:
                log = subprocess.check_output([str(p) for p in [*device, "logcat", "-d"]], text=True)
                (root / f"{mode}-logcat.log").write_text(log)
                lines = [line for line in log.splitlines() if "MauiCompatProbe" in line]
                if any("PASS " in line or "FAIL " in line for line in lines):
                    break
                require(time.monotonic() < deadline, f"{mode}: no exact result; inspect logcat.")
                time.sleep(0.5)
            require(any("PASS 13/13" in line for line in lines) and not any("FAIL " in line for line in lines),
                    f"{mode} failed: {root / (mode + '-logcat.log')}")
            device_results.append({"mode": mode, "dotnetHost": host, "checks": 13, "apkSha256": sha(apk)})
    require(all(sha(Path(path)) == value for path, value in frozen.items()), "An old consumer/MAUI binary changed.")
    full = json.loads((root / "maui-profile.json").read_text())
    selected = json.loads((root / "maui-selective-profile.json").read_text())
    report = {"scope": "Resolved MAUI AndroidX/Material public/protected boundary and observed compiled consumers.",
              "mauiVersion": "11.0.0-rc.2.26505.5", "frozenBinaries": frozen,
              "replacementSha256": sha(replacement), "originalSha256": sha(refs / replacement.name),
              "fullTypes": len(full["Types"]), "fullMembers": sum(len(t["Members"]) for t in full["Types"]),
              "selectedMembers": sum(len(t["Members"]) for t in selected["Types"]),
              "deviceResults": device_results, "commands": results}
    (root / "results.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps({key: value for key, value in report.items() if key not in ("commands", "frozenBinaries")}, indent=2))


if __name__ == "__main__":
    main()
