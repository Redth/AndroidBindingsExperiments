# Native API evolution and stable .NET binding ownership

## Conclusion

**Types moving between Maven artifacts is a real, repeated maintenance problem, but it is usually not the same problem as Java binary incompatibility. Stable, precompiled .NET foundation assemblies are a credible solution to much of this churn, not something the evidence rules out.**

The strongest historical .NET incidents involve making the CLR assembly owner follow the current Maven container. A Java class can keep its binary name while moving into a KMP Android/JVM variant or out of a KTX archive. JNI does not encode the archive name in its class/member lookup, but a precompiled .NET consumer does encode the assembly identity of a referenced CLR type.

My recommendation is therefore to keep a small set of explicit, stable CLR owners, independently resolve the final native graph, and verify the actual JNI contracts against that graph. Multiple assembly-local views remain useful for new API access and independent projections; the evidence does not justify making competing full CLR owners for the same Java classes the default.

This is a research conclusion, not certification that an old binding DLL will work against every future version of a library.

## Evidence and scope

I resolved **32 Android/JVM dependency snapshots**, indexed **292 distinct JAR/AAR contents by SHA-256**, and made **22 old/new or mixed-graph comparisons**. The sample covers Collection, Lifecycle, Activity, SavedState, Navigation, Compose ownership changes, Google Play services Location, and older Google Play services base/basement splits.

For each archive the research records class binary names, provenance, class/interface flags, superclass/interfaces, annotations, and declared field/method descriptors and flags. Selected examples were independently checked with JDK 21 `javap`, including the bytecode of compatibility-forwarding methods.

The accompanying evidence archive contains requested coordinates, selected versions, variant attributes, artifact hashes, comparison results, verification results, and analysis scripts. It does not redistribute the Maven binaries.

Important boundaries:

- This is a deliberately selected historical sample, not all versions of all 444 corpus roots and not a population-wide breakage-frequency estimate.
- Counts below refer to retained **JVM-public, non-synthetic class declarations** in the relevant namespace. They include nested and implementation types. They are not counts of audited, supported public Kotlin/AndroidX APIs.
- These are bytecode and dependency-resolution observations, not builds or on-device runs of precompiled old managed bindings against all the new graphs.
- Final resolution explicitly selects Android/JVM variants. An initial generic JVM pass selected some Compose desktop variants; that pass was preserved separately and excluded from the final results.
- A bare Gradle resolver is not the complete Android/Kotlin plugin pipeline. Four older snapshots resolve Kotlin 1.8.x alongside old 1.6.21 JDK-fragment artifacts and contain 20 duplicate Kotlin classes each. The indexes expose these conflicts rather than treating those graphs as deployable. The library-namespace relocation measurements are unaffected. Kotlin documents the plugin/metadata alignment needed to prevent this exact family of duplication.[1]

## 1. Whole Java types really do move between artifacts

| Versions compared | Measured retained JVM-public declarations with changed artifact owner | Concrete observation |
|---|---:|---|
| Collection 1.2.0 -> 1.3.0 | 21 | `androidx.collection.ArrayMap` moves from `collection` to `collection-jvm`; `ArrayMapKt` moves from `collection-ktx` to `collection-jvm`. |
| Lifecycle 2.5.1 -> 2.6.0 | 8 | `androidx.lifecycle.DispatchQueue` moves from `lifecycle-runtime-ktx` to `lifecycle-common`. This is an implementation-type example, not a recommendation to expose it as supported API. |
| Lifecycle 2.7.0 -> 2.8.0 | 68 | Retained Lifecycle/ViewModel classes move into JVM/Android variants and formerly separate KTX content is consolidated. |
| Activity 1.8.2 -> 1.9.0 | 15 | KTX facade classes move into `activity`; `activity-ktx` becomes empty. |
| SavedState 1.2.1 -> 1.3.0 | 11 | Retained classes move into `savedstate-android`; KTX extensions are consolidated into the base library. |
| Navigation 2.8.0 -> 2.9.0 | 105 | Retained classes move from the original common/runtime archives into Android variants. |
| Play services base graph 8.4.0 -> 9.0.0 | 47 | For example, `com.google.android.gms.common.api.Api` and `GoogleApiClient` move from `play-services-basement` to `play-services-base` without changing their binary names. Other APIs also evolve across this old release boundary; this is not a claim that the entire upgrade is binary compatible. |

These are not merely release-note interpretations: the class files were located in the old and new archives.

Official release notes corroborate several especially important waves:

