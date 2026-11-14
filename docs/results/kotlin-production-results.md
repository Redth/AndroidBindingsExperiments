# Kotlin binding compiler: integrated follow-up

## Result

All of the requested follow-up workstreams now have working, opt-in
implementations: a canonical optional runtime package, richer Kotlin type
contracts, typed Flow facades, metadata policy, measured/optimized streaming,
defaults, value-class boxing, managed suspend implementations, and regenerated
support dependencies.

This is an integrated experimental implementation, not a declaration that all
Kotlin language features or every Maven version combination are production-ready.
The existing generator remains the default. No commits, pushes, or pull requests
were created.

## What changed

`Microsoft.Android.Kotlin`, owned by dotnet/android, is the one optional managed
runtime/package. Its public boundary uses opaque JNI peers rather than a
particular published Kotlin CLR assembly. The package contains its managed
assembly and authored Kotlin/Gradle source, with **no JAR/AAR payload and no
managed Kotlin package dependencies**.

The native source is included once in the app's existing Gradle graph. It
participates in that graph's version selection and writes build outputs outside
the restored package directory. The package smoke test selected the consumer's
stdlib 2.4.0, left its source directory unchanged, and verified an incremental
native build.

The compiler/runtime now provide:

- Structured classifiers, nested arguments, variance/stars, type parameters,
  nullability, and metadata type-table handling.
- Typed peer and nested List/Set/Map suspend results, plus typed Flow method and
  property facades. Collection facades are live read-only views. Map entries
  preserve null keys instead of pretending to satisfy a non-null-key dictionary
  contract.
- `KotlinOptional<T>` and verified native default helpers, including explicit
  null/zero, nontrailing required parameters, suspend/interface/static/extension
  functions, two mask words, and public concrete constructor factories.
- Verified value-class Box/Unbox versus constructor-validating Create helpers,
  including primitive/reference/nullable/generic representations and unsigned
  Kotlin/JVM sign-bit conversions.
- `FromTask` for implementations of arbitrary raw suspend contracts. Native
  cancellation reaches managed code and waits for its Task/finally cleanup;
  original managed exceptions survive the round trip.
- Metadata policy recognizing 1.1-1.9 and 2.0-2.4, with explicit diagnostics and
  raw-binding fallback for unsupported versions. Direct metadata entry points
  cannot bypass the policy.

Parser repairs include interface functions without constructor metadata,
predefined string-table index zero, substring/class-ID handling, nullable
primitive descriptors, boxed generic arrays, and preservation of static/
synthetic flags. Support regeneration also repaired scoped inner generics,
covariant invokers, native bridge aliases, actual abstract/default contracts,
unsigned value wrappers, and duplicate string conveniences.

Enabling conveniences throughout regenerated Kotlin stdlib exposed one final
bug: CharSequence default facades called an unadjusted helper name. The compiler
now uses the emitted `Formatted` shim name, with a regression test.

## Android verification

API35 ARM64 emulator, with actual native execution:

| Binding ownership | Debug | Full trim + R8 | NativeAOT + full trim + R8 |
|---|---:|---:|---:|
| Published Kotlin support owners + canonical runtime | 56/56 | 56/56 | 56/56 |
| Fully regenerated Kotlin/AndroidX support owners + same runtime | 56/56 | 56/56 | 56/56 |
| Regenerated AndroidX/EmojiPicker ecosystem | 6/6 | 6/6 | 6/6 |
| Regenerated Okio/OkHttp/RxJava/Gson/Protobuf ecosystem | 6/6 | 6/6 | 6/6 |

The 56-check contract includes Android Main/immediate dispatch from managed
workers, Flow collection on Main, cancellation races, parent Jobs, exceptions,
cleanup, null items, cold/hot flows, strict demand, peer ownership, managed
implementations, defaults, boxing, nested collections, and the benchmark.

The standalone source-only NuGet consumer passed **3/3** checks with no managed
Kotlin binding dependencies. Real compiler fixtures for Kotlin **2.0.21** and
**2.1.20** each passed all 56 contracts; the main fixture/compiler is **2.4.21**.
Recognizing older metadata is not the same as testing every older compiler.

NativeAOT retains the existing assembly-level Mono.Android IL3053 warning.
These configurations are not described as warning-free.

## Streaming measurements

One native subscription and a reusable ValueTask source replace the old
per-item native operation/Task/FlowItem protocol. Demand acknowledgement prevents
prefetch while C# is processing its current item.

Each measurement used 10,000 items, warmups, three alternating trials per
protocol, exact counts/checksums, and process-wide managed allocation counters.

| Published-owner configuration | Baseline median bytes | New median bytes | Reduction |
|---|---:|---:|---:|
| Debug | 41,047,064 | 2,077,448 | 94.94% |
| Full trim + R8 | 37,684,408 | 2,076,984 | 94.49% |
| NativeAOT | 48,919,368 | 2,145,904 | 95.61% |

The regenerated-owner matrix independently showed 94.45-95.61% reductions.
The test enforces at least a 20% median allocation improvement. The published-
owner Debug median throughput was approximately 11,900 versus 73,265 items/sec.
These are emulator measurements, not controlled shipping-device guarantees;
Java/native heap allocation is not measured.

## Corpus and support ecosystem

