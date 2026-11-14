import json
from pathlib import Path

root = Path(__file__).parent
run = root / "binding-corpus/runs/16cbf0c447c43193f322"
audit = json.loads((run / "native-signature-review.json").read_text())
consumers = json.loads((run / "inherited-consumers/results.json").read_text())
assert len(consumers) == 8 and all(c["exit_code"] == 0 for c in consumers)
assert audit["completed"] == 444
assert not audit["lost_types"] and not audit["lost_fields"]
reviewed = []
for loss in audit["method_losses"]:
    category = loss["classification"]
    if category in ("same-parameters-different-return-descriptor", "same-name-different-parameters"):
        assert loss["registered_native_replacements"], loss
        outcome = "Registered replacement matches a raw native declaration."
    elif category == "exact-native-declaration":
        assert loss["exact_native_flags"]["synthetic"] == "true", loss
        assert loss["registered_native_replacements"], loss
        outcome = "Typed native alternatives retained for a synthetic bridge."
    elif category == "no-same-owner-declaration":
        coordinate = loss["coordinate"]
        consumer = next(c for c in consumers if c["coordinate"] == coordinate)
        if coordinate.startswith(("androidx.media2:", "androidx.wear.watchface:")):
            outcome = "Imported native descriptor retained; managed consumer compiles."
        elif coordinate.startswith(("com.google.android.gms:play-services-auth:", "com.google.android.gms:play-services-base:")):
            outcome = "Iterator invoker member retained; XPath registration owner differs."
        else:
            outcome = "Callable through inherited CLR class/interface contract."
        loss = dict(loss, consumer=consumer)
    else:
        raise AssertionError(category)
    reviewed.append(dict(loss, review=outcome))
result = {
    "run": run.name,
    "reviewed_registration_differences": reviewed,
    "consumers": consumers,
    "limitations": [
        "Consumer compilation is not runtime validation of every native method.",
        "Media3 inherited decoder results remain erased to Java.Lang.Object.",
        "The original EmojiPicker published-reference graph remains incompatible.",
    ],
}
(run / "managed-contract-review.json").write_text(json.dumps(result, indent=2) + "\n")
print("Reviewed", len(reviewed), "registration differences; all eight CLR consumers compile.")
closure = json.loads((root / "binding-closures/closure-fd39c48ff195/comparison.json").read_text())
assert closure["modes"]["semantic"]["compilation"]["exit_code"] == 0
assert {(d["configuration"], d["nativeaot"]) for d in closure["device_probes"]} == {
    ("Debug", False), ("Release", False), ("Release", True),
}
assert all(d["results"].splitlines()[-1] == "DONE 5" for d in closure["device_probes"])
summary = json.loads((run / "summary.json").read_text())
assert summary["tools"]["generator"] == closure["inputs"]["tools"]["generator"]
print("The final corpus and all three closure device runs use the same immutable compiler.")
