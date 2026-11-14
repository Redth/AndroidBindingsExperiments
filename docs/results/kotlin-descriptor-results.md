# Official Kotlin frontend, supported APIs, and real generics

The experimental binding compiler now uses a build-time Kotlin/ASM descriptor
frontend. Official Kotlin metadata supplies source visibility and type contracts;
ASM supplies physical class/member facts and the actual invocation targets of
synthetic bridges. C# still owns stable CLR identity, naming, projection, and JNI
marshaling. This is a hybrid frontend, not a wholesale replacement of class-parse
or the existing C# emitter.

## What changed

- Supported-API policy considers enclosing/source visibility, `RestrictTo`,
  `VisibleForTesting`, and `ApiStatus.Internal`. Required CLR contracts remain
  hidden infrastructure. Excluded native nominals survive validation as carrier
  symbols, then are erased from managed signatures and omitted from peer emission.
  This avoids pruning legitimate public operations merely because their native
  signatures mention implementation types.
- Typed `XView<T>` wrappers preserve class/method generics, inherited substitutions,
  scoped variable shadowing, public fields, generic Task/Flow results, boxed
  reference arrays, and nested read-only collection results. They retain the
  canonical peer and never register a second JNI owner.
- Actual bytecode bridge targets prevent unrelated same-name/same-arity
  overloads from being merged. JVM descriptors remain distinct from CLR signatures.
- Multifile Kotlin facades inherit verified source contracts from their parts.
  Missing/unsupported parts are unverified, not guessed.
- Descriptor output is deterministic and atomic. Nonfinite numeric constants and
  unpaired UTF-16 strings have lossless JSON tags instead of invalid JSON/UTF-8.
  ASM, Gson, and metadata tooling remain isolated from app and runtime packages.

## Inputs and corpus

The pinned `dotnet/android-libraries/config.json` is commit
`2393441675a6d82bb817ab7aa2df698c7ec929ce`: 444 records, including dependency-only
and no-binding entries. No library-specific metadata transforms were added.

| Result | Records |
|---|---:|
| `compiled` | 280 |
| `compiled-api-changes-review-required` | 38 |
| `compiled-supported-api-profile` | 44 |
| `input-failed` | 1 |
| `metadata-only` | 1 |
| `no-jvm-classes` | 80 |

**362 independent binding assemblies compiled without C# errors.**
Their managed dependency references are matching published NuGet bindings,
not fully regenerated dependency closures for all roots.

The original EmojiPicker NuGet dependency graph remains contradictory:
Emoji2 `[1.2.0.2,1.2.1)` cannot satisfy the AppCompat/ViewsHelper requirements
for 1.6.0.3. We did not relax those ranges. Instead, the same native
`androidx.emoji2:emoji2-emojipicker:1.7.0` was regenerated in a previously
resolved, hash-verified **49-archive Maven closure**, including Kotlin, coroutines,
and Guava: **6,697 native classes, 3688 generated C# files,
zero C# errors**, and no published managed support bindings.
The output is `BindingEmoji.Foundation.dll` (17,161,216 bytes).
This is compilation evidence, not an EmojiPicker on-device behavior claim.

## API-quality review

The compiled-metadata/native-slot audit covered **362 DLLs**.
It found **zero restricted public leaks** and **zero unexplained registration
losses** after reviewing 124 inventory differences.
Evidence distinguishes source-policy exclusions, legacy JNI signatures that
never existed physically, typed operations covering synthetic bridges, and
operations retained through compiled CLR ancestry. Selected Media2/WatchFace
dependency archive hashes are recorded separately.

Inventory comparison is not a proof of behavioral equivalence or preservation
of every managed spelling. The evidence retains the original strict comparison
statuses; reviewed API changes are not relabeled as unchanged legacy API.

## Actual Android integration

`tests/BindingProjection/Reference.slnx` regenerates its locked native graph,
including Kotlin support, with no published AndroidX/Kotlin managed bindings.
Foundation owns the canonical peers; Reader/Writer use independent minimal views.

