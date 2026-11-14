# Kotlin conveniences, assembly grouping, and source size

## Result in plain language

The next compiler slice is implemented as an opt-in, runnable prototype.
Kotlin suspend functions can now have C# methods you can `await`, and Kotlin
Flow can be consumed with `await foreach`.

The important part is not just nicer method names: cancellation reaches the
actual Kotlin operation, exceptions reach the C# caller, and leaving a Flow loop
stops collection and waits for Kotlin cleanup. Existing low-level bindings are
preserved.

This is not yet a shipped runtime or a complete Kotlin language projection.
Complex result types still need more projection work, and Flow currently needs
an explicit element converter for typed enumeration.

## Implemented

The parser records suspend status, simple result classifiers, and nullability
from actual Kotlin metadata. It does not guess that an arbitrary method taking
a `Continuation` must be a suspend function.

`--experimental-kotlin-async`, together with
`--experimental-semantic-model`, enables additive adapters in the existing
`XAJavaInterop1` backend:

- Non-null Kotlin `Unit` becomes `Task`.
- Strings and all eight JVM primitives become `Task<T>`, including nullable
  variants.
- Complex/generic/value-class results conservatively remain raw object peers.
- Existing native methods named `FooAsync` are not overwritten. Conflicting
  conveniences use `FooKotlinAsync`, preserving overload groups.
- Each generated method delegates to shared runtime code rather than emitting
  its own coroutine state machine or calling `Task.Run`.

The shared runtime has a small Kotlin source bridge and a C# adapter. The
prototype builds the Kotlin source through the existing Gradle test fixture.
Production integration should compile that shared source once in the app's
Gradle graph, not distribute another copy of an ecosystem library's binary or
emit helper classes in every binding assembly.

The managed helper is currently test-fixture source, not a new shipped package:
`external/Java.Interop/tests/generator-Tests/TestInputs/SemanticDeviceProbe/KotlinCoroutines.cs`.
The Kotlin bridge is in the existing Bytecode-Tests `kotlin-gradle` fixture.
Published Kotlin stdlib/coroutines bindings still supply the managed Kotlin
types. The new Gradle coroutine dependency is test-only; SDK tools do not
acquire a shipped coroutine dependency.

Example:

```csharp
using Java.Interop.Kotlin;

string text = await fixture.DelayedAsync (cancellationToken);
int number = await fixture.NumberAsync (cancellationToken);
await fixture.UnitAsync (cancellationToken);

using var flow = fixture.Finite ();
await foreach (string? item in flow.AsAsyncEnumerable (
    KotlinCoroutines.GetNullableString, cancellationToken)) {
    // Null is a legitimate item, not the end of the stream.
}
```

### Cancellation, threading, Flow, and ownership

Kotlin handles its own suspend/result ABI. A `CancellationToken` cancels the
owned native coroutine; cancellation is reported after the coroutine finishes
and its `finally` blocks run. It is cooperative cancellation, not forced
interruption of arbitrary native code.

The optional `coroutineContext` parameter supports a native dispatcher and
parent Job. The bridge owns a supervised child scope: cancellation propagates
from parent to child, but a failed Task does not cancel the caller's Job.

Without an explicit native context, `Dispatchers.Unconfined` starts execution
on the calling thread but does not guarantee main-thread resumption. A .NET
synchronization context is not automatically a Kotlin dispatcher. UI-sensitive
native code must receive the appropriate context; native Main dispatch also
requires the normal coroutines-android module.

Each Flow enumeration starts collection lazily. A rendezvous channel plus an
acknowledgement prevents the producer advancing while C# is processing its
current item. There is no unbounded queue or implicit prefetch. Native
StateFlow/SharedFlow conflation and replay rules are not replaced with managed
event semantics.

Early `break`, consumer conversion failure, token cancellation, and native
parent cancellation cancel and join collection, including native cleanup.
Cleanup does not reuse an already-cancelled consumer token. Null items remain
distinct from end-of-stream. Returned existing peers are not indiscriminately
disposed.

