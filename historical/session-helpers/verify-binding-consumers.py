import argparse
import json
from pathlib import Path
import shlex
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("run", type=Path)
args = parser.parse_args()
run = args.run.resolve()
summary = json.loads((run / "summary.json").read_text())
cases = {
    "androidx.media2:media2-session:1.3.0": "Media2",
    "androidx.media3:media3-extractor:1.11.1": "Media3",
    "androidx.paging:paging-common-jvm:3.3.6": "Paging",
    "androidx.preference:preference:1.2.1": "Preference",
    "androidx.wear.watchface:watchface-guava:1.2.1": "Watchface",
    "com.google.android.gms:play-services-auth:22.0.0": "Auth",
    "com.google.android.gms:play-services-base:18.11.0": "Base",
    "com.google.android.gms:play-services-wallet:20.0.0": "Wallet",
}
results = []
for coordinate, name in cases.items():
    directory = run / coordinate.replace(":", "__") / "semantic"
    original = shlex.split((directory / "compile.rsp").read_text())
    arguments = [arg for arg in original if arg.startswith("-r:")]
    output = run / "inherited-consumers" / name
    output.mkdir(parents=True, exist_ok=True)
    source = Path(__file__).parent / "inherited-consumers" / (name + ".cs")
    arguments += [
        "-nologo", "-nostdlib+", "-target:library", "-nullable:enable",
        "-r:" + str(directory / "BindingComparison.dll"),
        "-out:" + str(output / "Consumer.dll"), str(source),
    ]
    response = output / "compile.rsp"
    response.write_text("\n".join('"' + arg + '"' for arg in arguments) + "\n")
    command = [summary["tools"]["dotnet"], summary["tools"]["csc"], "@" + str(response)]
    result = subprocess.run(command, cwd=directory.parent, capture_output=True, text=True)
    (output / "compile.log").write_text(result.stdout + result.stderr)
    results.append({"coordinate": coordinate, "source": str(source), "exit_code": result.returncode, "command": command})
    print(coordinate, result.returncode, result.stdout.strip())
(run / "inherited-consumers/results.json").write_text(json.dumps(results, indent=2) + "\n")
raise SystemExit(any(result["exit_code"] for result in results))
