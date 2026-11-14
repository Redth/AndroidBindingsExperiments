import hashlib
import json
import os
from pathlib import Path
import subprocess


evidence = Path(__file__).parent
root = Path.cwd()
command = ["./dotnet-local.sh", "build", "tests/BindingProjection/App/Reference.App.csproj", "-c", "Debug", "-v:q", "-nr:false"]


def snapshot():
    paths = []
    for project in ("Foundation", "Reader", "Writer"):
        directory = root / "tests/BindingProjection" / project / "obj/Debug/net11.0-android"
        paths.extend(p for p in (directory / "generated/src").glob("*") if p.is_file())
        paths.extend(directory.glob("ownership.json"))
    paths.append(root / "tests/BindingProjection/Foundation/obj/Debug/net11.0-android/gradle/Release-08b8a/outputs/api-descriptor.json")
    assert paths and all(p.is_file() for p in paths)
    return {
        str(p.relative_to(root)): {"mtime_ns": p.stat().st_mtime_ns, "sha256": hashlib.sha256(p.read_bytes()).hexdigest()}
        for p in sorted(set(paths))
    }


def build(label):
    with (evidence / f"api-generic-incremental-{label}.log").open("w") as log:
        subprocess.run(command, check=True, stdout=log, stderr=subprocess.STDOUT)


build("warm-debug-after-aot")
before = snapshot()
build("unchanged")
unchanged = snapshot()
assert before == unchanged, [p for p in before if before[p] != unchanged.get(p)][:10]
writer = root / "tests/BindingProjection/Writer/CounterWriter.cs"
original = writer.stat()
original_hash = hashlib.sha256(writer.read_bytes()).hexdigest()
try:
    os.utime(writer, None)
    build("csharp-only")
    after_csharp = snapshot()
    assert unchanged == after_csharp, [p for p in unchanged if unchanged[p] != after_csharp.get(p)][:10]
    assert original_hash == hashlib.sha256(writer.read_bytes()).hexdigest()
finally:
    os.utime(writer, ns=(original.st_atime_ns, original.st_mtime_ns))
result = {
    "command": command,
    "trackedOutputs": len(before),
    "unchangedBuildPreservedOutputs": True,
    "csharpOnlyChangePreservedOutputs": True,
    "writerContentUnchanged": True,
    "outputs": before,
}
(evidence / "api-generic-incremental-results.json").write_text(json.dumps(result, indent=2) + "\n")
print(f"Unchanged and C#-only builds preserved all {len(before)} generated source/metadata outputs and timestamps.")
