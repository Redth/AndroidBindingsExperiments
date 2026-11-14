# Historical Python workbench validation

This records the original Python-runner handoff, not the current workflow.
See [the .NET-only validation](dotnet-workbench-validation.md) for the replacement.
The original runner is preserved only in the external source archive.

These commands were run for this repository handoff on **14 November 2026**.
They are new results, separate from the imported historical reports.

Environment: macOS ARM64, Python 3.14.6, .NET SDK **11.0.100**, installed
.NET 10 runtime, JDK **21.0.12**, Android workload **37.2.31** and an existing
Android SDK. No workload was installed, replaced or copied from the original
experiment. Public dependency feeds and normal user caches were available.

## What was rerun

Commands below run from the workbench root:

| Command | Observed result |
| --- | --- |
| `python3 eng/workbench.py doctor` | Required tools found; selected SDK 11.0.100 |
| `python3 eng/workbench.py verify-preservation` | Source payload and all 190 curated text hashes match; 18 external historical asset records agree with their checksum index |
| `python3 eng/workbench.py bootstrap` | Fresh shallow fetch of the exact source pin; all 91 patched files and 186 additions verified |
| `python3 eng/workbench.py --source .artifacts/clean-checkout bootstrap` | A second independent materialization also verifies all 277 files |
| Repeating both bootstrap commands | Existing source verified without overwriting it |
| `python3 eng/workbench.py build` | All four host tools build; descriptor `test installDist` succeeds with 9 JVM tests |
| `python3 eng/workbench.py test` | 813 generator, 24 package-task, 22 corpus Python and 8 workbench tests pass |
| `python3 eng/workbench.py pack` | Current compiler preview packed and inspected: 147 entries, build-only tooling and no transitive compiler/runtime payload |
| `python3 eng/workbench.py generate` | Authored Java JAR -> semantic Android binding -> separate C# consumer; zero warnings/errors |
| `python3 eng/workbench.py verify-package --work-dir .artifacts/package-fixture-final` | All 15 installed-workload fixture stages pass; sampled workload files remain unchanged |
| `python3 eng/workbench.py tool class-parse -- --help` | Evaluated output discovery and host invocation work |

`JAVA_HOME` and `ANDROID_HOME` supplied the fixture's existing JDK/Android SDK
locations. Both `generate` and `verify-package` restore into a cache keyed by
the actual nupkg SHA-256, then check the restored nupkg bytes against the feed.
This avoids accidentally validating an earlier locally repacked package under
the same workbench ID/version.

The fixture exercises initial generation, no-op output stability, missing-source
regeneration, metadata/native invalidation, Legacy/Semantic switching, explicit
invalid-mode diagnostics, Android/non-Android multi-targeting, design-time
compilation and strict multithreaded tasks. It also checks scoped consumer
R8 rules in the binding AAR and the unchanged installed workload.
The invalid-mode probe intentionally exits nonzero; that is an expected
diagnostic test, not a failed handoff.

[Normalized fixture evidence](workbench-package-fixture.json) retains the exact
stages, commands, results, timings and workload hashes with local paths replaced
by explicit placeholders. Timings are observations, not performance benchmarks.
Full raw logs and build outputs remain under ignored `.artifacts/`.

## Local deliverables

The freshly validated compiler is:

```text
.artifacts/feed/Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.1.nupkg
SHA256 7f3c28f0dd0916264bb83fe621dd08c56d8a58d51261fe9ff063fef0d0714e8c
11027644 bytes
```

Its matching source/package provenance is:

```text
.artifacts/feed/Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.1.provenance.json
SHA256 5198f770e9dcabc5118df71de29cb5c68284c266d580ad03fbb8c729de8b3fda
```

The provenance embeds the preserved source manifest, the source commit,
package hash, runner hash and SDK-selection hash. The preserved source
manifest's byte hash is:

```text
upstream/manifest.json
SHA256 cb21a0ee236a38270089abce21b9abf0b6aec85475d0a6528ab29cee4a6f0e91
```

No source-integration patch was required. The source remains exact; the
workbench supplies the runner/sample and overrides only the compiler package
version. Source provenance checks reject unexpected tracked or untracked source
edits when packing.

Rebuilding later may produce different archive bytes because of build/pack
metadata. Use the provenance emitted by that run, rather than expecting this
inspection nupkg's byte hash to be a cross-machine reproducible-build promise.

## What this does not prove

No fresh Android device/JNI, Kotlin runtime packaging, MAUI app/pilot,
second-package-version upgrade, whole-corpus regeneration, R8 app execution
or NativeAOT app execution was performed for this handoff. The package fixture
checks consumer-rule packaging, not execution of an R8-processed app.

Historical reports still preserve their bounded device results. In particular,
the MAUI profile extraction/verification CLI is current source and its host
tests pass, but there is no automatic MAUI-profile MSBuild switch in the package.
The fresh result does not certify Windows/Linux, every Android 37.x workload,
arbitrary historical CLR contracts or incompatible native graph combinations.

The full configured corpus and its mutually exclusive alternatives remain
preserved inputs/evidence, not a newly rebuilt 717-record per-artifact tree.
Historical inspection assets are indexed but not stored in git or published by
the local validation commands.
