#!/usr/bin/env python3
"""Compile and execute the real generic checks against grouped implementation DLLs."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time
import uuid


ROOT = Path(__file__).resolve().parents[3]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assemblies", required=True, type=Path)
    parser.add_argument("--native-props", required=True, type=Path)
    parser.add_argument("--device", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--configuration", choices=("Debug", "Release", "NativeAOT"), default="Debug")
    parser.add_argument("--probe-assembly", type=Path, help="Precompiled fixture-only binding, referencing the consolidated dependencies without rebinding them.")
    args = parser.parse_args()
    directory = args.output.resolve() / (args.configuration + "-" + uuid.uuid4().hex[:12])
    directory.mkdir(parents=True)
    configuration = "Debug" if args.configuration == "Debug" else "Release"
    command = [str(ROOT / "dotnet-local.sh"), "build",
               "tests/BindingProjection/Corpus/Smoke/GroupedSmoke.csproj", "-t:Install", "-c", configuration,
               "-v:q", "-nr:false", "-p:AdbTarget=-s " + args.device,
               "-p:_AndroidGroupedAssemblyDirectory=" + str(args.assemblies.resolve()),
               "-p:_AndroidGroupedSmokeNativeInputs=" + str(args.native_props.resolve())]
    if args.configuration == "NativeAOT":
        command.append("-p:PublishAot=true")
    if args.probe_assembly:
        if not args.probe_assembly.is_file():
            parser.error("--probe-assembly must identify the compiled fixture-only binding.")
        command.append("-p:_AndroidGroupedProbeAssembly=" + str(args.probe_assembly.resolve()))
    with (directory / "build.log").open("w") as log:
        subprocess.run(command, cwd=ROOT, check=True, stdout=log, stderr=subprocess.STDOUT)
    apk = ROOT / f"tests/BindingProjection/Corpus/Smoke/bin/{configuration}/net11.0-android/android-arm64/net.dot.android.groupedbindings-Signed.apk"
    adb = [str(Path(os.environ["ANDROID_HOME"]) / "platform-tools/adb"), "-s", args.device]
    identity = uuid.uuid4().hex
    launch = adb + ["shell", "am", "start", "-S", "-W", "-n",
                    "net.dot.android.groupedbindings/net.dot.android.bindings.GroupedSmokeActivity", "--es", "run", identity]
    completed = subprocess.run(launch, capture_output=True, text=True, check=True)
    (directory / "launch.log").write_text(completed.stdout + completed.stderr)
    if "Error" in completed.stdout:
        raise RuntimeError(f"Grouped activity failed to launch: {directory}")
    results = []
    for _ in range(120):
        read = subprocess.run(adb + ["logcat", "-d", "-v", "raw", "-s", "GroupedBindingSmoke:V", "*:S"],
                              capture_output=True, text=True, check=True)
        results = [line[len(identity) + 1:] for line in read.stdout.splitlines() if line.startswith(identity + " ")]
        if any(line.startswith(("DONE ", "FAIL ")) for line in results):
            break
        time.sleep(0.5)
    (directory / "results.txt").write_text("\n".join(results) + "\n")
    passed = [line for line in results if line.startswith("GENERIC PASS ")]
    if len(passed) != 26 or len(set(passed)) != 26 or not results or results[-1] != "DONE 26":
        error = subprocess.run(adb + ["logcat", "-d", "-s", "GroupedBindingSmoke:V", "AndroidRuntime:E", "*:S"],
                               capture_output=True, text=True, check=True)
        (directory / "failure.log").write_text(error.stdout + error.stderr)
        raise RuntimeError(f"Grouped runtime checks failed: {directory}\n" + "\n".join(results[-8:]))
    (directory / "results.json").write_text(json.dumps({
        "configuration": args.configuration, "device": args.device, "identity": identity,
        "assemblies": str(args.assemblies.resolve()), "apk": str(apk),
        "apkSha256": hashlib.sha256(apk.read_bytes()).hexdigest(),
        "buildCommand": command, "launchCommand": launch, "results": results, "passed": 26,
        "probeAssembly": str(args.probe_assembly.resolve()) if args.probe_assembly else None,
        "probeAssemblySha256": hashlib.sha256(args.probe_assembly.read_bytes()).hexdigest() if args.probe_assembly else None,
    }, indent=2) + "\n")
    print(f"{args.configuration}: 26/26 grouped-assembly checks passed; {directory}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
