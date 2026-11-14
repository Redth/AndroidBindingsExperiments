import hashlib
import io
import json
from pathlib import Path
import subprocess
import zipfile

ROOT = Path(__file__).parent
JAVAP = "${JAVA_HOME}/bin/javap"


def graph(case):
    return json.loads((ROOT / "indexes" / (case + ".json")).read_text())


def extract(case, name):
    current = graph(case)
    facts = current["classes"][name]
    artifact = next(a for a in current["artifacts"] if ":".join(a[k] for k in ("group", "artifact", "version")) == facts["coordinate"])
    with zipfile.ZipFile(artifact["file"]) as container:
        if Path(artifact["file"]).suffix == ".aar":
            matches = []
            for entry in container.namelist():
                if entry == "classes.jar" or entry.startswith("libs/") and entry.endswith(".jar"):
                    with zipfile.ZipFile(io.BytesIO(container.read(entry))) as jar:
                        if name + ".class" in jar.namelist():
                            matches.append(jar.read(name + ".class"))
            if len(matches) != 1:
                raise ValueError(f"Expected exactly one class match: {case} {name}")
            data = matches[0]
        else:
            data = container.read(name + ".class")
    if hashlib.sha256(data).hexdigest() != facts["sha256"]:
        raise ValueError(f"Class hash mismatch: {case} {name}")
    directory = ROOT / "javap" / case
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / (name.rsplit("/", 1)[-1] + ".class")
    path.write_bytes(data)
    result = subprocess.run([JAVAP, "-p", "-s", "-c", str(path)], text=True, capture_output=True, check=True)
    path.with_suffix(".txt").write_text(result.stdout)
    return result.stdout


def verify_slot(old_case, new_case, owner, member, descriptor):
    a = graph(old_case)["classes"][owner]
    b = graph(new_case)["classes"][owner]
    old = next(m for m in a["methods"] if m["name"] == member and m["descriptor"] == descriptor)
    new = next(m for m in b["methods"] if m["name"] == member and m["descriptor"] == descriptor)
    assert bool(old["flags"] & 8) == bool(new["flags"] & 8)
    return {"old_case": old_case, "new_case": new_case, "owner": owner, "name": member,
            "descriptor": descriptor, "old_flags": old["flags"], "new_flags": new["flags"]}


