#!/usr/bin/env python3
"""Make public historical archives portable without changing executable payloads."""
from pathlib import Path
import hashlib
import io
import json
import re
import tarfile
import zipfile

FILES = Path(__file__).resolve().parent
ROOT = FILES / "team-handoff/release-assets"
SOURCE = Path("${ANDROID_SOURCE}")
TEXT = (".md", ".json", ".props", ".targets", ".csproj", ".proj", ".py", ".log", ".csv", ".txt")
CODE = (".dll", ".nupkg", ".cs", ".jar", ".aar", ".png")
secret = re.compile(rb"(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|-----BEGIN (?:RSA |OPENSSH |EC )?PRIVATE KEY-----)")


def sanitize(data):
    text = data.decode("utf-8-sig")
    for source, target in (
        (str(SOURCE), "${ANDROID_SOURCE}"),
        (str(FILES), "${EXPERIMENT_ARTIFACTS}"),
        ("${ANDROID_HOME}", "${ANDROID_HOME}"),
        ("${JAVA_HOME}", "${JAVA_HOME}"),
        ("${DOTNET_ROOT}", "${DOTNET_ROOT}"),
        ("${NUGET_PACKAGES}", "${NUGET_PACKAGES}"),
        ("${GRADLE_USER_HOME}", "${GRADLE_USER_HOME}"),
        ("${HOME}", "${HOME}"),
    ):
        text = text.replace(source, target)
    if "Sending adb public key" in text or "androidboot.qemu.adb.pubkey" in text:
        text = "\n".join(line for line in text.splitlines() if "Sending adb public key" not in line and "androidboot.qemu.adb.pubkey" not in line) + "\n"
    return text.encode()


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


assets = json.loads((ROOT / "assets.json").read_text())
audit = []
for asset in assets:
    path = ROOT / asset["file"]
    original = digest(path)
    if path.suffix not in (".zip",) and not path.name.endswith(".tar.xz"):
        continue
    changes = []
    binaries = {}
    temp = path.with_name(path.name + ".curated")
    if path.suffix == ".zip":
        with zipfile.ZipFile(path) as old, zipfile.ZipFile(temp, "w", zipfile.ZIP_DEFLATED, compresslevel=7) as new:
            for info in old.infolist():
                data = old.read(info.filename)
                name = info.filename
                if name.endswith(CODE):
                    binaries[name] = hashlib.sha256(data).hexdigest()
                if name.endswith(".binlog"):
                    changes.append({"file": name, "action": "omitted raw binary build log"})
                    continue
                if "logcat" in name and name.endswith(".log"):
                    lines = data.decode(errors="replace").splitlines()
                    data = ("\n".join(line for line in lines if re.search(
                        r"ThirdParty|Probe|CHECK|PASS|FAIL|FATAL|Fatal|AndroidRuntime|SIGABRT|std::runtime_error|libmaplibre", line)) + "\n").encode()
                    name = name.removesuffix(".log") + ".filtered.log"
                    changes.append({"file": info.filename, "action": "filtered to probe/crash evidence"})
                if name.endswith(TEXT) and not name.endswith(CODE):
                    clean = sanitize(data)
                    if clean != data:
                        changes.append({"file": name, "action": "replaced machine-local paths / removed emulator public-key lines"})
                    data = clean
                    if secret.search(data):
                        raise RuntimeError("Credential-shaped value in " + name)
                new.writestr(name, data)
        with zipfile.ZipFile(temp) as new:
            assert new.testzip() is None
            for name, value in binaries.items():
                assert hashlib.sha256(new.read(name)).hexdigest() == value, name
    else:
        # The large generated-source archive contains source payloads only. Leave its bytes alone.
        if path.name == "consolidated-generated-source.tar.xz":
            asset["publicAudit"] = {"generatedSourceArchivePreserved": True}
            continue
        with tarfile.open(path, "r:xz") as old, tarfile.open(temp, "w:xz", preset=6) as new:
            for info in old:
                if not info.isfile():
                    new.addfile(info)
                    continue
                stream = old.extractfile(info)
                if stream is None:
                    raise RuntimeError("Missing tar member " + info.name)
                data = stream.read()
                if info.name.endswith(CODE):
                    binaries[info.name] = hashlib.sha256(data).hexdigest()
                if info.name.endswith(TEXT) and not info.name.endswith(CODE):
                    clean = sanitize(data)
                    if clean != data:
                        changes.append({"file": info.name, "action": "replaced machine-local paths"})
                    data = clean
                    if secret.search(data):
                        raise RuntimeError("Credential-shaped value in " + info.name)
                info.size = len(data)
                info.uid = info.gid = 0
                info.uname = info.gname = ""
                new.addfile(info, io.BytesIO(data))
        with tarfile.open(temp, "r:xz") as new:
            for info in new:
                if info.name in binaries:
                    stream = new.extractfile(info)
                    assert stream is not None
                    assert hashlib.sha256(stream.read()).hexdigest() == binaries[info.name], info.name
    if changes:
        temp.replace(path)
        asset["sourceOriginalSha256"] = original
        asset["sha256"] = digest(path)
        asset["bytes"] = path.stat().st_size
        asset["format"] = "public historical archive; executable/generated-code payloads unchanged; text paths normalized"
    else:
        temp.unlink()
    asset["publicAudit"] = {"unchangedExecutableOrGeneratedSourcePayloads": len(binaries),
                             "textChanges": len(changes), "machinePaths": "replaced in narrative/evidence; source code unchanged"}
    audit.append({"file": asset["file"], "sourceSha256": original, "publishedSha256": asset["sha256"],
                  "payloadsVerifiedUnchanged": len(binaries), "changes": changes})
    print(asset["file"], len(changes), "text changes;", len(binaries), "payloads unchanged", flush=True)

(ROOT / "assets.json").write_text(json.dumps(assets, indent=2) + "\n")
(ROOT / "SHA256SUMS").write_text("".join(f"{a['sha256']}  {a['file']}\n" for a in assets))
(ROOT / "public-archive-audit.json").write_text(json.dumps(audit, indent=2) + "\n")
