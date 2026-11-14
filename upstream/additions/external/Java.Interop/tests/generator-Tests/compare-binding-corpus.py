#!/usr/bin/env python3
"""Compare every artifact record in a pinned android-libraries config.json."""

import argparse
from concurrent.futures import ThreadPoolExecutor, as_completed
from collections import Counter
import hashlib
import importlib
import io
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import time
import traceback
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


comparison = importlib.import_module("compare-bindings")


class CorpusInputError(RuntimeError):
    pass


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".writing")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def select_artifacts(config):
    return [
        dict(artifact)
        for solution in config
        for artifact in solution["artifacts"]
    ]


def identity(artifact):
    return ":".join(artifact[key] for key in ["groupId", "artifactId", "version"])


def slug(artifact):
    return identity(artifact).replace(":", "__")


def maven_base(artifact):
    repository = artifact.get("mavenRepositoryType", "Google")
    repositories = {
        "Google": "https://dl.google.com/dl/android/maven2",
        "MavenCentral": "https://repo.maven.apache.org/maven2",
    }
    if repository not in repositories:
        raise CorpusInputError(f"Unknown Maven repository {repository} for {identity(artifact)}")
    base = ("https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public-maven/maven/v1"
            if os.environ.get("RUNNINGONCI") == "true" else repositories[repository])
    return base + "/" + artifact["groupId"].replace(".", "/") + \
        f"/{artifact['artifactId']}/{artifact['version']}/{artifact['artifactId']}-{artifact['version']}"


def published_relocation(artifact, pom):
    relocation = pom.find("{*}distributionManagement/{*}relocation")
    if relocation is None:
        return None
    target = {}
    for key in ("groupId", "artifactId", "version"):
        target[key] = (relocation.findtext("{*}" + key) or artifact[key]).strip()
        if not target[key] or "${" in target[key]:
            raise CorpusInputError(f"Unsupported Maven relocation on {identity(artifact)}")
    if identity(target) == identity(artifact):
        raise CorpusInputError(f"Self-referential Maven relocation on {identity(artifact)}")
    return target


def download(url, path):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        return digest(path)
    for attempt in range(4):
        try:
            with urllib.request.urlopen(url, timeout=120) as response:
                content = response.read()
            temporary = path.with_suffix(path.suffix + ".downloading")
            temporary.write_bytes(content)
            temporary.replace(path)
            return digest(path)
        except urllib.error.HTTPError as error:
            if (error.code == 429 or error.code >= 500) and attempt < 3:
                time.sleep(2 ** attempt)
                continue
            raise CorpusInputError(f"HTTP {error.code}: {url}") from error
        except urllib.error.URLError as error:
            if attempt == 3:
                raise CorpusInputError(f"Download failed: {url}: {error.reason}") from error
            time.sleep(2 ** attempt)


def dependency_group(nuspec, framework):
    target_major = int(re.match(r"net(\d+)", framework).group(1))

    def version(text):
        return tuple(int(part) for part in re.findall(r"\d+", text))

    def rank(group):
        target = group.get("targetFramework", "").lower()
        android = re.fullmatch(r"net(\d+)\.(\d+)-android([\d.]*)", target)
        if android and int(android.group(1)) <= target_major:
            return (3, (int(android.group(1)), int(android.group(2))), version(android.group(3)))
        mono = re.fullmatch(r"monoandroid([\d.]*)", target)
        if mono:
            return (2, version(mono.group(1)), ())
        if target.startswith("netstandard") or target.startswith(".netstandard"):
            return (1, version(target), ())
        return (0, (), ())

    groups = nuspec.findall(".//{*}dependencies/{*}group")
    if not groups:
        return list(nuspec.findall(".//{*}dependencies/{*}dependency")), "ungrouped"
    group = max(groups, key=rank)
    if rank(group)[0] == 0:
        raise CorpusInputError("No compatible Android/standard NuGet dependency group.")
    return list(group), group.get("targetFramework")


