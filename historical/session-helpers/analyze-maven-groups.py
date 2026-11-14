import io
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ET
import zipfile


root = Path(__file__).parent
closure = root / "binding-closures/closure-fd39c48ff195"
manifest = json.loads((closure / "inputs.json").read_text())
owners = {}
for artifact in manifest["artifacts"].values():
    if not artifact["bind"]:
        continue
    path = Path(artifact["file"])
    with zipfile.ZipFile(path) as archive:
        jars = [
            archive.read(name) for name in archive.namelist()
            if name == "classes.jar" or name.startswith("libs/") and name.endswith(".jar")
        ] if path.suffix == ".aar" else [path.read_bytes()]
    for data in jars:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            for name in archive.namelist():
                if not name.endswith(".class"):
                    continue
                name = name[:-6].replace("/", ".").replace("$", ".")
                owner = (artifact["group"], artifact["artifact"])
                if name in owners and owners[name] != owner:
                    raise ValueError(f"Duplicate type ownership: {name}")
                owners[name] = owner

edges = {}
api = closure / "obj/Debug/net11.0-android/api.xml.class-parse"
for package in ET.parse(api).getroot().findall("package"):
    for typ in package:
        name = package.get("name") + "." + typ.get("name", "")
        owner = owners.get(name)
        if not owner or typ.get("visibility") != "public":
            continue
        # Inspect only the declared visible signature, never private member
        # descendants reached by recursively walking the entire type node.
        signatures = [(typ.tag, typ.attrib)]
        for member in typ:
            if member.tag in ("implements", "typeParameters") or member.get("visibility") in ("public", "protected"):
                signatures.extend((item.tag, item.attrib) for item in member.iter())
        for kind, attributes in signatures:
            for value in attributes.values():
                references = re.findall(r"L([A-Za-z0-9_/$]+);", value)
                references += re.findall(r"[A-Za-z_$][\w$]*(?:\.[\w$]+)+", value)
                for reference in references:
                    target = owners.get(reference.replace("/", ".").replace("$", "."))
                    if target and target[0] != owner[0]:
                        edges.setdefault((owner[0], target[0]), set()).add(
                            (name, kind, attributes.get("name"), reference, owner[1], target[1])
                        )

graph = {group: set() for group, _ in owners.values()}
for source, target in edges:
    graph[source].add(target)
index = {}
low = {}
stack = []
active = set()
components = []


def visit(node):
    index[node] = low[node] = len(index)
    stack.append(node)
    active.add(node)
    for target in sorted(graph[node]):
        if target not in index:
            visit(target)
            low[node] = min(low[node], low[target])
        elif target in active:
            low[node] = min(low[node], index[target])
    if low[node] == index[node]:
        component = []
        while True:
            member = stack.pop()
            active.remove(member)
            component.append(member)
            if member == node:
                break
        components.append(sorted(component))


for group in sorted(graph):
    if group not in index:
        visit(group)

result = {
    "scope": "Public/protected JVM signatures in the already-resolved 36-module AndroidX closure, not the entire corpus or a compiled per-group experiment.",
    "group_count": len(graph),
    "candidate_assembly_count_after_merging_cycles": len(components),
    "cycles": [component for component in components if len(component) > 1],
    "edges": [
        {"from": source, "to": target, "evidence": [list(item) for item in sorted(values, key=str)]}
        for (source, target), values in sorted(edges.items())
    ],
}
(root / "maven-group-analysis.json").write_text(json.dumps(result, indent=2) + "\n")
print("Groups:", len(graph), "acyclic components:", len(components), "cycles:", result["cycles"])
for source, target in sorted(edges):
    if (target, source) in edges and source < target:
        print("Reciprocal:", source, target)
        print(" ", sorted(edges[(source, target)], key=str)[0])
        print(" ", sorted(edges[(target, source)], key=str)[0])
