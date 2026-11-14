import hashlib
import json
import os
from pathlib import Path
import shlex
import subprocess
import xml.etree.ElementTree as ET
import zipfile


root = Path(__file__).parent
repo = Path("${ANDROID_SOURCE}")
tools = repo / "bin/Debug/lib/packs/Microsoft.Android.Sdk.Darwin/37.99.0/tools"
dotnet = str(repo / "dotnet-local.sh")
summary = json.loads((root / "binding-corpus/runs/16cbf0c447c43193f322/summary.json").read_text())
runtime = repo / "src/Microsoft.Android.Kotlin/bin/Debug/net11.0-android/Microsoft.Android.Kotlin.dll"
output = root / "kotlin-production-real-libraries"
output.mkdir(exist_ok=True)
selected = {
    "androidx.datastore:datastore-core-jvm:1.2.1",
    "androidx.lifecycle:lifecycle-runtime-android:2.11.0",
    "androidx.paging:paging-common-jvm:3.3.6",
    "androidx.room:room-runtime-android:2.8.5",
}


def execute(command, cwd, log):
    with log.open("w") as stream:
        result = subprocess.run(command, cwd=cwd, stdout=stream, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f"Command failed: {log}")
    return {"command": command, "cwd": str(cwd), "log": str(log), "exit_code": 0}


def methods(document):
    return {
        (package.get("name"), typ.get("name"), method.get("name"), method.get("jni-signature")): method
        for package in document.getroot().findall("package")
        for typ in package
        for method in typ.findall("method")
    }


results = []
for result in summary["results"]:
    if result["coordinate"] not in selected:
        continue
    coordinate = result["coordinate"]
    directory = output / coordinate.replace(":", "__")
    directory.mkdir(exist_ok=True)
    original = result["modes"]["semantic"]["generation"]
    inputs = Path(original["command"][3]).parents[3]
    archives = [p for p in (inputs / "artifact.jar", inputs / "artifact.aar") if p.exists()]
    if len(archives) != 1:
        raise ValueError(f"Expected one archive: {inputs}")
    archive = archives[0]
    if hashlib.sha256(archive.read_bytes()).hexdigest() != result["inputs"]["archive_sha256"]:
        raise ValueError(f"Archive hash changed: {archive}")
    native = archive
    if archive.suffix == ".aar":
        native = directory / "classes.jar"
        with zipfile.ZipFile(archive) as aar:
            native.write_bytes(aar.read("classes.jar"))
    parsed = directory / "fresh-api.xml"
    parse = execute([dotnet, str(tools / "class-parse.dll"), "-o=" + str(parsed), str(native)], directory, directory / "class-parse.log")
    # Keep reference/type configuration; refresh semantic metadata from the
    # exact native archive instead of relying on the historical parser cache.
    raw = ET.parse(original["command"][3])
    fresh = methods(ET.parse(parsed))
    count = 0
    for key, method in methods(raw).items():
        if key in fresh:
            method.attrib.update({k: v for k, v in fresh[key].attrib.items() if k.startswith("kotlin-")})
            for old_parameter, new_parameter in zip(method.findall("parameter"), fresh[key].findall("parameter")):
                old_parameter.attrib.update({k: v for k, v in new_parameter.attrib.items() if k.startswith("kotlin-")})
            count += fresh[key].get("kotlin-suspend") == "true"
    if not count:
        raise ValueError(f"No suspend metadata found: {coordinate}")
    api = directory / "api.xml.class-parse"
    raw.write(api, encoding="unicode")
    sources = directory / "semantic"
    sources.mkdir(exist_ok=True)
    command = [
        dotnet, str(tools / "generator.dll"), original["command"][2], str(api),
        "--csdir=" + str(sources), "--xml-adjuster-output=" + str(sources / "api.xml"),
        "--experimental-semantic-model", "--experimental-kotlin-async",
        "--ref=" + str(runtime),
    ]
    generation = execute(command, inputs, directory / "generation.log")
    previous_response = Path(result["modes"]["semantic"]["compilation"]["command"][-1][1:])
    arguments = shlex.split(previous_response.read_text())
    arguments = [a for a in arguments if not a.endswith(".cs") and not a.startswith("-out:")]
    arguments += ["-out:" + str(sources / "Binding.dll"), "-r:" + str(runtime)]
    arguments += [str(p) for p in sorted(sources.glob("*.cs"))]
    response = directory / "compile.rsp"
    response.write_text("\n".join('"' + a + '"' for a in arguments) + "\n")
    command = result["modes"]["semantic"]["compilation"]["command"][:3] + ["@" + str(response)]
    compilation = execute(command, inputs, directory / "compilation.log")
    text = "\n".join(p.read_text() for p in sources.glob("*.cs"))
    wrappers = text.count("global::Java.Interop.Kotlin.KotlinCoroutines.Run")
    if not wrappers:
        raise ValueError(f"No async convenience emitted: {coordinate}")
    results.append({
        "coordinate": coordinate, "suspend_facts": count, "generated_task_adapters": wrappers,
        "archive_sha256": result["inputs"]["archive_sha256"], "parse": parse,
        "generation": generation, "compilation": compilation,
        "runtime_sha256": hashlib.sha256(runtime.read_bytes()).hexdigest(),
    })
    (output / "results.json").write_text(json.dumps(results, indent=2) + "\n")
    print(coordinate, "PASS", wrappers, "Task adapters", flush=True)

if len(results) != len(selected):
    raise ValueError("A requested library was not found.")
