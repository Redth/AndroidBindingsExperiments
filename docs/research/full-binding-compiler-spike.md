# Full-surface binding compiler spike: results and recommendation

## Result

The first opt-in semantic compiler front-end is implemented and compared against
the current generator using identical inputs. It produces compilable Activity
bindings without Activity-specific hand-authored metadata, restores additional
singleton fields, and recovers Kotlin value-class information that the current
pipeline loses between class parsing and generation.

| Pilot | Current pipeline | Semantic spike |
|---|---|---|
| AndroidX Activity 1.11.0 | 34 C# errors; 28 registered fields | 0 C# errors; 39 registered fields |
| AndroidX Fragment 1.8.9 | 0 C# errors; 23 registered fields | 0 C# errors; 24 registered fields |
| Real Kotlin value-class fixture | 0 value structs; 2 erased `Tint` overloads | 3 value structs; 3 distinctly typed `Tint` overloads |

Both Kotlin outputs compile. Activity retains all 75 registered JVM types and
255 method registrations; Fragment retains all 42 types and 451 method
registrations. Neither AndroidX pilot loses a registered type, method, or field.
These counts are registration inventories, not an exhaustive CLR public API diff.

The complete generator suite passed **496/496 tests**, with no skipped tests.
The generated Activity assembly also passed **13/13 checks on Android API 35**,
including Java-to-managed overrides and nonrecursive base dispatch.
The device run is Debug, not a Release/trimming/NativeAOT validation of this
compiler spike. Earlier runtime-mode results from the separate shared-peer
experiment should not be attributed to this compiler.

**This is not yet a new independent binding generator.** It is a new immutable
fact model and explicit projection planner with a lowering adapter into the
existing Java resolver/C# peer-emission pipeline. That separation was deliberate:
it lets us measure improvements without simultaneously replacing JNI dispatch,
callback emission, Java resolution, and the entire emitter.

## What changed

### 1. Preserve binary facts before destructive normalization

`BindingCompilation` snapshots the complete raw class-parse XML before the
existing Java resolver can mutate or prune it. Its typed read-only definitions
retain type/method/parameter/field attributes and exact JVM method descriptors.
The original XML is also retained, including facts not yet strongly modeled.

Malformed method descriptors, inconsistent parameter counts, and duplicate JVM
identities are rejected rather than silently merged. A separate projection plan
records override, naming, and opaque-field decisions. A `.semantics.json` file
beside adjusted XML preserves facts and decisions for inspection.

This is the beginning of the proposed compiler model, not a complete normalized
Kotlin declaration/type system.

### 2. Separate managed override shape from the actual JNI slot

Activity's concrete result contracts specialize generic inputs and outputs.
Their JVM classes contain typed implementations plus synthetic erased bridges.
The legacy exporter drops bridge methods, while the managed base contract uses
erased `Java.Lang.Object` signatures. That leaves generated concrete classes
unable to implement their managed abstract base members: the observed 34 errors.

The planner requires actual bridge evidence, resolves the managed override
shape against the base contract, and preserves the typed native descriptor.
It handles both scalar and array generic instantiations and checks pointer/
primitive ABI compatibility. Unsupported ABI-changing shapes require an adapter
and fail explicitly.

The Android probe verifies both directions: managed-to-Java calls through these
slots and Java-to-managed overrides, including calls back to their base methods.
We do not generate erased Java-source overrides that clash under javac erasure.

**Tradeoff:** some C# override signatures are now correctly erased rather than
fully typed. This fixes concrete output but does not yet deliver the desirable
generic C# facade. Pointer ABI compatibility alone is not a comprehensive argument
type-safety policy; checked conversions and invalid-input coverage are still
needed before making this a production projection contract.

### 3. Recover Kotlin value-class semantics already understood upstream

The existing classfile tooling already detects primitive-backed Kotlin value
classes. The existing emitter already has support for readonly struct projections
and typed overload recovery. The intervening XML exporter discards the
`kotlin-*` projection attributes.

The spike retains those facts and restores them by exact owner/name/descriptor
identity after adjustment. A new regression test takes actual compiled Kotlin
classfiles through resolution/export before generating C#, rather than
bypassing the stage where information was lost.

In the complete-pipeline comparison:

```csharp
// Current output: underlying JVM representations collide.
Tint (long color);
Tint (float dp);

// Semantic output: distinct Kotlin identities survive.
Tint (MyColor color);
Tint (MyAlpha alpha);
Tint (MyDp dp);
```

`MyColor`, `MyAlpha`, and `MyDp` become readonly structs. This reuses existing
metadata decoding and struct emission; it is not a newly implemented official
Kotlin metadata decoder. The fixture therefore shows recovered pipeline
capability, not complete support for every Kotlin value-class use.

The Kotlin registration inventory intentionally changes from four registered
types to one, and from nine method registrations to seven: three peer classes
become structs, three peer method registrations disappear, and one additional
typed overload survives. Do not interpret that representation change as a
binary-compatible full API equivalence claim.

### 4. Retain companion/singleton fields without corrupting native lookup

When a public nested type and a singleton field have the same name, the planner
keeps the type and gives the field a stable managed name such as
`CompanionInstance`, avoiding other known names with numeric suffixes.

Where a public field exposes a known private JVM class, the spike can project an
opaque `Java.Lang.Object` while preserving the field's actual JNI descriptor.
Both getter and setter use the native descriptor; reconstructing it from the
projected managed type would look up the wrong field.