def class_count(archive):
    with zipfile.ZipFile(archive) as container:
        if archive.suffix == ".jar":
            return sum(name.endswith(".class") for name in container.namelist())
        total = 0
        for name in container.namelist():
            if name == "classes.jar" or name.startswith("libs/") and name.endswith(".jar"):
                with zipfile.ZipFile(io.BytesIO(container.read(name))) as jar:
                    total += sum(entry.endswith(".class") for entry in jar.namelist())
        return total


def review_accessibility_changes(delta, decisions):
    types = {item["DeclaringType"]: item for item in decisions if not item["NativeSlot"]}
    methods = {
        item["DeclaringType"] + "." + item["NativeSlot"]: item
        for item in decisions if "(" in item["NativeSlot"]
    }
    fields = {
        item["DeclaringType"] + "." + item["NativeSlot"].split(":", 1)[0]: item
        for item in decisions if ":" in item["NativeSlot"] and "(" not in item["NativeSlot"]
    }
    expected, unexplained = {}, {}
    for kind in ("types", "methods", "fields"):
        expected[kind], unexplained[kind] = [], []
        for identity in delta.get("lost_" + kind, []):
            if kind == "types":
                decision = types.get(identity.replace("/", ".").replace("$", "."))
            else:
                native_identity = identity.replace("..ctor(", ".<init>(")
                decision = (methods if kind == "methods" else fields).get(native_identity)
                if decision is None:
                    owner = native_identity.split("(", 1)[0].rsplit(".", 1)[0]
                    decision = types.get(owner)
            if decision and decision["Classification"] in ("Excluded", "Infrastructure"):
                expected[kind].append({"identity": identity, "reason": decision["Reason"]})
            else:
                unexplained[kind].append(identity)
    return {"expected_profile_exclusions": expected, "unexplained_losses": unexplained}


def execute(command, cwd, log, timeout=600):
    start = time.monotonic()
    with log.open("w") as stream:
        try:
            process = subprocess.run(command, cwd=cwd, stdout=stream, stderr=subprocess.STDOUT, timeout=timeout)
            result = {"exit_code": process.returncode}
        except subprocess.TimeoutExpired:
            result = {"exit_code": None, "timed_out": True}
    result.update(seconds=time.monotonic() - start, command=command, working_directory=str(cwd), log=str(log))
    result.update(comparison.diagnostics(log))
    return result


def stable_version(value):
    if not re.fullmatch(r"\d+(?:\.\d+){0,3}", value):
        raise CorpusInputError(f"Cannot automatically reconcile non-stable NuGet version {value!r}.")
    parts = tuple(int(part) for part in value.split("."))
    return parts + (0,) * (4 - len(parts))


def stable_range(value):
    value = value.strip()
    if value.startswith(("[", "(")):
        if not value.endswith(("]", ")")):
            raise CorpusInputError(f"Invalid NuGet range {value!r}.")
        bounds = [part.strip() for part in value[1:-1].split(",")]
        if len(bounds) == 1 and value.startswith("[") and value.endswith("]"):
            lower = upper = bounds[0]
        elif len(bounds) == 2:
            lower, upper = bounds
        else:
            raise CorpusInputError(f"Unsupported NuGet range {value!r}.")
        return (
            stable_version(lower) if lower else None, value.startswith("["),
            stable_version(upper) if upper else None, value.endswith("]"), lower,
        )
    return stable_version(value), True, None, False, value


