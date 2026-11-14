import hashlib
import io
import json
from pathlib import Path
import re
import struct
import zipfile

ROOT = Path(__file__).parent


class Reader:
    def __init__(self, data):
        self.data = data
        self.position = 0

    def take(self, length):
        end = self.position + length
        if end > len(self.data):
            raise ValueError("Truncated JVM class file")
        value = self.data[self.position:end]
        self.position = end
        return value

    def u1(self):
        return self.take(1)[0]

    def u2(self):
        return struct.unpack(">H", self.take(2))[0]

    def u4(self):
        return struct.unpack(">I", self.take(4))[0]


def class_facts(data):
    reader = Reader(data)
    if reader.u4() != 0xCAFEBABE:
        raise ValueError("Invalid JVM class file magic")
    reader.take(4)
    pool = [None] * reader.u2()
    index = 1
    while index < len(pool):
        tag = reader.u1()
        if tag == 1:
            pool[index] = reader.take(reader.u2())
        elif tag in (7, 8, 16, 19, 20):
            pool[index] = reader.u2()
        elif tag in (3, 4, 9, 10, 11, 12, 17, 18):
            reader.take(4)
        elif tag in (5, 6):
            reader.take(8)
            index += 1
        elif tag == 15:
            reader.take(3)
        else:
            raise ValueError(f"Unknown JVM constant-pool tag {tag}")
        index += 1

    def text(index):
        return pool[index].decode("utf-8")

    def name(index):
        return text(pool[index]) if index else None

    def value(r):
        tag = chr(r.u1())
        if tag in "BCDFIJSZsc":
            r.u2()
        elif tag == "e":
            r.take(4)
        elif tag == "@":
            annotation(r)
        elif tag == "[":
            for _ in range(r.u2()):
                value(r)
        else:
            raise ValueError(f"Unknown annotation value tag {tag}")

    def annotation(r):
        descriptor = text(r.u2())
        for _ in range(r.u2()):
            r.u2()
            value(r)
        return descriptor

    def attributes(r):
        annotations = []
        for _ in range(r.u2()):
            attribute_name = text(r.u2())
            payload = r.take(r.u4())
            if attribute_name in ("RuntimeVisibleAnnotations", "RuntimeInvisibleAnnotations"):
                item = Reader(payload)
                annotations.extend(annotation(item) for _ in range(item.u2()))
        return annotations

    flags = reader.u2()
    this = name(reader.u2())
    parent = name(reader.u2())
    interfaces = [name(reader.u2()) for _ in range(reader.u2())]

    def members():
        result = []
        for _ in range(reader.u2()):
            access, member_name, descriptor = reader.u2(), text(reader.u2()), text(reader.u2())
            result.append({"name": member_name, "descriptor": descriptor, "flags": access,
                           "annotations": attributes(reader)})
        return result

    fields = members()
    methods = members()
    annotations = attributes(reader)
    if reader.position != len(data):
        raise ValueError("Unexpected trailing class-file data")
    return {"name": this, "flags": flags, "super": parent, "interfaces": interfaces,
            "fields": fields, "methods": methods, "annotations": annotations}


def jar_entries(archive):
    for entry in archive.infolist():
        if entry.filename.endswith(".class") and not entry.filename.startswith("META-INF/") and entry.filename != "module-info.class":
            yield entry.filename, archive.read(entry)


def inspect_graph(path):
    resolved = json.loads(path.read_text())
    if resolved["status"] != "resolved":
        return resolved
    classes = {}
    duplicates = []
    artifacts = []
    for artifact in resolved["artifacts"]:
        file = Path(artifact["file"])
        coordinate = ":".join(artifact[k] for k in ("group", "artifact", "version"))
        origin = artifact["group"] + ":" + artifact["artifact"]
        artifacts.append(dict(artifact, sha256=hashlib.sha256(file.read_bytes()).hexdigest()))
        with zipfile.ZipFile(file) as container:
            if file.suffix == ".aar":
                entries = []
                for entry in container.infolist():
                    if entry.filename == "classes.jar" or entry.filename.startswith("libs/") and entry.filename.endswith(".jar"):
                        with zipfile.ZipFile(io.BytesIO(container.read(entry))) as jar:
                            entries.extend(jar_entries(jar))
            else:
                entries = jar_entries(container)
            for entry_name, data in entries:
                facts = class_facts(data)
                if facts["name"] + ".class" != entry_name:
                    raise ValueError(f"JVM entry/name mismatch: {coordinate}: {entry_name}")
                facts.update(artifact=origin, coordinate=coordinate, sha256=hashlib.sha256(data).hexdigest())
                if facts["name"] in classes:
                    duplicates.append({"name": facts["name"], "owners": [classes[facts["name"]]["coordinate"], coordinate]})
                else:
                    classes[facts["name"]] = facts
    return {"status": "indexed", "requested": resolved["requested"], "artifacts": artifacts,
            "classes": classes, "duplicate_classes": duplicates}


def focus(case, name):
    if case.startswith("mixed-"):
        return name.startswith(("androidx/activity/", "androidx/lifecycle/", "androidx/savedstate/"))
    if case.startswith("compose-"):
        return name.startswith(("androidx/compose/ui/platform/", "androidx/lifecycle/compose/", "androidx/savedstate/compose/"))
    prefix = {"collection": "androidx/collection/", "lifecycle": "androidx/lifecycle/",
              "activity": "androidx/activity/", "savedstate": "androidx/savedstate/",
              "navigation": "androidx/navigation/", "location": "com/google/android/gms/location/",
              "base": "com/google/android/gms/common/"}
    return name.startswith(prefix[case.split("-")[0]])


