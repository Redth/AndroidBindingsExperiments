# .NET-only workbench validation

Fresh results from **14 November 2026**, replacing the active Python workbench.
The [previous validation](workbench-validation.md) remains historical evidence;
its Python corpus count is not carried forward as a new passing test.

Environment: macOS ARM64, .NET SDK **11.0.100**, installed .NET 10 runtime,
JDK **21.0.12**, existing Android SDK and Android workload **37.2.31**.
No interpreter, SDK or workload was installed or removed.

## Reproduction

The initial build/package checks used a verified local archive, as shown below.
The default public HTTP path was subsequently verified independently as well.
The local override remains useful for offline reproduction:

```sh
dotnet run --project eng/Workbench -- --block-python doctor
dotnet run --project eng/Workbench -- \
  --archive .artifacts/archives/original-python-sources-6dd45d4.zip \
  --block-python verify-preservation
dotnet run --project eng/Workbench -- \
  --source .artifacts/dotnet-source \
  --archive .artifacts/archives/original-python-sources-6dd45d4.zip \
  --block-python bootstrap
```

The remaining source-dependent commands use the same source/archive selections:

```sh
dotnet run --project eng/Workbench -- --source .artifacts/dotnet-source --archive .artifacts/archives/original-python-sources-6dd45d4.zip --block-python build
dotnet run --project eng/Workbench -- --source .artifacts/dotnet-source --archive .artifacts/archives/original-python-sources-6dd45d4.zip --block-python test
dotnet run --project eng/Workbench -- --source .artifacts/dotnet-source --archive .artifacts/archives/original-python-sources-6dd45d4.zip --block-python pack
dotnet run --project eng/Workbench -- --block-python generate
dotnet run --project eng/Workbench -- --source .artifacts/dotnet-source --archive .artifacts/archives/original-python-sources-6dd45d4.zip --block-python verify-package --work-dir .artifacts/dotnet-package-fixture-guards
```

`JAVA_HOME` and `ANDROID_HOME` selected the existing JDK/Android SDK. The fixture
requires a new work directory each run. Omitting `--archive` downloads and
verifies the published archive into the ignored cache.

### Public download follow-up

The coordinating session verified release-server digests for the published
original archive, index and checksums, cleared the default archive cache, then
independently ran:

```sh
dotnet run --project eng/Workbench -- --block-python verify-preservation
dotnet run --project eng/Workbench -- --source .artifacts/dotnet-http-source --block-python bootstrap
```

The first command downloaded from the original indexed public URL and verified
190 curated records and all 33 archived files. The second created a new
independent upstream tree with all 91 patched files and 186 additions matching
their original hashes. Neither command invoked Python. This follow-up validates
the public default path separately from the local-override fixture evidence.

## Observed results

| Check | Result |
| --- | --- |
| New isolated upstream materialization | Exact pinned commit, original patch, all 91 tracked outputs and 186 additions verified |
| Repeated .NET bootstrap | Existing source reverified without overwriting |
| Original source/research preservation | All 190 curated records and all 33 archived original Python files verify |
| BCL-only runner/test build | Zero warnings/errors, no added NuGet dependencies |
| Four host tools and official descriptor | Build succeeds; descriptor has 9 passing JVM tests |
| Generator host regressions | 813 pass |
| Package-task regressions | 24 pass |
| .NET workbench safeguards | 14 pass, including explicit parent-path and malformed JSON guards |
| Authored Java -> semantic Android binding -> separate C# consumer | Builds with zero warnings/errors |
| New package inspection | 147 entries; private tools, no runtime/compiler transitive payload or bundled MSBuild host assemblies |
| C# no-device package fixture | 15 passing command stages; exact expected invalid-mode diagnostic failure |
| Installed workload | Sampled file hashes unchanged |
| Evaluated host-tool invocation | `tool class-parse -- --help` succeeds |

The C# fixture verifies raw parser facts, official descriptor/semantic reports,
metadata and namespace transforms, scoped consumer rules in the binding AAR,
no-op stability, missing-source regeneration, metadata/native invalidation,
Legacy/Semantic switching, Android/non-Android multi-targeting, design-time
compilation and strict multithreaded tasks. It additionally checks that the
private compiler does not leak into downstream consumer assets.

The hello and fixture use NuGet caches keyed by the actual package hash and
verify restored nupkg bytes against the feed. A stale package under the same
development ID/version cannot satisfy validation.

## Python-blocking proof

Every workflow above placed per-check fail-fast Python-name shims first in
child `PATH`. No unexpected Python invocation was recorded. The .NET tests
include a deliberate negative control: requesting `python3` must execute the
blocking shim, return 127 and be reported as blocked, without running a Python
interpreter. The runner resolves executable names against the actual child
PATH so the control does not accidentally use the parent PATH.

[Normalized fixture and blocking evidence](dotnet-package-fixture.json)
records the 15 stages, workload hashes and six workflow log checks without
machine-local paths. Raw logs remain under ignored `.artifacts/logs/`.
This is PATH-based verification, not a sandbox against arbitrary absolute
interpreter paths.

The **22 original Python corpus tests were not run**. Their exact original
sources and prior results remain archived/historical rather than being
silently deleted or relabeled as .NET tests.

## Package and source provenance

```text
.artifacts/feed/Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.2.nupkg
11027854 bytes
SHA256 59b3ce130f71fe16c1f0ff650c1088cdd4b1083ef217e2f569036686abb47d00

.artifacts/feed/Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.2.provenance.json
SHA256 d1208d47f0b01827b1a7706ff689104459d6edf9df56a3c5a860e712f4e01248

upstream/manifest.json (unchanged original)
SHA256 cb21a0ee236a38270089abce21b9abf0b6aec85475d0a6528ab29cee4a6f0e91

provenance/archived-python.json
SHA256 209acbef432f5a94a4ab3287beed1af012b9be226f7809daf8e5be729bde55d5
```

The matching provenance embeds the original source manifest and hashes the
C# runner files, SDK selection and archive mapping. There are no source
integration patches; only the package version changes for this distinct
workbench snapshot. Repacking may change archive metadata, so use provenance
from the specific run rather than promising cross-machine identical nupkg bytes.

All 33 formerly tracked Python files are preserved externally, including 13
source additions, 18 historical helpers and the former runner/tests.
See [archive integrity and publication](../python-archive.md).

## Not revalidated

No fresh Kotlin runtime pack, MAUI/device pilot, commercial-service calls,
whole-corpus regeneration, application R8 execution or NativeAOT execution.
MAUI compatibility remains a compiler CLI/verification pilot, not an automatic
public MSBuild profile switch. Windows/Linux are not certified by this run.
The original source archive is published and its default HTTP path is verified.
Git publication and the new package release remain coordinating-session actions,
not side effects of the workbench validation commands.