The fresh-parser rerun covered all **444** pinned AndroidX/GMS records at
android-libraries commit `2393441675a6d82bb817ab7aa2df698c7ec929ce`:

| Classification | Records |
|---|---:|
| Independently compiled without inventory-review flags | 319 |
| Independently compiled with conservative inventory-review flags | 43 |
| No JVM classes | 80 |
| Metadata-only/no exported C# surface | 1 |
| Incompatible original NuGet dependency graph | 1 |

All **362** prepared binding outputs compile with **zero semantic C# errors**.
The original EmojiPicker NuGet graph remains explicitly failed; it was not
silently repaired or counted as a successful independent restore. The same
native library works in its separately resolved, regenerated closure.

No registered native types or fields are lost relative to the legacy
inventories. Five newly flagged method declarations were inspected: Media3
DataSource.read, two GMS sequenced-collection reversed methods, and two inherited
parcel writers. Their exact JNI calls remain in inherited-interface adapters,
explicit implementations, or class invokers. Earlier inventory review evidence
remains preserved, not replaced with a blanket equivalence assertion.

Complete support regeneration now compiles:

- The 49-archive AndroidX/Kotlin/coroutines/Guava graph, with **zero published
  support DLLs**: 2,795 registered types and 19,069 method registrations.
- A separate 12-archive graph including Okio 3.9.1, OkHttp 4.12.0, RxJava
  2.2.21/3.1.10, Reactive Streams, Gson 2.11.0, protobuf-javalite 3.25.5,
  Kotlin stdlib, and annotations, also with **zero published support DLLs**.
- The integrated 51-archive graph with conveniences enabled throughout
  regenerated stdlib/coroutines/AndroidX plus the fixture. The only managed
  support owner is our canonical runtime: **zero C# errors**, 2,833 registered
  types, and 19,549 method registrations.

The current compiler also compiled all **50** Task adapters across DataStore,
Lifecycle, Paging, and Room. This is compilation coverage for those methods,
not a claim that every one was exercised with a real library operation.

## Reproducible checks and evidence

Commands ran from the worktree root with the isolated NuGet cache and JDK 21.
The device comparison JSON records exact build/install/launch commands and
native/managed hashes.

| Command | Result |
|---|---|
| `./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj -c Debug -v minimal` | 672 passed |
| `./dotnet-local.sh test external/Java.Interop/tests/Xamarin.Android.Tools.Bytecode-Tests/Xamarin.Android.Tools.Bytecode-Tests.csproj -c Debug -v minimal --filter 'FullyQualifiedName!~ModuleInfoTests.ClassFile'` | 110 passed, 2 skipped |
| `./dotnet-local.sh test external/Java.Interop/tests/Xamarin.SourceWriter-Tests/Xamarin.SourceWriter-Tests.csproj -c Debug -v minimal` | 11 passed |
| `python3 -m unittest discover -s external/Java.Interop/tests/generator-Tests -p 'test_compare_binding_corpus.py' -q` | 14 passed |
| `./dotnet-local.sh pack src/Microsoft.Android.Kotlin/Microsoft.Android.Kotlin.csproj --no-restore -p:Version=0.1.0-spike.1 -o <session-files>/kotlin-final-packages -v minimal` | Passed; source-only package verified |
| `git diff --check` | Passed |

The excluded module-info constant-pool expectation is an independently
reproduced pre-existing fixture failure. The intentional Kotlin constructor XML
snapshot was updated; the parser tests otherwise pass. No unrelated fixture
expectation was changed.

The new optional package ships no third-party native binary or managed Kotlin
dependency, so this change adds no such payload to THIRD-PARTY-NOTICES. This is
not a claim that every pre-existing repository notice is complete.

Evidence paths are relative to this session's `files` directory:

- `kotlin-production-final-evidence.json`: machine-readable index, exact
  benchmark trials, runtime matrices, package hash/content list, and library
  adapter commands.
- `kotlin-final-corpus/runs/a7abbeb8f1dd88966273/summary.json`: fresh 444-record
  rerun, including the still-visible failure/review classifications.
- `kotlin-complete-async-closures/closure-dd9c3fa21074/`: integrated all-regenerated
  convenience compilation and 56-check runtime matrix.
- `kotlin-final-closures/closure-409b238c02ce/`: fully regenerated 49-archive graph.
- `kotlin-broad-support-closures/closure-a40096047f63/`: additional support graph.
- `kotlin-package-consumers/package-17f5134eb80d/`: independent package/source-
  graph consumer.
- `kotlin-final-packages/Microsoft.Android.Kotlin.0.1.0-spike.1.nupkg`: local
  experimental package; not published.

## Boundaries

Arbitrary generic facade forms still erase where a correct richer projection is
not implemented. Collection views are read-only, not immutable. Protected/
abstract constructor conveniences remain raw; constructor factories cover public
concrete classes. Managed reverse implementations use `FromTask` in a raw
override rather than an autogenerated implementation class. Cancellation is
cooperative and can wait indefinitely if native or managed code ignores it.

The source-size and Maven-group ownership recommendations from the previous
report still stand: do not remove useful raw API surface merely to shrink C#,
and use Maven groups as a default ownership proposal rather than an unconditional
assembly boundary. Lifecycle/SavedState cycles and very large GMS groups still
require an explicit canonical ownership plan.