def public_class(item):
    return bool(item["flags"] & 1) and not item["flags"] & 0x1000


def api_member(item):
    return bool(item["flags"] & 5) and not item["flags"] & 0x1000


def resolved_method(classes, owner, member, seen=None):
    seen = set() if seen is None else seen
    if owner in seen or owner not in classes:
        return None
    seen.add(owner)
    item = classes[owner]
    for candidate in item["methods"]:
        if candidate["name"] == member["name"] and candidate["descriptor"] == member["descriptor"]:
            return {"owner": owner, "flags": candidate["flags"]}
    if member["name"] == "<init>":
        return None
    for parent in [item["super"]] + item["interfaces"]:
        result = resolved_method(classes, parent, member, seen)
        if result is not None:
            return result
    return None


def compare(old_name, new_name, graphs):
    old, new = graphs[old_name], graphs[new_name]
    if old["status"] != "indexed" or new["status"] != "indexed":
        return {"old": old_name, "new": new_name, "status": "input-failed"}
    a = {n: c for n, c in old["classes"].items() if focus(old_name, n) and public_class(c)}
    b = {n: c for n, c in new["classes"].items() if focus(new_name, n) and public_class(c)}
    moves, changes, lost, inherited, compatibility, missing, invocation_changes = [], [], [], [], [], [], []
    for name in sorted(a.keys() & b.keys()):
        x, y = a[name], b[name]
        if x["artifact"] != y["artifact"]:
            moves.append({"name": name, "from": x["artifact"], "to": y["artifact"],
                          "identical_class_bytes": x["sha256"] == y["sha256"]})
        if bool(x["flags"] & 0x0200) != bool(y["flags"] & 0x0200):
            changes.append({"name": name, "from": "interface" if x["flags"] & 0x200 else "class",
                            "to": "interface" if y["flags"] & 0x200 else "class"})
        declared = {(m["name"], m["descriptor"]): m for m in y["methods"] if api_member(m)}
        for member in x["methods"]:
            if not api_member(member):
                continue
            current = declared.get((member["name"], member["descriptor"]))
            if current is not None:
                if bool(member["flags"] & 8) != bool(current["flags"] & 8):
                    invocation_changes.append({"type": name, "name": member["name"], "descriptor": member["descriptor"]})
                continue
            result = resolved_method(new["classes"], name, member)
            record = {"type": name, "name": member["name"], "descriptor": member["descriptor"],
                      "restricted_type": "Landroidx/annotation/RestrictTo;" in x["annotations"],
                      "restricted_member": "Landroidx/annotation/RestrictTo;" in member["annotations"]}
            if result is not None and result["flags"] & 5 and bool(result["flags"] & 8) == bool(member["flags"] & 8):
                target = inherited if result["owner"] != name else compatibility
                target.append(dict(record, resolved_owner=result["owner"]))
            else:
                missing.append(record)
    lost = sorted(a.keys() - b.keys())
    return {"old": old_name, "new": new_name, "status": "compared", "old_public_classes": len(a),
            "new_public_classes": len(b), "retained_public_classes": len(a.keys() & b.keys()),
            "moved_classes": moves, "class_kind_changes": changes, "removed_public_class_declarations": lost,
            "removed_member_declarations_still_resolved": inherited,
            "retained_compatibility_declarations": compatibility,
            "method_staticness_changes": invocation_changes,
            "unresolved_or_incompatible_old_member_declarations": missing,
            "note": "JVM public/protected declarations, not an audited supported-API surface. Android platform ancestors are outside these Maven-only indexes."}


if __name__ == "__main__":
    directory = ROOT / "indexes"
    directory.mkdir(exist_ok=True)
    graphs = {}
    for path in sorted((ROOT / "graphs/resolved").glob("*.json")):
        output = directory / path.name
        if output.exists():
            graphs[path.stem] = json.loads(output.read_text())
        else:
            graphs[path.stem] = inspect_graph(path)
            output.write_text(json.dumps(graphs[path.stem], separators=(",", ":")) + "\n")
        print(path.stem, graphs[path.stem]["status"], len(graphs[path.stem].get("classes", {})), flush=True)
    pairs = [
        ("collection-1.2.0", "collection-1.3.0"), ("collection-1.3.0", "collection-1.4.0"),
        ("lifecycle-2.5.1", "lifecycle-2.6.0"), ("lifecycle-2.6.0", "lifecycle-2.7.0"),
        ("lifecycle-2.7.0", "lifecycle-2.8.0"), ("lifecycle-2.8.0", "lifecycle-2.11.0"),
        ("activity-1.8.2", "activity-1.9.0"), ("activity-1.9.0", "activity-1.13.0"),
        ("savedstate-1.2.1", "savedstate-1.3.0"), ("navigation-2.7.7", "navigation-2.8.0"),
        ("navigation-2.8.0", "navigation-2.9.0"), ("location-20.0.0", "location-21.0.0"),
        ("location-21.0.0", "location-21.0.1"), ("location-21.0.1", "location-21.3.0"),
        ("base-8.4.0", "base-9.0.0"), ("base-9.0.0", "base-10.0.1"),
        ("base-10.0.1", "base-17.0.0"), ("base-17.0.0", "base-18.11.0"),
        ("compose-lifecycle-old", "compose-lifecycle-new"),
        ("compose-savedstate-old", "compose-savedstate-new"), ("mixed-foundation", "mixed-with-navigation"),
        ("mixed-foundation", "mixed-with-credentials"),
    ]
    results = [compare(a, b, graphs) for a, b in pairs]
    (ROOT / "binary-comparisons.json").write_text(json.dumps(results, indent=2) + "\n")
    print("Comparison pairs:", len(results), flush=True)
