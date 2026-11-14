#!/usr/bin/env python3
"""Reproduce the binding experiment without modifying an installed workload."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import zipfile


ROOT = Path(__file__).resolve().parents[1]
UPSTREAM = ROOT / "upstream"
SOURCE_URL = "https://github.com/dotnet/android.git"
SOURCE_COMMIT = "097fd4f7eb6cab27a798e04254ac838c9e8d0369"
DEFAULT_SOURCE = ROOT / ".artifacts" / "source"
TOOLS = ("generator", "class-parse", "grouped-bindings", "binding-compatibility")
PACKAGE = "Microsoft.Android.BindingGenerator.Semantic"
VERSION = "0.1.0-preview.workbench.1"


class WorkbenchError(Exception):
    pass


def run(arguments, *, cwd=ROOT, capture=False, env=None):
    arguments = [str(argument) for argument in arguments]
    print("+ " + " ".join(arguments), flush=True)
    temporary = ROOT / ".artifacts" / "tmp"
    temporary.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ if env is None else env)
    environment.update(TMPDIR=str(temporary), TMP=str(temporary), TEMP=str(temporary))
    result = subprocess.run(
        arguments, cwd=cwd, env=environment, check=True, text=True,
        stdout=subprocess.PIPE if capture else None,
    )
    return result.stdout.strip() if capture else None


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def relative_path(value):
    path = PurePosixPath(value)
    if not value or "\\" in value or path.is_absolute() or ".." in path.parts:
        raise WorkbenchError(f"Unsafe snapshot path: {value!r}")
    if not path.parts or path.parts[0] == ".git":
        raise WorkbenchError(f"Unsafe snapshot path: {value!r}")
    return Path(*path.parts)


def snapshot_files():
    manifest_path = UPSTREAM / "manifest.json"
    if not manifest_path.is_file():
        raise WorkbenchError("The curated source snapshot has not been imported.")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if (
        manifest["schemaVersion"] != 1
        or manifest["repository"] != SOURCE_URL
        or manifest["commit"] != SOURCE_COMMIT
        or manifest["additionsRoot"] != "additions"
    ):
        raise WorkbenchError("The source manifest differs from the supported snapshot.")
    records = {"manifest.json": digest(manifest_path)}
    files = [
        *manifest["patches"],
        *({**item, "path": "additions/" + item["path"]} for item in manifest["additions"]),
    ]
    for item in files:
        expected, name = item["sha256"], item["path"]
        relative = relative_path(name)
        if name in records or not re.fullmatch(r"[0-9a-f]{64}", expected):
            raise WorkbenchError(f"Duplicate path or invalid checksum: {name}")
        path = UPSTREAM / relative
        if path.is_symlink() or not path.is_file():
            raise WorkbenchError(f"Missing file or unsupported symlink: {name}")
        if not path.resolve().is_relative_to(UPSTREAM.resolve()):
            raise WorkbenchError(f"Snapshot path escapes upstream/: {name}")
        if digest(path) != expected:
            raise WorkbenchError(f"Checksum mismatch: upstream/{name}")
        records[name] = expected
    actual = {
        path.relative_to(UPSTREAM).as_posix()
        for path in UPSTREAM.rglob("*")
        if path.is_file() and path != UPSTREAM / "README.md"
    }
    if actual != records.keys():
        missing = sorted(actual.symmetric_difference(records))
        raise WorkbenchError(f"Snapshot inventory differs from manifest.json: {missing}")
    if not records or "patches/android.patch" not in records:
        raise WorkbenchError("Snapshot must include patches/android.patch.")
    return records


def verify_materialized(source):
    manifest = json.loads((UPSTREAM / "manifest.json").read_text(encoding="utf-8"))
    for item in [*manifest["trackedOutputs"], *manifest["additions"]]:
        path = source / relative_path(item["path"])
        expected = {item["sha256"], item.get("worktreeSha256", item["sha256"])}
        if not path.is_file() or digest(path) not in expected:
            raise WorkbenchError(f"Materialized source differs from export: {item['path']}")
    print(
        f"Verified {len(manifest['trackedOutputs'])} patched files and "
        f"{len(manifest['additions'])} additions against the original export."
    )


def source_marker(source):
    return source / ".git" / "binding-workbench.json"


def require_source(source):
    if not source_marker(source).is_file():
        raise WorkbenchError(
            f"No bootstrapped source at {source}. Run bootstrap first."
        )
    marker = json.loads(source_marker(source).read_text(encoding="utf-8"))
    if marker != {"commit": SOURCE_COMMIT, "files": snapshot_files()}:
        raise WorkbenchError(
            "The source directory belongs to a different snapshot. "
            "Bootstrap into a new empty --source directory."
        )
    head = run(["git", "-C", source, "rev-parse", "HEAD"], capture=True)
    if head != SOURCE_COMMIT:
        raise WorkbenchError(f"Source HEAD differs from the pinned commit: {head}")


def bootstrap(source):
    records = snapshot_files()
    if source_marker(source).is_file():
        head = run(["git", "-C", source, "rev-parse", "HEAD"], capture=True)
        if head != SOURCE_COMMIT:
            raise WorkbenchError(f"Existing source HEAD differs from the pin: {head}")
        verify_materialized(source)
        source_marker(source).write_text(
            json.dumps({"commit": SOURCE_COMMIT, "files": records}, indent=2) + "\n",
            encoding="utf-8",
        )
        print(f"Verified the existing materialization at {source}; no source overwritten.")
        return
    if source.exists() and any(source.iterdir()):
        raise WorkbenchError(
            f"Refusing to overwrite nonempty source directory: {source}. "
            "Choose a new empty --source directory."
        )
    source.mkdir(parents=True, exist_ok=True)
    run(["git", "init", "--quiet", source])
    run(["git", "-C", source, "remote", "add", "origin", SOURCE_URL])
    run(["git", "-C", source, "fetch", "--depth=1", "origin", SOURCE_COMMIT])
    run(["git", "-C", source, "checkout", "--quiet", "--detach", "FETCH_HEAD"])
    patch = UPSTREAM / "patches" / "android.patch"
    run(["git", "-C", source, "apply", "--check", patch])
    run(["git", "-C", source, "apply", patch])
    for name in sorted(records):
        if not name.startswith("additions/"):
            continue
        relative = relative_path(name.removeprefix("additions/"))
        destination = source / relative
        if destination.exists() or destination.is_symlink():
            raise WorkbenchError(f"Addition would overwrite an upstream file: {relative}")
        if not destination.resolve().is_relative_to(source.resolve()):
            raise WorkbenchError(f"Addition escapes the source directory: {relative}")
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(UPSTREAM / name, destination)
    manifest = json.loads((UPSTREAM / "manifest.json").read_text(encoding="utf-8"))
    for item in manifest["additions"]:
        (source / relative_path(item["path"])).chmod(int(item["mode"], 8) & 0o777)
    verify_materialized(source)
    source_marker(source).write_text(
        json.dumps({"commit": SOURCE_COMMIT, "files": records}, indent=2) + "\n",
        encoding="utf-8",
    )
    print(f"Materialized the pinned source and experiment at {source}.")
    print("No workloads were installed and no upstream submodules were downloaded.")


def doctor(args):
    missing = [name for name in ("git", args.dotnet, "java") if not shutil.which(name)]
    print(f"Python: {sys.version.split()[0]}")
    for executable, arguments in (
        ("git", ["--version"]),
        (args.dotnet, ["--list-sdks"]),
        ("java", ["-version"]),
    ):
        if executable not in missing:
            run([executable, *arguments])
    if missing:
        raise WorkbenchError("Missing prerequisites: " + ", ".join(missing))
    run([args.dotnet, "--version"])
    print("Use JDK 21. Host projects target net10.0; packaged MSBuild tasks need .NET 11.")
    print("Doctor reports installed tools; it does not install SDKs or workloads.")


def tool_project(source, name):
    return source / "external" / "Java.Interop" / "tools" / name / (name + ".csproj")


def managed(args, verb, project, *extra):
    return run(
        [args.dotnet, verb, project, "-c", "Debug", "-nr:false", "-v:minimal", *extra],
    )


def descriptor(args):
    wrapper = args.source / "build-tools" / "gradle" / (
        "gradlew.bat" if os.name == "nt" else "gradlew"
    )
    run(
        [*([] if os.name == "nt" else ["bash"]), wrapper,
         "--no-daemon", "--console", "plain", "-p",
         args.source / "external/Java.Interop/tools/jvm-api-descriptor",
         "test", "installDist"],
        cwd=args.source,
    )


def build(args):
    for name in TOOLS:
        managed(args, "build", tool_project(args.source, name))
    descriptor(args)


def test(args):
    for project in (
        "external/Java.Interop/tests/generator-Tests/generator-Tests.csproj",
        "tests/Microsoft.Android.BindingGenerator-Tests/Microsoft.Android.BindingGenerator-Tests.csproj",
    ):
        managed(args, "test", args.source / project)
    run(
        [sys.executable, "-m", "unittest", "discover", "-s",
         args.source / "tests" / "BindingProjection" / "Corpus", "-p", "test_*.py", "-v"],
        cwd=args.source,
    )
    run([sys.executable, "-m", "unittest", "discover", "-s", ROOT / "eng" / "tests", "-v"])


def inspect_package(path):
    if not path.is_file():
        raise WorkbenchError(f"Package not found: {path}. Run pack first.")
    required = {
        f"build/{PACKAGE}.targets",
        f"build/{PACKAGE}.Bindings.targets",
        f"tools/tasks/{PACKAGE}.dll",
        "tools/generator/generator.dll",
        "tools/parser/class-parse.dll",
        "tools/descriptor/jvm-api-descriptor.jar",
        "THIRD-PARTY-NOTICES.TXT",
    }
    with zipfile.ZipFile(path) as archive:
        names = set(archive.namelist())
        if required - names:
            raise WorkbenchError(f"Package is missing required files: {sorted(required - names)}")
        forbidden = [
            name for name in names
            if name.startswith(("lib/", "ref/", "buildTransitive/", "tools/tasks/Microsoft.Build."))
            or (name.endswith(".jar") and not name.startswith("tools/"))
        ]
        if forbidden:
            raise WorkbenchError(f"Build-only package contains unexpected assets: {forbidden}")
        listing = ROOT / ".artifacts" / "package-contents.txt"
        listing.parent.mkdir(parents=True, exist_ok=True)
        listing.write_text("\n".join(sorted(names)) + "\n", encoding="utf-8")
    print(f"Inspected {len(names)} package entries; SHA256 {digest(path)}")
    print(f"Package contents: {listing}")


def pack(args):
    verify_materialized(args.source)
    manifest = json.loads((UPSTREAM / "manifest.json").read_text(encoding="utf-8"))
    for arguments, expected in (
        (["diff", "--no-ext-diff", "--name-only", "-z"],
         {item["path"] for item in manifest["trackedOutputs"]}),
        (["ls-files", "--others", "--exclude-standard", "-z"],
         {item["path"] for item in manifest["additions"]}),
    ):
        actual = set(run(["git", "-C", args.source, *arguments], capture=True).split("\0")) - {""}
        if actual != expected:
            raise WorkbenchError(
                "Package provenance requires an unmodified snapshot; source inventory differs: "
                + ", ".join(sorted(actual.symmetric_difference(expected)))
            )
    # Publish invoked by the package project does not restore these tool projects.
    for name in ("generator", "class-parse"):
        managed(args, "build", tool_project(args.source, name))
    descriptor(args)
    feed = ROOT / ".artifacts" / "feed"
    feed.mkdir(parents=True, exist_ok=True)
    managed(args, "pack", args.source / "src" / PACKAGE / (PACKAGE + ".csproj"),
            "-o", feed, f"-p:PackageVersion={VERSION}")
    package = feed / f"{PACKAGE}.{VERSION}.nupkg"
    inspect_package(package)
    provenance = {
        "package": {"file": package.name, "sha256": digest(package)},
        "sdk": run([args.dotnet, "--version"], capture=True),
        "sourceManifestSha256": digest(UPSTREAM / "manifest.json"),
        "sourceManifest": json.loads((UPSTREAM / "manifest.json").read_text(encoding="utf-8")),
        "workbenchScriptSha256": digest(Path(__file__)),
        "sdkSelectionSha256": digest(ROOT / "global.json"),
        "packageVersionOverride": VERSION,
        "sourceIntegrationPatches": [],
    }
    provenance_path = feed / f"{PACKAGE}.{VERSION}.provenance.json"
    provenance_path.write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(f"Matching package/source hashes: {provenance_path}")


def invoke_tool(args):
    project = tool_project(args.source, args.tool)
    target = run(
        [args.dotnet, "msbuild", project, "-nologo", "-p:Configuration=Debug",
         "-getProperty:TargetPath"],
        capture=True,
    )
    if not Path(target).is_file():
        raise WorkbenchError(f"Tool is not built at {target}. Run build first.")
    arguments = args.arguments[1:] if args.arguments[:1] == ["--"] else args.arguments
    run([args.dotnet, "--roll-forward", "Major", target, *arguments])


def verify_restored_package(package, cache):
    restored = cache / PACKAGE.lower() / VERSION / package.name.lower()
    if not restored.is_file() or digest(restored) != digest(package):
        raise WorkbenchError("The restored compiler does not match the freshly packed nupkg.")


def generate(args):
    package = ROOT / ".artifacts" / "feed" / f"{PACKAGE}.{VERSION}.nupkg"
    inspect_package(package)
    work = ROOT / ".artifacts" / "hello"
    shutil.copytree(ROOT / "samples" / "hello", work, dirs_exist_ok=True)
    classes = work / "classes"
    classes.mkdir(parents=True, exist_ok=True)
    extension = ".exe" if os.name == "nt" else ""
    jdk = args.jdk.expanduser().resolve() if args.jdk else None
    javac = jdk / "bin" / ("javac" + extension) if jdk else "javac"
    jar = jdk / "bin" / ("jar" + extension) if jdk else "jar"
    run([javac, "--release", "11", "-parameters", "-d", classes,
         work / "Native" / "Greeter.java"])
    run([jar, "cf", work / "Binding" / "hello.jar", "-C", classes, "."])
    cache = ROOT / ".artifacts" / "sample-packages" / digest(package)
    environment = dict(os.environ, NUGET_PACKAGES=str(cache))
    properties = []
    if jdk:
        properties.append(f"-p:JavaSdkDirectory={jdk}")
    if args.android_sdk:
        properties.append(f"-p:AndroidSdkDirectory={args.android_sdk.expanduser().resolve()}")
    run(
        [args.dotnet, "build", work / "Consumer" / "Hello.Consumer.csproj",
         "-c", "Debug", "-nr:false", "-v:minimal",
         "--source", package.parent, "--source", "https://api.nuget.org/v3/index.json",
         *properties],
        cwd=work, env=environment,
    )
    verify_restored_package(package, cache)
    generated = list((work / "Binding" / "obj").rglob("*Greeter*.cs"))
    if not generated or not any("Greet (" in path.read_text(encoding="utf-8") for path in generated):
        raise WorkbenchError("Consumer built but the expected generated Greeter binding is absent.")
    print(f"Generated Greeter and compiled its downstream consumer in {work}.")
    print("This is a compile check, not an Android device/JNI execution result.")


def verify_package(args):
    if not args.jdk or not args.android_sdk:
        raise WorkbenchError(
            "The package fixture needs --jdk/JAVA_HOME and --android-sdk/ANDROID_HOME."
        )
    package = ROOT / ".artifacts" / "feed" / f"{PACKAGE}.{VERSION}.nupkg"
    inspect_package(package)
    cache = ROOT / ".artifacts" / "fixture-packages" / digest(package)
    run(
        [sys.executable, args.source / "tests/BindingProjection/GeneratorPackage/verify-package.py",
         "--work-dir", args.work_dir.expanduser().resolve(),
         "--feed", package.parent, "--package-version", VERSION,
         "--dotnet", shutil.which(args.dotnet) or args.dotnet,
         "--jdk", args.jdk.expanduser().resolve(),
         "--android-sdk", args.android_sdk.expanduser().resolve(),
         "--source", "https://api.nuget.org/v3/index.json"],
        env=dict(os.environ, NUGET_PACKAGES=str(cache)),
    )
    verify_restored_package(package, cache)


def verify_preservation():
    snapshot_files()
    curated = json.loads((ROOT / "provenance" / "curated-files.json").read_text(encoding="utf-8"))
    for item in curated:
        path = ROOT / relative_path(item["path"])
        if not path.is_file() or digest(path) != item["sha256"]:
            raise WorkbenchError(f"Curated file differs from the export: {item['path']}")
    assets = json.loads((ROOT / "release-assets" / "assets.json").read_text(encoding="utf-8"))
    declared = {}
    for line in (ROOT / "release-assets" / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        match = re.fullmatch(r"([0-9a-f]{64})  (.+)", line)
        if not match or match[2] in declared:
            raise WorkbenchError(f"Invalid or duplicate release asset checksum: {line!r}")
        declared[match[2]] = match[1]
    if declared != {item["file"]: item["sha256"] for item in assets}:
        raise WorkbenchError("Historical asset manifest and SHA256SUMS disagree.")
    print(f"Verified the source payload and {len(curated)} preserved text files.")
    print(f"Checked {len(assets)} historical asset records; asset bytes are external, not in git.")


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    result.add_argument(
        "--source", type=Path, default=DEFAULT_SOURCE,
        help="isolated materialized upstream checkout (default: .artifacts/source)",
    )
    result.add_argument("--dotnet", default="dotnet", help="installed dotnet host to use")
    commands = result.add_subparsers(dest="command", required=True)
    commands.add_parser("doctor", help="report prerequisites without installing tools")
    commands.add_parser("verify-snapshot", help="verify the complete source checksum inventory")
    commands.add_parser("verify-preservation", help="verify imported source/text and historical asset accounting")
    commands.add_parser("bootstrap", help="fetch the exact source pin and apply the snapshot")
    commands.add_parser("build", help="build four host tools and test/install the descriptor")
    commands.add_parser("test", help="run generator and package-task host regressions")
    commands.add_parser("pack", help="build and inspect the local compiler preview package")
    commands.add_parser("inspect", help="inspect the local compiler preview without building it")
    tool = commands.add_parser("tool", help="invoke a built host tool using its evaluated output path")
    tool.add_argument("tool", choices=TOOLS)
    tool.add_argument("arguments", nargs=argparse.REMAINDER)
    for name, description in (
        ("generate", "generate a tiny Java binding and compile its C# consumer"),
        ("verify-package", "run the no-device installed-workload package fixture"),
    ):
        sample = commands.add_parser(name, help=description)
        sample.add_argument("--jdk", type=Path, default=os.environ.get("JAVA_HOME"))
        sample.add_argument("--android-sdk", type=Path, default=os.environ.get("ANDROID_HOME") or os.environ.get("ANDROID_SDK_ROOT"))
        if name == "verify-package":
            sample.add_argument("--work-dir", required=True, type=Path, help="new fixture directory")
    return result


def main():
    if sys.version_info < (3, 11):
        print("error: Python 3.11 or newer is required.", file=sys.stderr)
        return 1
    args = parser().parse_args()
    args.source = args.source.expanduser().resolve()
    try:
        if args.command == "doctor":
            doctor(args)
        elif args.command == "verify-snapshot":
            records = snapshot_files()
            print(f"Verified {len(records)} source snapshot files.")
        elif args.command == "verify-preservation":
            verify_preservation()
        elif args.command == "bootstrap":
            bootstrap(args.source)
        elif args.command == "inspect":
            inspect_package(ROOT / ".artifacts" / "feed" / f"{PACKAGE}.{VERSION}.nupkg")
        elif args.command == "generate":
            generate(args)
        else:
            require_source(args.source)
            {"build": build, "test": test, "pack": pack, "tool": invoke_tool,
             "verify-package": verify_package}[args.command](args)
    except (WorkbenchError, OSError, ValueError, KeyError, zipfile.BadZipFile,
            subprocess.CalledProcessError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