- **Collection 1.3.0, October 4, 2023:** Kotlin/KMP migration and consolidation of `collection-ktx` into `collection`.[2]
- **Activity 1.9.0, April 17, 2024:** remaining APIs rewritten in Kotlin; all KTX extensions moved into `activity` and the KTX artifact emptied.[3]
- **Lifecycle 2.8.0, May 14, 2024:** KMP migration and consolidation of runtime/viewmodel KTX APIs. The release explicitly says Android binary compatibility was maintained.[4]
- **SavedState 1.3.0, May 7, 2025:** KMP migration, extension consolidation, and new Compose ownership.[5]

The churn is bursty, not constant for every type at every update. The measured Collection 1.3.0 -> 1.4.0, Lifecycle 2.6.0 -> 2.7.0, and Activity 1.9.0 -> 1.13.0 comparisons have **zero retained-class artifact-owner moves** in their respective measured namespaces. That does not mean zero API changes.

**Implication:** one Maven group per assembly would have absorbed most of these particular container changes, because the group stayed the same. However, the durable rule should be an explicit CLR ownership map, not a requirement to move a CLR type whenever its current Maven group or artifact changes.

## 2. Members can move while the old binary API remains callable

### Compose's Lifecycle owner moved across Maven groups

The old static getter is:

```text
androidx.compose.ui.platform.AndroidCompositionLocals_androidKt
    .getLocalLifecycleOwner()
    : androidx.compose.runtime.ProvidableCompositionLocal
```

In the newer API, its preferred owner is:

```text
androidx.lifecycle.compose.LocalLifecycleOwnerKt
    .getLocalLifecycleOwner()
    : androidx.compose.runtime.ProvidableCompositionLocal
```

That is a real move from the `androidx.compose.ui` Maven group to `androidx.lifecycle`, documented in Lifecycle 2.8.0.[4]

But the Android Compose UI 1.7.0 class file **still contains the old getter with the same JVM descriptor**. Its bytecode calls the new Lifecycle getter. An existing JNI call to the old slot therefore has a compatibility entry point; it does not need to discover the new source-level owner.

### SavedState's Compose owner does the same

SavedState 1.3.0 documents moving `LocalSavedStateRegistryOwner` from Compose UI to `savedstate-compose`.[5]

Comparing Compose UI 1.8.3 and 1.9.0 shows the old getter still present in `AndroidCompositionLocals_androidKt`. The 1.9.0 getter forwards to:

```text
androidx.savedstate.compose.LocalSavedStateRegistryOwnerKt
    .getLocalSavedStateRegistryOwner()
```

Again, the preferred source API moved across a Maven-group boundary, but the old binary slot was retained.

### Methods can become inherited rather than disappear

In the Play services base/basement 8.4.0 graph, `ConnectionResult` declares:

```text
describeContents()I
```

In the 9.0.0 graph it no longer declares that method. Instead, it inherits the same descriptor from `AbstractSafeParcelable`.

The old/new class declarations and ancestor method were verified with `javap` and the class-file index. A compatibility checker must distinguish this from a missing method. Java binary compatibility is defined in terms of binary linkage, not an identical source declaration layout.[6]

Navigation provides another useful, but different, hierarchy example. The 2.8.0 release introduces `CollectionNavType<T>`.[7] The actual `IntArrayType` implementation changes superclass from `NavType` to `CollectionNavType`, which itself extends `NavType`. This does not mean the existing `NavType.IntArrayType` field suddenly needs a different CLR assembly owner.

### Kotlin can hide a source API but preserve its binary bridge

Lifecycle's `ViewModel` changes from `Closeable`-based APIs toward `AutoCloseable` in the KMP migration. The old:

```text
ViewModel.addCloseable(Ljava/io/Closeable;)V
```

is still physically present in 2.8.0, even though its compatibility declaration is synthetic and no longer part of the ordinary new-source API presentation.

**Implication:** validating against only the newly projected Kotlin API would be too strict. We need an index of all required binary entry points, including retained synthetic compatibility methods. Conversely, blindly regenerating and republishing C# from only the preferred new source API can create a CLR break that the native library deliberately avoided.

## 3. There are genuine native ABI breaks

Play services Location 21.0.0 is a clear counterexample to assuming that newer is always compatible.

Google's **October 13, 2022** release notes explicitly announce that these four types changed from classes to interfaces:[8]

- `ActivityRecognitionClient`
- `FusedLocationProviderClient`
- `GeofencingClient`
- `SettingsClient`

The 20.0.0 and 21.0.0 class files confirm all four class/interface changes. They remain interfaces in the sampled 21.3.0 release.

