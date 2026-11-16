# Workbench validation

Fresh results from **16 November 2026** for the current self-contained source
snapshot. Earlier reports under `historical-*.md` describe their own revisions,
not commands or counts to apply to this snapshot.

Environment: macOS ARM64, .NET SDK **11.0.100**, installed .NET 10 runtime,
JDK **21.0.12**, existing Android SDK and Android workload **37.2.31**.
No SDK/workload installation or installed-workload modification was needed.

## Commands run

A new independent source checkout was used; no source archive, local override,
download cache or previous prepared SDK was an input:

```sh
dotnet run --project eng/Workbench -- doctor
dotnet run --project eng/Workbench -- verify-preservation
dotnet run --project eng/Workbench -- --source .artifacts/practical-source bootstrap
dotnet run --project eng/Workbench -- --source .artifacts/practical-source build
dotnet run --project eng/Workbench -- --source .artifacts/practical-source test
dotnet run --project eng/Workbench -- --source .artifacts/practical-source pack
dotnet run --project eng/Workbench -- generate
dotnet run --project eng/Workbench -- --source .artifacts/practical-source verify-package \
  --work-dir .artifacts/practical-package-check-final
```

`JAVA_HOME` and `ANDROID_HOME` selected the existing JDK/Android SDK. Repeating
bootstrap on that checkout verified it without overwriting source.

| Check | Result |
| --- | --- |
| Pinned upstream + tracked patch + local additions | All 91 tracked outputs and 173 additions verify |
| Current curated files | All 172 hashes verify; historical 18-asset checksum index remains intact |
| Four host tools | Build succeeds |
| Official Kotlin/JVM descriptor | 9 tests pass |
| Generator regressions | 813 pass |
| Package-task regressions | 24 pass |
| Workbench safeguards | 9 pass |
| Compiler package | 147 entries; tooling stays private and no MSBuild host assemblies are bundled |
| Authored Java -> generated binding -> separate C# consumer | Builds with zero warnings/errors |
| Full no-device C# package fixture | 15 command stages pass, including the expected invalid-selection diagnostic |

Safeguards retain path/JSON guards, hash-tampering rejection, exact inventories,
nonempty-source refusal, canonical/declared CRLF hashes, symlink rejection,
source/package provenance checks, restored nupkg byte checks and child exit-code
propagation. Tests of removed archival/interpreter machinery are not retained
or counted.

The package fixture checks raw parser facts, official descriptor/semantic
reports, metadata/namespace transforms, scoped consumer rules in the binding
AAR, no-op output stability, missing-source regeneration, metadata/native
invalidation, Legacy/Semantic switching, multi-targeting, design-time
compilation and strict multithreaded tasks. It also checks project-private
compiler isolation and unchanged installed workload file hashes.

Both sample and fixture use content-addressed NuGet caches and verify that the
restored compiler bytes equal the selected local package. Normalized
[fixture evidence](workbench-package-check.json) records the exact stages,
commands and workload hashes without machine-specific paths. Full logs remain
under ignored `.artifacts/logs/`.

The main README's complete binding-project snippet matches the validated hello
project. Its Maven/Gradle snippets were checked against the pinned SDK item
documentation and compiler input targets, but are explicitly illustrative:
no new Maven-resolution or Gradle-module integration run is claimed here.

## Deliverable hashes

```text
.artifacts/feed/Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.3.nupkg
11027947 bytes
SHA256 af9475310d1d4e0eae85f949a5e50aaf20742ab545b14d376fdf3188a03933cc

.artifacts/feed/Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.3.provenance.json
76933 bytes
SHA256 2a8fa90d3c4799712e0dccb6da539b3721b2d1189a91733d03ae1230d6067fe7

upstream/manifest.json
SHA256 3723a0af59a0aeeb6856cfd3cd144d4ecce3765f2d4c145ec7692b98e33d8782

provenance/curated-files.json
SHA256 0ee4440c1297bb6d0ec4ea11cd9bf9aaf4b78e8e3acc44915a82603f6d3479de

provenance/snapshot-change.json
SHA256 8c53e5d31f6f1f494903f6a62942814cf59af171ed202f0c7175cf6d176b1466
```

The current source manifest honestly describes **264**, not the previous 277,
files. `snapshot-change.json` records the prior revision/manifest hashes and
the 13 omitted research drivers; the curated inventory similarly excludes
18 retired helpers. Retained compiler/runtime/native fixture bytes and the
tracked patch are unchanged. Existing Git history and published releases remain
untouched and are not dependencies of the current bootstrap.

The current [workbench release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/workbench-2026-11-16)
uses `workbench.3`, distinct from historical `workbench.2`.
Its matching provenance includes the current manifest, source
pin, snapshot-change hash, runner source hashes and SDK selection. Later pack
metadata may produce different nupkg bytes; verify against that run's provenance.

## Limits

No fresh device, Kotlin runtime packaging, MAUI app/pilot, full native corpus,
commercial-service, app R8 or NativeAOT run. Compatibility profiles remain a
bounded CLI pilot, not automatic native/CLR compatibility or a public MAUI
MSBuild switch. Windows/Linux and every Android workload revision are not
certified by this run.

Validation commands do not commit, push, rewrite history or mutate releases.