if __name__ == "__main__":
    old_owner = "androidx/compose/ui/platform/AndroidCompositionLocals_androidKt"
    lifecycle_getter = "getLocalLifecycleOwner"
    savedstate_getter = "getLocalSavedStateRegistryOwner"
    descriptor = "()Landroidx/compose/runtime/ProvidableCompositionLocal;"
    old_ui = extract("compose-lifecycle-old", old_owner)
    new_ui = extract("compose-lifecycle-new", old_owner)
    latest_ui = extract("compose-savedstate-new", old_owner)
    assert lifecycle_getter + "();" in old_ui
    assert "Method androidx/lifecycle/compose/LocalLifecycleOwnerKt.getLocalLifecycleOwner:" in new_ui
    assert "Method androidx/savedstate/compose/LocalSavedStateRegistryOwnerKt.getLocalSavedStateRegistryOwner:" in latest_ui
    slots = [
        verify_slot("compose-lifecycle-old", "compose-lifecycle-new", old_owner, lifecycle_getter, descriptor),
        verify_slot("compose-savedstate-old", "compose-savedstate-new", old_owner, savedstate_getter, descriptor),
        verify_slot("lifecycle-2.7.0", "lifecycle-2.8.0", "androidx/lifecycle/ViewModel", "addCloseable", "(Ljava/io/Closeable;)V"),
    ]
    for case in ("lifecycle-2.7.0", "lifecycle-2.8.0"):
        extract(case, "androidx/lifecycle/ViewModel")
    for case in ("location-20.0.0", "location-21.0.0"):
        extract(case, "com/google/android/gms/location/FusedLocationProviderClient")
    for case in ("base-8.4.0", "base-9.0.0"):
        extract(case, "com/google/android/gms/common/ConnectionResult")
        extract(case, "com/google/android/gms/common/api/Api")
    old = graph("base-8.4.0")["classes"]["com/google/android/gms/common/ConnectionResult"]
    new = graph("base-9.0.0")["classes"]["com/google/android/gms/common/ConnectionResult"]
    ancestor = graph("base-9.0.0")["classes"]["com/google/android/gms/common/internal/safeparcel/AbstractSafeParcelable"]
    assert any(m["name"] == "describeContents" and m["descriptor"] == "()I" for m in old["methods"])
    assert not any(m["name"] == "describeContents" for m in new["methods"])
    assert any(m["name"] == "describeContents" and m["descriptor"] == "()I" for m in ancestor["methods"])
    known_clients = ("ActivityRecognitionClient", "FusedLocationProviderClient", "GeofencingClient", "SettingsClient")
    for name in known_clients:
        binary_name = "com/google/android/gms/location/" + name
        assert not graph("location-20.0.0")["classes"][binary_name]["flags"] & 0x200
        assert graph("location-21.0.0")["classes"][binary_name]["flags"] & 0x200
        assert graph("location-21.3.0")["classes"][binary_name]["flags"] & 0x200
    inventories = {p.stem: json.loads(p.read_text()) for p in (ROOT / "indexes").glob("*.json")}
    assert len(inventories) == 32
    assert all(x["status"] == "indexed" for x in inventories.values())
    assert all(not a["artifact"].endswith(("-desktop", "-jvmstubs")) for x in inventories.values() for a in x["artifacts"])
    aliases = [
        {"old_case": "lifecycle-2.5.1", "old_artifact": "androidx.lifecycle:lifecycle-runtime-ktx",
         "new_case": "lifecycle-2.6.0", "new_artifact": "androidx.lifecycle:lifecycle-common"},
        {"old_case": "savedstate-1.2.1", "old_artifact": "androidx.savedstate:savedstate-ktx",
         "new_case": "savedstate-1.3.0", "new_artifact": "androidx.savedstate:savedstate-android"},
    ]
    duplicate_containers = []
    for pair in aliases:
        a = {n for n, c in inventories[pair["old_case"]]["classes"].items() if c["artifact"] == pair["old_artifact"]}
        b = {n for n, c in inventories[pair["new_case"]]["classes"].items() if c["artifact"] == pair["new_artifact"]}
        duplicate_containers.append(dict(pair, conflicting_classes=sorted(a & b)))
    duplicates = {case: item["duplicate_classes"] for case, item in inventories.items() if item["duplicate_classes"]}
    artifacts = {}
    for item in inventories.values():
        for artifact in item["artifacts"]:
            key = artifact["sha256"]
            artifacts[key] = {k: artifact[k] for k in ("group", "artifact", "version", "sha256", "attributes")}
    evidence = {
        "snapshot_count": len(inventories), "distinct_native_archive_hashes": len(artifacts),
        "comparison_count": len(json.loads((ROOT / "binary-comparisons.json").read_text())),
        "known_location_class_to_interface_changes": list(known_clients),
        "verified_preserved_slots": slots,
        "verified_forwarding_getters": [lifecycle_getter, savedstate_getter],
        "inherited_example": "ConnectionResult.describeContents()I -> AbstractSafeParcelable.describeContents()I",
        "graphs_with_duplicate_native_classes": duplicates,
        "deliberately_mixed_old_new_container_conflicts": duplicate_containers,
        "native_artifacts": sorted(artifacts.values(), key=lambda a: (a["group"], a["artifact"], a["version"])),
        "scope": "Targeted bytecode/release-note research, not a population-wide frequency estimate or on-device compatibility certification."
    }
    (ROOT / "verified-evidence.json").write_text(json.dumps(evidence, indent=2) + "\n")
    print("Verified", evidence["snapshot_count"], "Android/JVM snapshots;",
          evidence["distinct_native_archive_hashes"], "distinct native archive hashes;",
          evidence["comparison_count"], "historical comparisons.")
    print("Snapshots with genuine duplicate native classes:", {k: len(v) for k, v in duplicates.items()})
    for case, items in duplicates.items():
        print(case, items[:2])
    print("Deliberately mixed container conflicts:", [(x["old_artifact"], len(x["conflicting_classes"])) for x in duplicate_containers])
    print("Preserved Compose forwarding getters, Closeable bridge, inherited Parcelable method, and four Location kind changes verified.")
