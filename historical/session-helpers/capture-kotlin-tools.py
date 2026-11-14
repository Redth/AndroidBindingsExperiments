import hashlib
import json
from pathlib import Path
import shutil

root = Path(__file__).parent
repo = Path("${ANDROID_SOURCE}")
source = repo / "bin/Debug/lib/packs/Microsoft.Android.Sdk.Darwin/37.99.0/tools"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


hashes = {path.name: digest(path) for path in sorted(source.iterdir()) if path.suffix in (".dll", ".json")}
key = hashlib.sha256(json.dumps(hashes, sort_keys=True).encode()).hexdigest()[:20]
snapshot = root / "kotlin-production-tools" / key
if not snapshot.exists():
    shutil.copytree(source, snapshot)
tools = json.loads((root / "binding-corpus/runs/16cbf0c447c43193f322/summary.json").read_text())["tools"]
tools.update(generator=str(snapshot / "generator.dll"),
             generator_sha256=digest(snapshot / "generator.dll"), generator_support_hashes=hashes)
(root / "kotlin-production-tools.json").write_text(json.dumps({"tools": tools}, indent=2) + "\n")
print("Immutable compiler:", key)