def compatible_dependency_promotions(assets, dependencies):
    roots = {dependency["id"].lower(): dependency for dependency in dependencies}
    downgrades = sorted({
        entry["libraryId"] for entry in assets.get("logs", [])
        if entry.get("code") == "NU1605" and entry.get("libraryId")
    })
    promotions = []
    for package in downgrades:
        root = roots.get(package.lower())
        if root is None:
            raise CorpusInputError(f"Cannot reconcile transitive-only downgrade {package}.")
        constraints = {("root", root["version"])}
        for libraries in assets.get("targets", {}).values():
            for owner, library in libraries.items():
                for dependency, version in library.get("dependencies", {}).items():
                    if dependency.lower() == package.lower():
                        constraints.add((owner, version))
        bounds = [stable_range(version) for _, version in sorted(constraints)]
        lower_bounds = [bound for bound in bounds if bound[0] is not None]
        if not lower_bounds:
            raise CorpusInputError(f"No concrete lower bound for downgrade {package}.")
        candidate = max(lower_bounds, key=lambda bound: bound[0])
        version = candidate[0]
        if any(
            lower is not None and (version < lower or version == lower and not include_lower)
            or upper is not None and (version > upper or version == upper and not include_upper)
            for lower, include_lower, upper, include_upper, _ in bounds
        ):
            raise CorpusInputError(
                f"No shared declared stable minimum for {package}: {sorted(constraints)}"
            )
        promotions.append({
            "id": root["id"], "original_range": root["version"], "version": candidate[4],
            "constraints": [{"source": owner, "range": version} for owner, version in sorted(constraints)],
        })
    return promotions


def prepare(artifact, root, tools):
    directory = root / "inputs" / slug(artifact)
    directory.mkdir(parents=True, exist_ok=True)
    prepared = directory / "prepared.json"
    if prepared.exists():
        data = json.loads(prepared.read_text())
        if data["artifact"] != artifact or data["framework"] != tools["framework"]:
            raise CorpusInputError(f"Prepared input identity changed: {directory}")
        return data

    base = maven_base(artifact)
    pom = directory / "artifact.pom"
    pom_hash = download(base + ".pom", pom)
    pom_root = ET.parse(pom).getroot()
    packaging = pom_root.findtext("{*}packaging", default="jar")
    manifest = {
        "artifact": artifact, "framework": tools["framework"], "pom_sha256": pom_hash,
        "pom_url": base + ".pom", "packaging": packaging,
    }
    relocation = published_relocation(artifact, pom_root)
    if relocation:
        manifest["classification"] = "maven-relocation"
        manifest["published_relocation"] = relocation
        save(prepared, manifest)
        return manifest
    if packaging == "pom":
        manifest["classification"] = "pom-only"
        save(prepared, manifest)
        return manifest
    if packaging not in ["jar", "aar"]:
        raise CorpusInputError(f"Unsupported packaging {packaging} for {identity(artifact)}")
    archive = directory / ("artifact." + packaging)
    manifest["archive_sha256"] = download(base + "." + packaging, archive)
    manifest["archive_url"] = base + "." + packaging
    manifest["class_count"] = class_count(archive)
    if manifest["class_count"] == 0:
        manifest["classification"] = "no-jvm-classes"
        save(prepared, manifest)
        return manifest

    package = artifact["nugetId"].lower()
    version = artifact["nugetVersion"].lower()
    nuspec = directory / "dependencies.nuspec"
    nuspec_url = f"https://api.nuget.org/v3-flatcontainer/{package}/{version}/{package}.nuspec"
    manifest["nuspec_sha256"] = download(nuspec_url, nuspec)
    dependencies, selected_group = dependency_group(ET.parse(nuspec).getroot(), tools["framework"])
    manifest["dependency_group"] = selected_group
    manifest["dependencies"] = [{"id": d.get("id"), "version": d.get("version")} for d in dependencies]
    project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(project, "PropertyGroup")
    for name, value in {
        "TargetFramework": tools["framework"], "AssemblyName": "BindingComparison",
        "IsBindingProject": "true", "Nullable": "enable",
        "EnableDefaultCompileItems": "false", "EnableDefaultAndroidItems": "false",
        "RestorePackagesWithLockFile": "true",
    }.items():
        ET.SubElement(properties, name).text = value
    items = ET.SubElement(project, "ItemGroup")
    ET.SubElement(items, "AndroidLibrary", Include=archive.name, Bind="true", Pack="false")
    for dependency in manifest["dependencies"]:
        ET.SubElement(items, "PackageReference", Include=dependency["id"], Version=dependency["version"])
    ET.indent(project)
    ET.ElementTree(project).write(directory / "Inputs.csproj", encoding="unicode")
    bootstrap = execute(
        [tools["dotnet"], "build", "Inputs.csproj", "-c", tools["configuration"], "-v:minimal", "-nr:false"],
        directory, directory / "sdk-input-build-0.log",
    )
    manifest["sdk_preparation"] = bootstrap
    intermediate = directory / f"obj/{tools['configuration']}/{tools['framework']}"
    source = intermediate / "api.xml.class-parse"
    response = intermediate / "generated/src/generator.rsp"
    assets = directory / "obj/project.assets.json"
    if (not source.exists() or not response.exists()) and assets.exists():
        save(directory / "preparation-failure.json", manifest)
        promotions = compatible_dependency_promotions(json.loads(assets.read_text()), manifest["dependencies"])
        if promotions:
            manifest["initial_sdk_preparation"] = bootstrap
            manifest["dependency_promotions"] = promotions
            pins = {promotion["id"].lower(): promotion["version"] for promotion in promotions}
            for reference in project.findall("./ItemGroup/PackageReference"):
                version = pins.get(reference.get("Include").lower())
                if version:
                    reference.set("Version", "[" + version + "]")
            ET.ElementTree(project).write(directory / "Inputs.csproj", encoding="unicode")
            bootstrap = execute(
                [tools["dotnet"], "build", "Inputs.csproj", "-c", tools["configuration"], "-v:minimal", "-nr:false"],
                directory, directory / "sdk-input-build-1.log",
            )
            manifest["sdk_preparation"] = bootstrap
    if not source.exists() or not response.exists():
        save(directory / "preparation-failure.json", manifest)
        raise CorpusInputError(f"SDK preparation did not produce raw input/response: {bootstrap['log']}")
    options = shlex.split(response.read_text())
    references = list(dict.fromkeys(option.removeprefix("--ref=") for option in options if option.startswith("--ref=")))
    if any(Path(reference).stem.lower() == package for reference in references):
        raise CorpusInputError("Primary published binding appeared in its own reference set.")
    manifest.update(
        classification="jvm-binding",
        source=str(source), response=str(response), references=references,
        source_sha256=digest(source),
        reference_hashes={reference: digest(Path(reference)) for reference in references},
    )
    save(prepared, manifest)
    return manifest


