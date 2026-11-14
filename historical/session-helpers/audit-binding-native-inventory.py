import argparse
from collections import Counter
import json
from pathlib import Path
import xml.etree.ElementTree as ET


parser = argparse.ArgumentParser()
parser.add_argument("run", type=Path)
args = parser.parse_args()
summary = json.loads((args.run / "summary.json").read_text())
if summary["completed"] != 444:
    raise SystemExit("The full 444-record comparison must finish before this audit.")
counts = Counter()
totals = Counter()
losses = []
type_losses = []
field_losses = []
for result in summary["results"]:
    if result["status"] != "compiled-api-changes-review-required":
        continue
    coordinate = result["coordinate"]
    semantic_methods = set(json.loads(
        (args.run / coordinate.replace(":", "__") / "semantic/inventory.json").read_text()
    )["methods"])
    delta = result["coverage_delta"]
    for kind, slots in delta.items():
        totals[kind] += len(slots)
    type_losses.extend({"coordinate": coordinate, "slot": slot} for slot in delta["lost_types"])
    field_losses.extend({"coordinate": coordinate, "slot": slot} for slot in delta["lost_fields"])
    declarations = {}
    raw_types = {}
    for package in ET.parse(result["inputs"]["source"]).getroot().findall("package"):
        for type_node in package:
            if type_node.tag not in ("class", "interface"):
                continue
            owner = ".".join(part for part in (package.get("name"), type_node.get("name")) if part)
            raw_types[owner] = type_node
            for method in type_node.findall("method"):
                signature = method.get("jni-signature")
                if signature:
                    declarations[f"{owner}.{method.get('name')}{signature}"] = dict(method.attrib)
    for slot in delta["lost_methods"]:
        prefix, descriptor = slot.split("(", 1)
        owner, name = prefix.rsplit(".", 1)
        node = raw_types.get(owner)
        candidates = [] if node is None else [
            dict(method.attrib) for method in node.findall("method") if method.get("name") == name
        ]
        flags = declarations.get(slot)
        if flags is not None:
            classification = "exact-native-declaration"
        elif any(method.get("jni-signature", "").split(")")[0] == "(" + descriptor.split(")")[0]
                 for method in candidates):
            classification = "same-parameters-different-return-descriptor"
        elif candidates:
            classification = "same-name-different-parameters"
        else:
            classification = "no-same-owner-declaration"
        replacements = [
            f"{prefix}{method['jni-signature']}" for method in candidates
            if "jni-signature" in method
            and f"{prefix}{method['jni-signature']}" in semantic_methods
            and (classification != "same-parameters-different-return-descriptor"
                 or method["jni-signature"].split(")")[0] == "(" + descriptor.split(")")[0])
        ]
        counts[classification] += 1
        losses.append({
            "coordinate": coordinate,
            "slot": slot,
            "classification": classification,
            "exact_native_flags": flags,
            "same_name_native_declarations": candidates,
            "registered_native_replacements": replacements,
        })
audit = {
    "run": args.run.name,
    "completed": summary["completed"],
    "status_counts": summary["status_counts"],
    "review_records": sum(
        result["status"] == "compiled-api-changes-review-required" for result in summary["results"]
    ),
    "review_record_inventory_delta": dict(totals),
    "method_loss_classifications": dict(counts),
    "lost_types": type_losses,
    "lost_fields": field_losses,
    "method_losses": losses,
    "limitations": (
        "Registration inventories are not complete CLR API inventories. An absent exact declaration "
        "is not proof of an invalid legacy registration: inherited lookup and external dependency "
        "contracts are not resolved by this audit. A changed descriptor still requires semantic review."
    ),
}
output = args.run / "native-signature-review.json"
output.write_text(json.dumps(audit, indent=2) + "\n")
print(json.dumps({key: value for key, value in audit.items() if key != "method_losses"}, indent=2))
