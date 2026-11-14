import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time

parser = argparse.ArgumentParser()
parser.add_argument("label")
args = parser.parse_args()
repo = Path("${ANDROID_SOURCE}")
root = Path(__file__).parent / "binding-reference-measurements"
root.mkdir(exist_ok=True)
project = repo / "tests/BindingProjection"
selected = [
    project / "Foundation/obj/Debug/net11.0-android/api.xml.class-parse",
    project / "Foundation/obj/Debug/net11.0-android/ownership.json",
    project / "Foundation/obj/Debug/net11.0-android/generated/src/Net.Dot.Android.Reference.SettingsRepository.cs",
    project / "Reader/obj/Debug/net11.0-android/views/Reference.Local.SettingsView.cs",
    project / "Writer/obj/Debug/net11.0-android/views/Reference.Local.SettingsView.cs",
    project / "Foundation/bin/Debug/net11.0-android/Reference.Foundation.dll",
    project / "Reader/bin/Debug/net11.0-android/Reference.Reader.dll",
    project / "Writer/bin/Debug/net11.0-android/Reference.Writer.dll",
]

def snapshot():
    return {str(p.relative_to(repo)): {"mtime_ns": p.stat().st_mtime_ns,
            "sha256": hashlib.sha256(p.read_bytes()).hexdigest()} if p.exists() else None for p in selected}

before = snapshot()
command = [str(repo / "dotnet-local.sh"), "build", str(project / "App/Reference.App.csproj"),
           "-c", "Debug", "-nr:false", "-v:minimal", "-bl:" + str(root / (args.label + ".binlog"))]
start = time.monotonic()
with (root / (args.label + ".log")).open("w") as log:
    result = subprocess.run(command, cwd=repo, stdout=log, stderr=subprocess.STDOUT)
after = snapshot()
evidence = {"label": args.label, "command": command, "exit_code": result.returncode,
            "seconds": time.monotonic() - start, "before": before, "after": after,
            "rewritten": [p for p in after if after[p] != before[p]]}
(root / (args.label + ".json")).write_text(json.dumps(evidence, indent=2) + "\n")
print(args.label, "exit", result.returncode, "seconds", round(evidence["seconds"], 3),
      "rewritten", evidence["rewritten"], flush=True)
raise SystemExit(result.returncode)