## Actual validation

The final fixture comparison is:
`kotlin-async/async-20261029T135106Z-4883427d/comparison.json`,
relative to this session's `files` directory. Legacy and semantic outputs both
compile without C# errors and retain the same 7 registered types and 45 method
registrations. The async option adds managed conveniences, not new JNI slots.

| Android configuration | Result |
|---|---:|
| Debug/CoreCLR | 25/25 |
| Release, full trimming, R8 | 25/25 |
| Release NativeAOT, full trimming, R8 | 25/25 |

These are API35 ARM64 checks. They cover immediate and suspended results,
primitive boxing, nulls, Unit, immediate/delayed native exceptions, Kotlin
cancellation, pre-cancelled calls, cross-thread cancellation, completion races,
native `finally` blocks, explicit dispatchers, parent Jobs, lazy/repeated Flow
collection, null items, strict backpressure, early exit, Flow failures,
cancellation while waiting, converter failure, StateFlow's initial value, and
canonical peer ownership.

NativeAOT retains the assembly-level Mono.Android IL3053 warning. No claim is
made that this configuration is warning-free or that every member of every
Android library has been exercised.

The same compiler and final helper were also used to generate and compile
adapters for four real AndroidX libraries:

| Library | Generated Task adapters | C# compilation |
|---|---:|---|
| DataStore Core JVM 1.2.1 | 19 | Passed |
| Lifecycle Runtime Android 2.11.0 | 10 | Passed |
| Paging Common JVM 3.3.6 | 7 | Passed |
| Room Runtime Android 2.8.5 | 14 | Passed |

This is compilation coverage for those 50 adapters, not runtime coverage of
all 50 methods. The checks reuse each library's original corpus inputs and
published managed references, adding suspend metadata reparsed from the exact
same native archive. They do not claim a completely regenerated Kotlin
ecosystem or new per-group assembly compatibility.

Evidence: `kotlin-async-real-libraries/results.json` and
`validate-kotlin-libraries.py`.

### Host checks and commands

Commands were run from the worktree root with the session-isolated NuGet cache.

```sh
./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj --no-restore -v minimal
```

Passed: **647**, failed: 0. Log: `kotlin-async-generator-tests.log`.

```sh
./dotnet-local.sh build external/Java.Interop/tools/class-parse/class-parse.csproj --no-restore -v quiet
```

Passed. Log: `kotlin-async-classparse-build.log`.

```sh
python3 -m unittest discover -s external/Java.Interop/tests/generator-Tests -p 'test_compare_binding_corpus.py' -q
```

Passed: **14**.

```sh
./dotnet-local.sh test external/Java.Interop/tests/Xamarin.Android.Tools.Bytecode-Tests/Xamarin.Android.Tools.Bytecode-Tests.csproj --no-restore -v minimal
```

The full parser suite reported **97 passed, 2 skipped, 1 failed**.
`ModuleInfoTests.ClassFile` expects 12 constant-pool entries, while the
environment's compiled module-info fixture has 13. The identical failure was
reproduced using the unchanged pre-coroutine parser from the completed corpus
snapshot; it is not caused by the new suspend facts. No unrelated fixture
expectation was changed.

```sh
./dotnet-local.sh test external/Java.Interop/tests/Xamarin.Android.Tools.Bytecode-Tests/Xamarin.Android.Tools.Bytecode-Tests.csproj --no-build --no-restore --filter 'FullyQualifiedName!~ModuleInfoTests.ClassFile' -v minimal
```

Passed: **97**, skipped: 2, failed: 0. Evidence:
`kotlin-async-bytecode-tests.log`, `kotlin-async-module-baseline.log`, and
`kotlin-async-bytecode-filtered.log`.

The repository README records all device reproduction commands. Actual final
commands are in the comparison JSON and in `kotlin-async-final-comparison.log`,
`kotlin-async-release.log`, and `kotlin-async-nativeaot.log`.

## One Maven group per assembly

