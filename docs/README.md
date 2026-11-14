# Research, code and evidence

Read [current state](CURRENT-STATE.md) first, then the
[current .NET workbench validation](results/dotnet-workbench-validation.md). The older notes
are preserved research, not a list of supported product features.

## Capability inventory

| Area | Current status | Where to look |
| --- | --- | --- |
| Semantic Java/Kotlin compiler, parser and official descriptor | Fresh host build/tests pass | [source map](../upstream/README.md), [commands](../eng/README.md) |
| Project-local compiler NuGet | Fresh .NET-only pack, authored binding/consumer and 15-stage installed-workload fixture pass | [validation](results/dotnet-workbench-validation.md), [hello](../samples/hello/) |
| Canonical owners and real implementation splitting | Current host tool built; generator regressions exercise executable cross-assembly IL and registry behavior | [emitter source](../upstream/additions/external/Java.Interop/tools/grouped-bindings/), [historical corpus results](results/maven-group-corpus-results.md) |
| Per-Maven-artifact assemblies | Current corpus default; historical MapLibre/Sentry/Koin graphs demonstrate it | [corpus source](../upstream/additions/tests/BindingProjection/Corpus/), [asset scopes](../release-assets/assets.json) |
| Kotlin runtime, typed views, Task/Flow and native bridge | Source preserved; historical runtime nupkg and device evidence retained; not rebuilt in the handoff | [runtime source](../upstream/additions/src/Microsoft.Android.Kotlin/), [Kotlin results](results/kotlin-production-results.md) |
| Compiled MAUI full/usage-selected contracts | Current host tool built and covered by generator tests; full native/MAUI pilot is historical, not rerun here | [profile tool](../upstream/additions/external/Java.Interop/tools/binding-compatibility/), [pilot result](results/maui-protected-compatibility-results.md) |
| Full configured native corpus | Exact 717-record input, resolutions, coverage and alternatives preserved; not all regenerated in newest artifact layout | [inputs](../inputs/), [coverage evidence](../evidence/full-config-corpus/), [report](results/full-config-corpus-results.md) |
| Third-party JNI/Debug/R8/NativeAOT proofs | Historical bounded offline results, not commercial service validation or a fresh device run | [profiles/evidence](../evidence/third-party/), [assets](../release-assets/README.md) |
| General drop-in compatibility and arbitrary old/new graphs | Not implemented or certified | [limits](CURRENT-STATE.md), [migration proposal](research/net12-binding-migration-plan.md) |

## How the direction changed

The [initial exploration](research/android-bindings-future.md) and
[architecture recommendation](research/android-binding-system-recommendation.md)
compare several alternatives, including local non-owning views and shared
contracts. They are design history, not the final package interface.

The [full compiler spike](research/full-binding-compiler-spike.md),
[projection design](research/binding-projection-generation.md), and
[canonical contract research](research/canonical-binding-contracts.md) explain
the compiler experiments and ownership constraints. The implemented package
provides an opt-in compiler over the normal native input graph.

The corpus work progressed from an incomplete AndroidX/GMS subset, through
full-configuration accounting and group-wide assemblies, to stable
per-artifact ownership. An older complete group-layout archive must not be
renamed or mixed into a newer per-artifact graph. Native alternatives remain
separate graphs.

The [migration plan](research/net12-binding-migration-plan.md) distinguishes
compiler adoption, additive conveniences, CLR contract changes and native
graph changes. Its broader coexistence/transition ideas remain proposals.
The later [MAUI pilot](results/maui-protected-compatibility-results.md) proves
one frozen-consumer compatibility path, not arbitrary contract reconstruction.
The profile switch is CLI-only; no automatic MAUI MSBuild profile property
was added to the package.

## Preservation

[report-index.json](report-index.json) identifies the imported narrative notes.
[source handoff](source-handoff.md) preserves export context.
[content-inventory.json](../provenance/content-inventory.json) accounts for
277 source files, 190 curated text files and 18 external historical assets.
The original 33 Python files are now in a
[checksummed external archive](python-archive.md), not in the tracked tree.
All original source/curated manifest entries remain accounted for.
The authoritative source/text hashes are in the corresponding manifests.

Historical commands retain their original semantics. Machine paths were
replaced by placeholders such as `${ANDROID_SOURCE}`; these are not necessarily
environment-variable expansion inside an archived script. Use the .NET command guide,
not a pasted historical command, for the minimal runnable path.
