# Complete Maven-group binding assemblies

> **Historical AndroidX/GMS subset:** This report and its DLL ZIPs cover 444
> records. The full configuration has now been expanded to 717 records,
> including 59 Firebase records. See `full-config-corpus-results.md` for current
> input coverage. The expanded DLL set has not yet been regenerated.

**All 444 original corpus records are accounted for. The generated implementation
DLLs are compiled, verified, and packaged.** This replaces the earlier
conflict-only report; the native conflicts were handled with compatible profiles,
not by dropping roots or choosing arbitrary duplicate classes.

## What to open

The inspection bundles and indexes are in:

`${EXPERIMENT_ARTIFACTS}/maven-group-delivery/`

| Bundle | Original records | Binding DLLs | Managed types |
|---|---:|---:|---:|
| `maven-group-primary.zip` | 435 | 127 | 91,491 |
| `maven-group-alternate-02.zip` | 6 | 58 | 26,397 |
| `maven-group-play-services-ads-lite.zip` | 2 | 26 | 5,734 |
| `maven-group-play-services-gass.zip` | 1 | 28 | 4,527 |

DLL counts include `Android.Maven.BindingSupport.dll`, but not the additional
canonical `Microsoft.Android.Kotlin.dll` included in each bundle. Type counts
include raw peers, typed views, invokers and implementation helpers; they are not
counts of supported native APIs. Profiles share many dependencies, so their
counts must not be added and described as unique APIs.

Start with **primary**. Its 126 native Maven groups each have one binding
assembly, plus the managed support assembly. The Drive/TagManager/Ads Base
alternative and the isolated Ads Lite/GASS sets are separate complete native
cohorts. **Never mix DLLs from different profiles.** Some identities intentionally
occur in more than one profile with different dependency versions or APIs.

Each bundle contains real implementation DLLs, the canonical managed runtime,
ownership/reference verification, selected native coordinates and hashes,
assembly indexes, and C# usage examples. It contains **no JAR/AAR payloads,
temporary bootstrap DLL, or reference-only assembly set**. All binding DLLs also
have an empty embedded-resource inventory. An app still needs the corresponding
resolved native graph and source-built Kotlin bridge.

`maven-group-catalog.zip` contains `assembly-index.csv`, `corpus-records.csv`,
`catalog.json`, and `validation.json`. These map every original record and Maven
group to the actual profile, selected version, DLL and output status.

### Health Connect

The primary bundle contains:

`assemblies/Android.Maven.Androidx.Health.Connect.dll`

This is a real 7,049,216-byte implementation DLL covering the Maven group,
including the Connect client and its same-group protocol dependencies. Its
other-group dependencies are alongside it in the primary bundle. The unpacked
DLL is also available under
`maven-group-assemblies/primary/assemblies/` in this session's files.

## Inputs and coverage

The input remains the AndroidX/GMS portion of
[`dotnet/android-libraries/config.json` at the pinned revision](https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/config.json).
It contains 444 records, 433 distinct group/artifact coordinates and 94 root
Maven groups. Gradle also resolves their transitive support libraries.

The selected-version policy follows the highest requested versions, with
transitive resolution and five reviewed Android/JVM sibling aliases. Seventeen
records are superseded by a selected version or explicit variant. Every original
record still has exactly one catalog entry; these are not 444 separate DLLs for
every originally requested version.

| Per-record outcome | Records |
|---|---:|
| Native types projected into a Maven-group DLL | 363 |
| Native archive with no JVM classes | 24 |
| Resolved module without its own native archive | 56 |
| Only package annotations, no bindable type (`androidx.work:work-gcm`) | 1 |

No empty binding DLLs were manufactured for metadata/resource-only records.
`work-gcm:2.12.0` contains only `package-info.class`, not an omitted public class.

The incompatible all-roots graph had 1,873 duplicate JVM names and failed an
actual D8 duplicate-type reproduction. The four profiles each pass native
ownership auditing. Ads Lite and GASS were re-resolved independently where
globally selected dependency versions broke their own closures.

There are no per-library metadata/API fixup transforms in these generated
projects. The compiler uses native class facts, official Kotlin metadata/ASM
descriptors, accessibility policy and shared semantic projection rules. The
five explicit variant aliases and compatibility profiles are recorded resolution
policy, not inferred equivalence between unrelated SDKs.

## What changed to finish the full corpus

The generator now handles the additional inherited property/default-interface,
callback deduplication, generic signature, enclosing visibility, typed marker
and Kotlin default-argument collection cases found by the larger graph.
Descriptor JSON and immutable XML reports no longer require giant in-memory
scalar strings.

The largest graph also exceeded the CLR's per-assembly string-literal limit.
Instead of cutting APIs or enabling an experimental language feature, the
pipeline now compiles canonical reference contracts first, then compiles real
implementations separately for each Maven group. This supports circular assembly
references without circular C# project builds. Reference contracts are
intermediates only and are explicitly rejected as deliverables.

The smaller profiles use the also-verified implementation relocation path.
Both paths preserve native ownership, internal bridges and JNI connector
assembly identities. Each implementation has a content-derived module identity;
verification checks unique owners and resolves group-owned type/member references.

## Actual validation

All four profile implementation builds finished with **zero C# errors**.
The primary verifier checked 91,491 type owners, 6,219 owned type references
and 44,550 owned member references. The other three sets also passed.

- `./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj -v:q -nr:false`: **745 passed**.
- `python3 -m unittest discover -s tests/BindingProjection/Corpus -p 'test_*.py' -v`: **10 passed**.
- `bash build-tools/gradle/gradlew --no-daemon --console plain -p external/Java.Interop/tools/jvm-api-descriptor test installDist`: **passed**, with **8 descriptor tests**.
- `./dotnet-local.sh build tests/BindingProjection/Corpus/Consumer/GroupedConsumer.csproj -c Debug -v:q -nr:false -p:_AndroidGroupedAssemblyDirectory=<profile>/assemblies`: **passed with zero warnings/errors against both complete primary and alternate sets**.
- The representative 24-assembly JNI/generic app passed **19/19 checks in Debug,
  Release/full trimming/R8 and NativeAOT**, separately for both implementation
  paths. The evidence includes exact commands, APK hashes and per-check results.
- `git diff --check`: **passed**. Both owned test emulators were stopped.

Exact profile-specific build and runtime commands are preserved in
`validation.json`. The generated whole-graph/reference/source-group compilation
disables SDK analyzers to bound memory; normal C# diagnostics remain enabled.
The ordinary consumer and app probes use normal analyzer settings.

**Limits:** These are experimental inspection outputs, not published production
NuGets. Generated bindings still produce warnings, including obsolete/restricted
infrastructure, nullability and hidden-member warnings. The runtime evidence
covers representative real workflows, not every API in all 444 records. This
does not establish binary compatibility with arbitrary future Maven graphs.

## Reproduce and inspect the C# examples

The reproducible pipeline is
`tests/BindingProjection/Corpus/generate-assemblies.py`; its README documents
resolution, profiles, canonical source-group compilation and the optional compact
implementation relocation path. The default source-group path avoids the
single-assembly string limit.

Usage examples are in each ZIP's `examples/` folder and in the repository:

- `tests/BindingProjection/Corpus/Consumer/GroupedConsumer.cs`
- `tests/BindingProjection/Corpus/Smoke/GroupedSmokeActivity.cs`
- `tests/BindingProjection/App/GenericProjectionChecks.cs`

The last file contains the actual Task/Flow/generic checks run on Android.
No commits, pushes or pull requests were created.