def compare(artifact, root, run_directory, tools):
    directory = run_directory / slug(artifact)
    directory.mkdir(parents=True, exist_ok=True)
    result_path = directory / "result.json"
    if result_path.exists():
        saved = json.loads(result_path.read_text())
        if saved["status"] != "input-failed":
            return saved
    result = {"coordinate": identity(artifact), "artifact": artifact, "status": "in-progress"}
    try:
        inputs = prepare(artifact, root, tools)
        result["inputs"] = inputs
        if inputs["classification"] != "jvm-binding":
            result["status"] = inputs["classification"]
            save(result_path, result)
            return result
        result["raw_type_count"] = len(ET.parse(inputs["source"]).findall("./package/class")) + len(
            ET.parse(inputs["source"]).findall("./package/interface"))
        result["modes"] = {}
        inventories = {}
        for mode in ["legacy", "semantic"]:
            mode_directory = directory / mode
            mode_directory.mkdir(exist_ok=True)
            data = {}
            result["modes"][mode] = data
            command = [
                tools["dotnet"], tools["generator"], "@" + inputs["response"], inputs["source"],
                f"--csdir={mode_directory}", f"--xml-adjuster-output={mode_directory}/api.xml",
                f"--type-map-report={mode_directory}/type-map.txt",
            ]
            if mode == "semantic":
                command.append("--experimental-semantic-model")
                if tools.get("generic_runtime"):
                    command += ["--experimental-generic-facades", "--ref=" + tools["generic_runtime"]]
                if tools.get("descriptor_tool"):
                    descriptor = mode_directory / "api-descriptor.json"
                    archive = root / "inputs" / slug(artifact) / ("artifact." + inputs["packaging"])
                    extraction = execute(
                        [tools["java"], "-Xmx1g", "-cp", str(Path(tools["descriptor_tool"]) / "*"),
                         "net.dot.android.descriptor.ApiDescriptorKt", "--archive", str(archive), "--output", str(descriptor)],
                        directory, mode_directory / "descriptor.log",
                    )
                    data["descriptor"] = extraction
                    if extraction["exit_code"] != 0:
                        data["generation"] = extraction
                        inventories[mode] = {"types": [], "methods": [], "fields": []}
                        continue
                    data["descriptor_sha256"] = digest(descriptor)
                    command.append("--experimental-api-descriptor=" + str(descriptor))
            generation = execute(command, directory, mode_directory / "generation.log")
            data["generation"] = generation
            inventories[mode] = comparison.inventory(mode_directory)
            save(mode_directory / "inventory.json", inventories[mode])
            data["registered_counts"] = {key: len(value) for key, value in inventories[mode].items()}
            if generation["exit_code"] != 0:
                continue
            semantics = mode_directory / "api.xml.semantics.json"
            if semantics.is_file():
                access = json.loads(semantics.read_text()).get("Accessibility", [])
                data["accessibility_counts"] = dict(Counter(item["Classification"] for item in access))
                data["unverified_api_count"] = sum(item["Classification"] == "Unverified" for item in access)
            generic_report = mode_directory / "api.xml.generic-projections.json"
            if generic_report.is_file():
                generic = json.loads(generic_report.read_text())
                data["generic_facade_count"] = len(generic["Facades"])
                data["generic_projection_decisions"] = dict(Counter(item["Reason"] for item in generic["Decisions"]))
            sources = sorted(mode_directory.glob("*.cs"))
            if not sources:
                data["classification"] = "no-generated-sources"
                continue
            arguments = [
                "-nologo", "-nostdlib+", "-target:library", "-unsafe",
                "-nullable:enable", "-langversion:preview", f"-out:{mode_directory}/BindingComparison.dll",
            ]
            arguments += [f"-r:{reference}" for reference in inputs["references"]]
            if tools.get("generic_runtime"):
                arguments.append("-r:" + tools["generic_runtime"])
            arguments += [str(path) for path in sources]
            compiler_response = mode_directory / "compile.rsp"
            compiler_response.write_text("\n".join('"' + argument + '"' for argument in arguments) + "\n")
            data["compilation"] = execute(
                [tools["dotnet"], tools["csc"], "-noconfig", "@" + str(compiler_response)],
                directory, mode_directory / "compilation.log",
            )
        result["coverage_delta"] = {
            f"{change}_{kind}": sorted(operation(set(inventories["semantic"][kind]), set(inventories["legacy"][kind])))
            for kind in ["types", "methods", "fields"]
            for change, operation in [("added", set.difference), ("lost", lambda new, old: old - new)]
        }
        semantic = result["modes"]["semantic"]
        semantic_facts = directory / "semantic/api.xml.semantics.json"
        if semantic_facts.is_file():
            access = json.loads(semantic_facts.read_text()).get("Accessibility", [])
            result["api_change_review"] = review_accessibility_changes(result["coverage_delta"], access)
        if semantic["generation"]["exit_code"] != 0:
            result["status"] = "semantic-generation-failed"
        elif result["raw_type_count"] == 0 and result["modes"]["legacy"]["generation"]["exit_code"] == 0 and not any(
                inventories[mode][kind] for mode in ("legacy", "semantic") for kind in ("types", "methods", "fields")):
            result["status"] = "metadata-only"
        elif "compilation" not in semantic:
            result["status"] = "no-generated-sources-review-required"
        elif semantic["compilation"]["exit_code"] != 0:
            result["status"] = "semantic-compilation-failed"
        elif result["raw_type_count"] > 0 and semantic["registered_counts"]["types"] == 0:
            result["status"] = "compiled-empty-projection-review-required"
        elif any(result["coverage_delta"][f"lost_{kind}"] for kind in ["types", "methods", "fields"]):
            review = result.get("api_change_review")
            result["status"] = "compiled-supported-api-profile" if review and not any(review["unexplained_losses"].values()) else "compiled-api-changes-review-required"
        else:
            result["status"] = "compiled"
    except (CorpusInputError, ET.ParseError, OSError, zipfile.BadZipFile) as error:
        result["status"] = "input-failed"
        result["error"] = str(error)
        (directory / "input-failure.txt").write_text(traceback.format_exc())
    save(result_path, result)
    return result