This was not just a theoretical risk. A native Mapbox client compiled against the older API reported an `IncompatibleClassChangeError` when run with Location 21.0.0: it expected `FusedLocationProviderClient` to be a class but found an interface.[9]

Giving the same Java object two different C# projections does not repair already-compiled Java bytecode that invokes an incompatible API. A frozen binding profile should conservatively reject an incompatible type kind, invocation contract, or required hierarchy rather than promise that alternate views can make the graph safe.

AndroidX and Play services also should not be assigned the same blanket version policy:

- AndroidX documents strict semantic versioning for **binary** compatibility, while source compatibility across minor releases is encouraged rather than required. Alpha APIs may change or disappear.[10]
- Play services documents independently versioned libraries since 15.0.0 and explicit version-compatibility checking mechanisms. It warns that particular versions of different libraries may be incompatible.[11]

Version policy is useful evidence and a sensible default, not a substitute for checking the native ABI we actually use or testing behavior.

## 4. The historical .NET failures are especially instructive

### A CLR ownership break even though the Java name survived

[dotnet/android-libraries#807](https://github.com/dotnet/android-libraries/issues/807), opened October 27, 2023, reports a Lottie binding compiled against the older Collection assembly failing after Collection types moved into `Xamarin.AndroidX.Collection.Jvm`.

The maintainer's [resolution comment](https://github.com/dotnet/android-libraries/issues/807#issuecomment-2000342372) identifies Collection 1.4.0.2 as the fix and explains that reported type forwards were added manually, not automatically detected.

This is strong evidence for the distinction:

```text
Same Java binary class
    + different native archive
    + moved generated CLR owner
    = potentially broken precompiled managed consumer
```

Keeping the CLR owner stable avoids creating that extra break. If we intentionally move a CLR type, .NET type forwarding is the established mechanism, but it requires a compatible forwarding assembly and target type; it is not automatic equivalence between arbitrary duplicate types.[12]

### SavedState produced both ownership and native-container incidents

[dotnet/android-libraries#1177](https://github.com/dotnet/android-libraries/issues/1177), June 18, 2025, reports that the old SavedState managed package became empty after types moved into `SavedState.Android`, with `CS7069` involving `ISavedStateRegistryOwner`.

[dotnet/android-libraries#1179](https://github.com/dotnet/android-libraries/issues/1179), June 23, 2025, and [dotnet/android-libraries#1247](https://github.com/dotnet/android-libraries/issues/1247), August 13, 2025, report duplicate native `androidx.savedstate.ViewKt` definitions after dependency updates. The latter report specifically involves MAUI, CommunityToolkit, and a Credentials NuGet update.

These are separate failure modes:

| Failure | Root issue | Stable CLR ownership fixes it? |
|---|---|---|
| Missing CLR type / missing type forward | Generated type changed .NET assembly | Avoids the avoidable owner change |
| Duplicate Java class | Old and new native containers both packaged | No; native graph/container selection must be fixed |
| Native class/interface or method ABI break | Existing native callers no longer link | No; requires a compatible graph, updated callers, or a specific bridge |

The long-lived [dotnet/android-libraries#764](https://github.com/dotnet/android-libraries/issues/764) tracks the duplicate-container family, including Lifecycle and Kotlin moves. [dotnet/android-libraries#1317](https://github.com/dotnet/android-libraries/issues/1317), November 2025, shows related Futures/Tracing KTX issues in a MAUI/.NET 10 setting.

The archive analysis also confirms why mixed old/new containers can fail: the old Lifecycle runtime-KTX 2.5.1 archive and new Lifecycle common 2.6.0 archive share **13 class names**, while old SavedState-KTX 1.2.1 and new SavedState-Android 1.3.0 share **`androidx.savedstate.ViewKt`**. Those are deliberately mixed archive pairs, not a claim that the final corrected Gradle resolver selected those incompatible pairs together.

## 5. What happens when another library changes a MAUI-like graph?

I resolved a small, deliberately MAUI-like foundation:

```text
activity                    1.8.2
lifecycle-runtime-ktx       2.7.0
lifecycle-viewmodel-ktx     2.7.0
savedstate-ktx              1.2.1
```

Adding `navigation-runtime:2.9.0` changes the selected native graph as follows:

| Item | Foundation graph | With newer Navigation |
|---|---|---|
| Distinct selected archives | 30 | 36 |
| Activity | 1.8.2 | 1.8.2 |
| Lifecycle | 2.7.0 | 2.9.0, with JVM/Android variants |
| SavedState | 1.2.1 | 1.3.0, with Android variant |
| Duplicate native class names | 0 | 0 |

**93 retained JVM-public classes in the measured Activity/Lifecycle/SavedState namespaces have different artifact owners** after that resolution. That is substantial native packaging churn, but there are **zero class/interface-kind changes** in those retained measured classes.

This does not certify every old binding call. It does show the exact shape of the problem: many archive changes and version upgrades can happen while existing Java type names remain stable.

I also added `credentials:1.5.0` separately. That produces a 45-archive graph but **does not raise the selected Lifecycle 2.7.0 or SavedState 1.2.1 versions** in this baseline. A newer/additional feature library does not necessarily require newer foundational native APIs, and a NuGet package-version change should not automatically be interpreted as a native ABI change.

These are synthetic native experiments, not the complete dependency graph of a particular shipped MAUI release. For orientation, MAUI's [pinned `eng/AndroidX.targets` at commit `e10376bc`](https://github.com/dotnet/maui/blob/e10376bc66a1388f23b736065c9295bf0b77b4a4/eng/AndroidX.targets) already selects Activity 1.10.1.3 and Navigation/Lifecycle LiveData 2.9.2.1 managed packages. The historical issue reports above, rather than this synthetic graph, establish that real MAUI users encountered these classes of failure.

### If the binding DLL stays fixed

Suppose MAUI and a separately compiled library reference a stable foundation DLL that owns `ILifecycleOwner`.

If the final graph supplies the same Java `androidx.lifecycle.LifecycleOwner` binary contract from `lifecycle-common-jvm` rather than its old archive, both managed consumers still reference the **same CLR type in the same DLL**. There is no assembly-identity problem to solve and no new peer is required just because the archive changed.

The genuine decisions arise when the new library needs:

- **Only old APIs against newer native binaries:** retain the foundation DLL if its required contracts still hold.
- **A new outbound method:** a generated facade/extension/delta projection can call it while retaining canonical CLR parameter/return owners.
- **A genuinely new Java type:** add a projection with an explicit owner and references to existing canonical types.
- **A new required CLR base/interface relationship, virtual member, or callback contract:** an updated foundation surface or explicit adapter may be necessary. A precompiled CLR type cannot acquire new members or inheritance relationships merely because its Java peer changed.
- **An incompatible native ABI:** reject or choose an appropriate compatibility profile. Duplicate managed views do not make incompatible Java libraries coexist.

## 6. Do not freeze everything that happens to be public in bytecode

One illustrative finding is `androidx.lifecycle.CloseableCoroutineScope`: it appears as a JVM-public class in the old graph and disappears in the new one. But the published Lifecycle 2.7.0 source archive declares it as **`internal class CloseableCoroutineScope`** in `androidx/lifecycle/ViewModel.kt`, line 47.[13]

Calling every such disappearance an AndroidX public-API compatibility violation would be incorrect. Kotlin visibility, `RestrictTo`, experimental annotations, compiler-generated bridges, and internal implementation classes all matter. AndroidX explicitly documents `RestrictTo` as a usage restriction enforced by lint, not by JVM access flags.[14]

For the new compiler this suggests two distinct products:

1. A deliberate **supported public foundation projection**, informed by Kotlin metadata and AndroidX API restrictions.
2. Explicit internal/raw projections or helper dependencies when an adapter genuinely needs them, with the exact native requirement recorded.

Kotlin convenience methods have additional ABI dependencies: `$default` adapters, value-class helpers, suspend descriptors, and compatibility `DefaultImpls` can matter even if those helpers should not become general public C# API. Kotlin documents compiler modes that do and do not emit compatibility bridges.[15]

This is another reason to avoid treating identical Kotlin source metadata as the definition of native binary compatibility.

## 7. Recommended .NET 12 direction

### Stable CLR identity first

Use a small set of logical foundation owners, initially grouped by Maven family and merged where CLR dependency cycles require it. Keep an explicit Java-type -> CLR-type/assembly map.

Do not change an existing type's CLR owner, name, or managed shape just because the selected native artifact changed. Preserve old compatible managed members when publishing an updated foundation, even when newer Kotlin metadata hides their native compatibility entry points.

One Maven group per assembly is a reasonable starting organization, **not an immutable identity rule**. Cross-group API moves already exist; the old owner can remain valid when the native library supplies a compatibility bridge.

### Validate the final native contract, not the original container

A precompiled binding should express "this Java type/member contract must exist somewhere in the final native graph," rather than "this class must still live in its original AAR."

The contract check should cover class/interface kind, required methods/fields and exact descriptors, static/instance invocation, constructors, access, relevant inheritance/assignability, callback/subclass obligations, and required Kotlin helpers. It must resolve inherited members and retained compatibility methods rather than comparing source declaration lists.

Keep binary-linkage requirements separate from higher-level projection expectations such as nullability, collection shape, and coroutine semantics. Those expectations still matter, but a Kotlin-source change need not be a JNI ABI break.

The current pilot validator in `BindingViewProjection.cs` checks selected member/owner/staticness and available Kotlin return shape. It is not yet a comprehensive hierarchy/type-kind/callback/field ABI checker. This research identifies concrete inputs for extending it; it does not claim that extension is already implemented.

### Keep native graph correctness independent

Reject duplicate Java classes, preserve relevant native dependency/version constraints, and let the existing Gradle integration select the graph. Normal R8/JNI retention or remapping must preserve the required callable entries.

Do not solve container duplication by creating more CLR wrappers. Do not force all Play services libraries to share one numerical version: they have been independently versioned since 15.0.0.[11]

### Let additions be cheap without promising impossible CLR mutation

Generate small outbound facades or local views for new methods, reuse canonical peer types, and allocate owners for genuinely new types. Refresh foundation assemblies when public managed inheritance, callbacks, virtual contracts, or desired primary API surface actually require it.

Keep multiple views as an intentional interoperability tool, especially where a stale CLR hierarchy cannot express native assignability. Do not make multiple competing peer owners the ordinary response to archive movement.

### Test a fixed DLL across native upgrades

The highest-value follow-up experiment is to **compile a foundation and two consuming managed libraries once, freeze those DLLs, then vary only the final native graph**. Include a packaging-only KMP/KTX move, retained Compose getter, inherited method, and an intentionally rejected Location class/interface transition. Exercise callbacks/subclassing, typed Flow, trimming/R8, and NativeAOT where relevant.

That directly tests the intended product promise. Regenerating C# against each new native graph and observing that it compiles would be a different, weaker test.

## Bottom line

The historical evidence strengthens the case for fewer stable binding assemblies. It weakens the idea that Maven archive movement itself requires duplicate managed definitions of the same Java classes.

The unavoidable work is maintaining **supported API profiles and their native ABI requirements**. We can largely stop turning upstream packaging reorganizations into CLR type-identity migrations.

## Sources

1. [Kotlin: alignment of transitive standard-library dependencies](https://kotlinlang.org/docs/gradle-configure-project.html#versions-alignment-of-transitive-dependencies).
2. [AndroidX Collection 1.3.0 release notes](https://developer.android.com/jetpack/androidx/releases/collection#1.3.0).
3. [AndroidX Activity 1.9.0 release notes](https://developer.android.com/jetpack/androidx/releases/activity#1.9.0).
4. [AndroidX Lifecycle 2.8.0 release notes](https://developer.android.com/jetpack/androidx/releases/lifecycle#2.8.0).
5. [AndroidX SavedState 1.3.0 release notes](https://developer.android.com/jetpack/androidx/releases/savedstate#1.3.0).
6. [Java Language Specification, binary compatibility](https://docs.oracle.com/javase/specs/jls/se21/html/jls-13.html).
7. [AndroidX Navigation 2.8.0 release notes](https://developer.android.com/jetpack/androidx/releases/navigation#2.8.0).
8. [Google Play services, October 13, 2022](https://developers.google.com/android/guides/releases#october_13_2022).
9. [mapbox/mapbox-events-android#577](https://github.com/mapbox/mapbox-events-android/issues/577).
10. [AndroidX versions and compatibility policy](https://developer.android.com/jetpack/androidx/versions).
11. [Google Play services library versioning](https://developers.google.com/android/guides/versioning).
12. [Microsoft Learn: CLR type forwarding](https://learn.microsoft.com/en-us/dotnet/standard/assembly/type-forwarding).
13. [Lifecycle ViewModel-KTX 2.7.0 published sources](https://dl.google.com/dl/android/maven2/androidx/lifecycle/lifecycle-viewmodel-ktx/2.7.0/lifecycle-viewmodel-ktx-2.7.0-sources.jar).
14. [AndroidX RestrictTo](https://developer.android.com/reference/androidx/annotation/RestrictTo).
15. [Kotlin JVM interface-default compatibility modes](https://kotlinlang.org/docs/interfaces.html#jvm-default-method-generation-for-interface-functions).

Additional primary incident reports are linked inline. The machine-readable evidence preserves selected coordinates, artifact hashes, and exact class/member observations.