**Use exact Maven groups as the default proposal, not an unconditional rule.**
Grouping `androidx.lifecycle` artifacts together is much more natural than one
assembly per Maven artifact, and reduces ownership churn when a class moves
between artifacts in the same group.

There is already a concrete counterexample to a strict rule in the graph we
tested:

```text
androidx.lifecycle:lifecycle-viewmodel-savedstate
    -> androidx.savedstate:savedstate
    -> androidx.lifecycle:lifecycle-common
```

These are not merely optional implementation dependencies. Public signatures
include a Lifecycle factory constructor accepting `SavedStateRegistryOwner`,
and `SavedStateRegistryOwner` inheriting `LifecycleOwner`. Combining all
Lifecycle artifacts first would therefore produce a circular assembly/project
dependency with SavedState.

The public/protected signature analysis of the existing 36-module AndroidX
closure finds **23 Maven groups and 22 acyclic components** after coalescing
the Lifecycle/SavedState cycle. This is graph evidence, not a compiled
22-assembly experiment.

Recommended ownership policy:

1. Start with one exact Maven group per candidate assembly.
2. Detect cycles in the projected managed contract graph. Merge the affected
   groups, or extract an intentionally stable shared contract assembly.
3. Split oversized groups into coherent families when necessary. All Google
   Play Services artifacts use `com.google.android.gms`; blindly applying the
   rule would create one very large assembly.
4. Persist the canonical JVM-type-to-managed-owner map. Do not silently
   recompute shipping assembly identity from each app's current dependency
   graph. Moving between groups still needs a compatibility strategy, such as
   type forwarders where applicable.

Grouping does not make two separately owned CLR types equivalent. Precompiled
consumers still need consistent ownership, assembly identity, and compatible
contracts. Native Maven version selection and managed assembly identity should
remain separate concerns.

Evidence: `maven-group-analysis.json` and `analyze-maven-groups.py`.

## Source size: useful improvements without deleting APIs

Measurements across all 362 comparable outputs from the completed corpus:

| Measurement | Legacy | Semantic |
|---|---:|---:|
| Generated C# bytes | 220,131,965 | 332,435,526 |
| Bytes on leading-comment lines | 45,334,979 | 66,798,972 |
| Bytes on pragma lines | 2,583,524 | 3,518,888 |

Comments account for about **20%** of semantic source text. That includes useful
documentation, so it is not all free savings. An optional compact mode could
move verbose diagnostic/XPath comments to a sidecar while keeping user-facing
documentation. Removing comments does not reduce compiled IL.

The `com.google.android.gms` group alone accounts for **130,234,052 source
bytes**, about 39% of the semantic corpus. This is another reason to allow
grouping exceptions. These source measurements are not app-size or controlled
build-performance benchmarks.

Priority order:

1. Keep the recovered public APIs; do not make the generator smaller by
   silently losing types again.
2. Share marshaling and async support. The new coroutine adapters already
   delegate to one shared runtime rather than emitting a state machine per
   bound method.
3. Measure redundant generated invoker/callback machinery before changing it.
   Preserve managed subclassing, JNI dispatch, trimming, and NativeAOT behavior.
4. Offer optional compact source and, later, demand-driven minimal projections
   alongside full-surface bindings.

Evidence: `binding-source-size-analysis.json`. No source-size optimization was
applied in this slice.

## Remaining production work

The first convenience slice is complete, but production integration still
needs a canonical shared runtime/package owner, richer Kotlin type projection,
automatic typed Flow facades, metadata-version policy, broader dispatcher and
library coverage, and throughput/allocation benchmarks. The correctness-first
Flow prototype creates per-item operations; that cost needs measurement and
likely optimization before high-rate streaming use.

Default-argument adapters, full value-class box/unbox conveniences, managed
implementations of arbitrary suspend APIs, and a complete regenerated
non-AndroidX support ecosystem remain outside this slice.

All changes remain local and uncommitted. No branch was pushed and no PR was
created.