def summarize(results, run_directory, inventory, tools):
    totals = Counter(result["status"] for result in results)
    errors = Counter()
    for result in results:
        semantic = result.get("modes", {}).get("semantic", {})
        errors.update(semantic.get("compilation", {}).get("error_codes", {}))
    summary = {
        "corpus": inventory, "tools": tools,
        "completed": len(results), "status_counts": dict(totals), "semantic_error_codes": dict(errors),
        "results": sorted(results, key=lambda result: result["coordinate"]),
        "functional_review": "Pending: compilation and inventories are not functional equivalence.",
        "reference_mode": "Published dependency bindings; regenerated dependency closure validation is required separately.",
    }
    save(run_directory / "summary.json", summary)
    return totals


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, required=True)
    parser.add_argument("--config-commit", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--jobs", type=int, default=4)
    parser.add_argument("--artifact", action="append", help="Exact group:artifact for a focused rerun; default is the entire corpus.")
    parser.add_argument("--configuration", choices=["Debug", "Release"], default="Debug")
    parser.add_argument("--framework", default="net11.0-android")
    parser.add_argument("--descriptor-tool", type=Path, help="Built isolated JVM descriptor tool's lib directory.")
    parser.add_argument("--generic-runtime", type=Path, help="Canonical Microsoft.Android.Kotlin assembly for typed generic facade comparisons.")
    args = parser.parse_args()
    if args.jobs < 1:
        parser.error("--jobs must be positive")
    repo = Path(__file__).resolve().parents[4]
    dotnet = str(repo / "dotnet-local.sh")
    generators = list((repo / f"bin/{args.configuration}/lib/packs").glob("Microsoft.Android.Sdk.*/*/tools/generator.dll"))
    if len(generators) != 1:
        parser.error("Build the local SDK/generator first; expected exactly one generator.")
    sdk_version = subprocess.check_output([dotnet, "--version"], text=True).strip()
    sdks = subprocess.check_output([dotnet, "--list-sdks"], text=True)
    sdk_directory = next(
        Path(directory) / version
        for version, directory in re.findall(r"^(\S+) \[([^]]+)\]$", sdks, re.M)
        if version == sdk_version
    )
    root = args.output.resolve()
    root.mkdir(parents=True, exist_ok=True)
    generator = generators[0]
    support_hashes = {path.name: digest(path) for path in sorted(generator.parent.glob("*")) if path.suffix in [".dll", ".json"]}
    extras = {}
    if args.generic_runtime:
        if not args.generic_runtime.is_file():
            parser.error("--generic-runtime must identify a built runtime assembly.")
        extras["generic_runtime"] = digest(args.generic_runtime)
    if args.descriptor_tool:
        jars = sorted(args.descriptor_tool.glob("*.jar"))
        if not jars:
            parser.error("--descriptor-tool must identify the installed descriptor tool's lib directory.")
        extras["descriptor_tool"] = {path.name: digest(path) for path in jars}
    if extras:
        support_hashes["experimental_inputs"] = extras
    tool_hash = hashlib.sha256(json.dumps(support_hashes, sort_keys=True).encode()).hexdigest()[:20]
    snapshot = root / "tools" / tool_hash
    if not snapshot.exists():
        shutil.copytree(generator.parent, snapshot)
        if args.generic_runtime:
            shutil.copyfile(args.generic_runtime, snapshot / "Microsoft.Android.Kotlin.dll")
        if args.descriptor_tool:
            shutil.copytree(args.descriptor_tool, snapshot / "jvm-api-descriptor")
    generator = snapshot / generator.name
    tools = {
        "dotnet": dotnet, "generator": str(generator), "generator_sha256": digest(generator),
        "generator_support_hashes": support_hashes,
        "csc": str(sdk_directory / "Roslyn/bincore/csc.dll"), "sdk_version": sdk_version,
        "configuration": args.configuration, "framework": args.framework,
        "driver_sha256": digest(Path(__file__)), "comparison_driver_sha256": digest(Path(comparison.__file__)),
    }
    if args.generic_runtime:
        tools["generic_runtime"] = str(snapshot / "Microsoft.Android.Kotlin.dll")
    if args.descriptor_tool:
        java_home = os.environ.get("JAVA_HOME")
        java = Path(java_home) / "bin/java" if java_home else Path(shutil.which("java") or "")
        if not java.is_file():
            parser.error("Set JAVA_HOME to the JDK used to build the descriptor tool.")
        tools["java"] = str(java)
        tools["descriptor_tool"] = str(snapshot / "jvm-api-descriptor")
    config = json.loads(args.config.read_text())
    artifacts = select_artifacts(config)
    selected_count = len(artifacts)
    if not artifacts:
        parser.error("The configuration contains no artifact records.")
    if args.artifact:
        requested = set(args.artifact)
        artifacts = [a for a in artifacts if a["groupId"] + ":" + a["artifactId"] in requested]
        found = {a["groupId"] + ":" + a["artifactId"] for a in artifacts}
        if requested != found:
            parser.error("Unknown requested artifacts: " + ", ".join(sorted(requested - found)))
    if len({identity(artifact) for artifact in artifacts}) != len(artifacts):
        parser.error("Duplicate coordinate/version identities require explicit configuration review.")
    inventory = {
        "repository": "https://github.com/dotnet/android-libraries", "commit": args.config_commit,
        "config_sha256": digest(args.config), "config_records_in_scope": selected_count,
        "selected_records": len(artifacts), "focused_filter": args.artifact,
        "families": dict(Counter(a["groupId"] for a in artifacts)),
        "artifacts": artifacts,
    }
    run_hash = hashlib.sha256(json.dumps({"tools": tools, "corpus": inventory}, sort_keys=True).encode()).hexdigest()[:20]
    run_directory = root / "runs" / run_hash
    run_directory.mkdir(parents=True, exist_ok=True)
    save(run_directory / "inventory.json", inventory)
    shutil.copyfile(args.config, run_directory / "config.json")
    shutil.copyfile(Path(__file__), run_directory / Path(__file__).name)
    shutil.copyfile(Path(comparison.__file__), run_directory / Path(comparison.__file__).name)
    print(f"Comparing {len(artifacts)} artifacts; evidence: {run_directory}", flush=True)
    results = []
    with ThreadPoolExecutor(max_workers=args.jobs) as executor:
        futures = {executor.submit(compare, a, root, run_directory, tools): a for a in artifacts}
        for future in as_completed(futures):
            result = future.result()
            results.append(result)
            totals = summarize(results, run_directory, inventory, tools)
            semantic = result.get("modes", {}).get("semantic", {})
            errors = len(semantic.get("compilation", {}).get("errors", []))
            print(f"{len(results)}/{len(artifacts)} {result['coordinate']}: {result['status']} ({errors} C# errors)", flush=True)
    print(json.dumps(dict(totals), sort_keys=True), flush=True)
    # Even a compiling corpus still needs explicit functional/API-change review.
    return 0 if all(result["status"] in ["compiled", "compiled-supported-api-profile", "metadata-only", "pom-only", "maven-relocation", "no-jvm-classes"] for result in results) else 1


if __name__ == "__main__":
    raise SystemExit(main())