Activity gains 11 registered field accessors; Fragment gains one. These are
**recovered fields, not additional recovered classes**. Getter/setter descriptor
handling has regression coverage; public and opaque companion getters were
also exercised on Android.

## Comparison method and performance

The repeatable driver is:

`external/Java.Interop/tests/generator-Tests/compare-bindings.py`

It downloads fixed Activity/Fragment versions, checks the pinned Activity
archive hash, records downloaded archive/nuspec/reference hashes, and restores
the matching published NuGet dependency groups. This is intentionally a
**published managed dependency graph**, not the Maven minimum graph.
The primary published binding DLL is excluded from its own reference set.

Each mode uses the same raw class-parse XML, generator executable, reference
DLLs, C# compiler, and compiler arguments. No Activity/Fragment-specific metadata
transforms were added. SDK input-build failures are recorded, not misreported
as success; generated C# is compiled independently to avoid stale/duplicate SDK
generated sources.

| Pilot | Current generation median | Semantic generation median |
|---|---:|---:|
| Activity | 1.241 s | 1.244 s |
| Fragment | 1.406 s | 1.466 s |
| Kotlin fixture | 0.903 s | 0.909 s |

These are three measured fresh-process invocations per mode, after one discarded
warm-up invocation, alternating execution order and running pilots sequentially.
They include process startup with warm filesystem caches. They do **not** measure
clean application build time, incremental performance, restore, or a large
AndroidX dependency closure. They support continuing the experiment, not a
general claim that semantic compilation is free.

Warnings remain visible. Activity still reports legacy `BG8605`/`BG8606`
resolution diagnostics associated with names containing `$`; Fragment retains
its unresolved/pruned anonymous-class diagnostics. Existing C# nullability,
hiding, and obsolete-API warnings remain. More recovered Activity companion
accessors also expose more existing obsolete APIs.

## Reproduce and inspect the evidence

Run from the repository root:

```sh
export NUGET_PACKAGES=${EXPERIMENT_ARTIFACTS}/nuget-packages

./dotnet-local.sh build external/Java.Interop/tools/generator/generator.csproj --no-restore -v:quiet
./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj --no-restore -v:minimal

python3 external/Java.Interop/tests/generator-Tests/compare-bindings.py \
  --pilot activity --output /absolute/path/to/evidence --device-serial emulator-5570
python3 external/Java.Interop/tests/generator-Tests/compare-bindings.py \
  --pilot fragment --output /absolute/path/to/evidence
python3 external/Java.Interop/tests/generator-Tests/compare-bindings.py \
  --pilot kotlin --output /absolute/path/to/evidence
```

Actual outcomes: generator build passed with zero warnings/errors; full test
suite passed 496/496; all three final comparison commands exited successfully;
the Activity command included 13/13 successful Android checks.
`ANDROID_HOME` must identify the SDK and the selected device must be running
for the device command. The probe installs `com.dotnet.bindingcompiler.spike`.
The session-owned emulator was stopped after validation.

The local SDK and generator were built before these comparisons. Host tooling
targets `net10.0`; generated bindings target the local .NET 11 Android surface.
The host SDK version and exact compiler/generator/reference identity are saved
with each run.

Final evidence root:

`files/full-binding-spike/final-comparisons/`

| Pilot | Evidence subdirectory |
|---|---|
| Activity | `activity-20261028T170502Z-0603f272/` |
| Fragment | `fragment-20261028T170535Z-36aa7b21/` |
| Kotlin | `kotlin-20261028T170551Z-bf53b6d5/` |

Each contains `comparison.json`, detailed commands/results, generated source,
input and output XML, diagnostics, hashes, inventories, and dependency inputs.
Activity's successful device results are under `device-fbc9d909/results.txt`.
The suite log is `files/full-binding-spike/all-generator-tests.log`.

Repository documentation:

`external/Java.Interop/tests/generator-Tests/README.md`

Direct invocation adds `--experimental-semantic-model` to the normal generator
command against raw class-parse input. Default behavior is unchanged; already
adjusted input is rejected in semantic mode.

## Recommendation for the next implementation slice

Continue the semantic-front-end approach before investing in a new emitter.
The first comparison found real failures caused by information loss and missing
projection decisions, not by an inability to write C# text.

The next substantive step should make Kotlin declarations and generic
substitution first-class in the model, with an explicit supported metadata-version
policy and an official-metadata-reader feasibility test. Preserve separate
binary-slot, source/Kotlin, managed-contract, and invocation-conversion identities.
Make every pruning/adaptation decision explainable and test full generated
compilation, not just XML fragments.

Then expand the corpus across Activity, Fragment, Lifecycle, SavedState,
collections, and coroutine-heavy APIs, including Kotlin-version and library-version
matrices. Build modern typed generic facades and checked conversions on those
facts before layering `suspend`/`Task`, cancellation, `Flow`, default arguments,
extension methods, boxed value classes, and richer property projection.

Those features are **not implemented in this spike**. Neither is source-generation
integration, a new full emitter, assembly-ownership unification, Gradle dependency
resolution, or a production compatibility promise. The successful result is a
measured, opt-in foundation with concrete output improvements and reproducible
comparison machinery, rather than a claim of automatically perfect bindings.