Debug, Release/full trim/R8, and NativeAOT/R8 each passed **10 workflow checks
and 19 generic checks twice**, including Activity recreation, cancellation
cleanup, cross-library callbacks, real DataStore commits, and persistence across
process restarts. Generic checks cover nullable/boxed values, arrays, bounds,
type failures, inherited substitutions, typed fields, managed callbacks,
generic suspend/Flow results, canonical identity, and disposal.

The actual compiled Foundation DLL preserves generic metadata in fields,
`Task<T>`, and `IAsyncEnumerable<T>`, with 1043 generic
facades and zero facade JNI registrations.

Unchanged and C#-only builds preserved all 2961 tracked
generated source/metadata outputs and timestamps after warming the selected
configuration. Final APK DEX and package checks found no ASM/Gson/metadata tool
payload or descriptor JSON. The canonical runtime NuGet has native source,
no JAR/AAR payload, and no managed NuGet dependency.

## Example from the real test application

```csharp
var box = GenericProbe_BoxView<int>.Create (17);
using var peer = box.Peer;
box.PublicValue = 41;
int value = box.Get ();

var suspender = GenericSuspenderView<string>.Create ("stream");
using var suspenderPeer = suspender.Peer;
string? result = await suspender.EchoAsync ("suspend");
var flow = suspender.Flow () ?? throw new InvalidOperationException ();
await foreach (var item in flow.AsAsyncEnumerable ()) {
    Console.WriteLine (item);
}
```

These are authored fixture APIs, not invented AndroidX APIs. See
`tests/BindingProjection/App/GenericProjectionChecks.cs` for the actual checks.

## Validation commands and outcomes

- `make all CONFIGURATION=Debug`: passed.
- `./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj -v:minimal -nr:false`: 731 passed.
- `bash build-tools/gradle/gradlew --no-daemon --console plain -p external/Java.Interop/tools/jvm-api-descriptor test installDist`: 7 JVM tests passed.
- `./dotnet-local.sh test external/Java.Interop/tests/Java.Interop.Tools.JavaTypeSystem-Tests/Java.Interop.Tools.JavaTypeSystem-Tests.csproj -v:q -nr:false`: 18 passed.
- `./dotnet-local.sh test src/Xamarin.Android.Build.Tasks/Tests/Xamarin.Android.Build.Tests/Xamarin.Android.Build.Tests.csproj -v:q -nr:false --filter 'FullyQualifiedName~ExperimentalBindingsTests'`: 6 passed.
- `./dotnet-local.sh test external/Java.Interop/tests/Xamarin.Android.Tools.Bytecode-Tests/Xamarin.Android.Tools.Bytecode-Tests.csproj -v:q -nr:false --filter 'FullyQualifiedName!~ModuleInfoTests.ClassFile'`: 110 passed, 2 skipped. The known unrelated ModuleInfo fixture mismatch was explicitly excluded.
- `python3 -m unittest discover -s external/Java.Interop/tests/generator-Tests -p 'test_compare_binding_corpus.py' -v`: 17 passed.
- Ordinary `./dotnet-local.sh build tests/BindingProjection/App/Reference.App.csproj -c Debug -v:q -nr:false`, then Release, then Release with `-p:PublishAot=true`: passed. Each APK was exercised with `tests/BindingProjection/verify-reference.py --device-serial emulator-5594` using its corresponding configuration.
- Full corpus invocation and per-root commands/hashes are in the immutable summary linked in `kotlin-descriptor-final-evidence.json`. The strict harness can return nonzero for the original invalid NuGet graph and API-review statuses; this is not represented as a clean unchanged-API comparison.

Generated code still produces warnings; these are not warning-free builds.
The owned read-only emulator was stopped after device verification.

## Remaining boundaries

Everything remains experimental and opt-in. The legacy default path is unchanged.
Unannotated Java public declarations cannot automatically be known to be
unsupported vendor implementation APIs. Erased native bounds are runtime
assignability checks, not complete static CLR F-bound enforcement. Mutable
nominal existential types and other unsupported shapes retain raw peers with
diagnostics. Unconstrained C# `T?` does not turn a nonnullable value argument into
`Nullable<T>`; choose `int?` when native null is legal. Read-only collection views
are live views, not immutable snapshots. No exhaustive behavior, API-version,
device-vendor, or Maven-version matrix is claimed.
