#!/usr/bin/env python3
"""Install an ordinary reference-app APK and verify two process/recreation cycles."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
import uuid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--apk", type=Path, required=True)
    parser.add_argument("--device-serial", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--configuration", required=True)
    args = parser.parse_args()
    sdk = os.environ.get("ANDROID_HOME")
    if not sdk:
        parser.error("Set ANDROID_HOME.")
    adb = [str(Path(sdk) / "platform-tools/adb"), "-s", args.device_serial]
    directory = args.output.resolve() / (args.configuration + "-" + uuid.uuid4().hex[:12])
    directory.mkdir(parents=True)

    def run(arguments, name):
        result = subprocess.run(adb + arguments, capture_output=True, text=True, timeout=120)
        (directory / (name + ".log")).write_text(result.stdout + result.stderr)
        if result.returncode:
            raise RuntimeError(f"ADB failed: {directory / (name + '.log')}")
        return result.stdout

    run(["install", "-r", str(args.apk.resolve())], "install")
    cycles = []
    expected = None
    for cycle in range(2):
        identity = uuid.uuid4().hex
        command = ["shell", "am", "start", "-S", "-W", "-n",
                   "net.dot.android.bindingreference/net.dot.android.reference.ReferenceActivity",
                   "--ez", "selfTest", "true", "--es", "runId", identity]
        if expected is not None:
            command += ["--ei", "expectedBefore", str(expected)]
        launch = run(command, f"launch-{cycle}")
        if re.search(r"^Error", launch, re.M):
            raise RuntimeError(f"Activity failed to launch: {launch}")
        lines = []
        deadline = time.monotonic() + 90
        while time.monotonic() < deadline:
            logs = run(["logcat", "-d", "-s", "BindingReference:V", "AndroidRuntime:E", "*:S"], f"logcat-{cycle}")
            lines = [line.split(identity + " ", 1)[1] for line in logs.splitlines() if identity + " " in line]
            if any(line.startswith(("DONE ", "FAIL ")) for line in lines):
                break
            time.sleep(0.5)
        passed = [line for line in lines if line.startswith("PASS ")]
        generic = [line for line in lines if line.startswith("GENERIC PASS ")]
        done = re.fullmatch(r"DONE 10 VALUE (\d+)", lines[-1]) if lines else None
        if done is None or len(passed) != 10 or len(set(passed)) != 10 or len(generic) != 26 or len(set(generic)) != 26:
            raise RuntimeError(f"The reference app did not pass all 10 workflow and 26 generic checks: {directory}, {lines}")
        current = int(done[1])
        if expected is not None and f"START VALUE {expected}" not in lines:
            raise RuntimeError("The second process did not observe the first process's persisted value.")
        expected = current
        cycles.append({"identity": identity, "results": lines, "value": current})
    evidence = {
        "configuration": args.configuration, "device": args.device_serial,
        "apk": str(args.apk.resolve()), "apk_sha256": hashlib.sha256(args.apk.read_bytes()).hexdigest(),
        "cycles": cycles, "process_restart_persistence": True,
    }
    (directory / "results.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print(f"{args.configuration}: 10/10 workflow and 26/26 generic checks twice, Activity recreation and process persistence passed; {directory}")


if __name__ == "__main__":
    main()
