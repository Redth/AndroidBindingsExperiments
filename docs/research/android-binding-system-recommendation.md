# Recommendation: a contract-driven Android binding system

28 October 2026. Proposed system design based on the binding research and shared-peer experiment.

## Executive decision

Build a new **contract-driven binding compiler** on top of Java.Interop. Its normal output should be small, assembly-local C# views, with canonical managed bindings reserved for shared public infrastructure and Java-to-managed ownership.

Use the same compiler to produce full foundational bindings. Make Kotlin metadata and compiler-assisted adapters first-class inputs, rather than trying to repair all Kotlin behavior after reducing it to Java-shaped XML.

The final application should combine and validate the **Java requirements** of its libraries. It should not normally merge their CLR types or regenerate another library's managed API.

This is the recommended primary architecture:

```text
Existing resolved Gradle/classpath graph
                |
       Immutable JVM/Kotlin index
                |
 Explicit contracts + roots + versioned profiles
                |
        Shared projection planner
           /                \
  C# views/contracts    Kotlin adapter recipes
           \                /
    Per-library requirement manifests
                |
 Final-app validation and adapter compilation
                |
 Existing Java.Interop / callback / R8 / runtime pipeline
```

**Compatibility assumption, not an approved requirement:** The system is opt-in and may introduce new managed APIs. Existing bindings remain usable. This is not a promise that two incompatible canonical CLR representations of the same Java hierarchy can be mixed transparently.

**Scope:** Design recommendation only. No production implementation has been made for this recommendation.

## 1. Decisions I recommend

| Area | Recommended default |
|---|---|
| Migration | Opt-in adoption, one library/API family at a time; no forced rewrite of existing apps |
| Ordinary library access | Internal, non-owning, statically generated views |
| Foundational APIs | Stable, managed-only canonical contract packs with full supported inheritance/callback surfaces |
| Assembly ownership | Local views stay local; shared public contracts have explicit owners |
| Final-app role | Validate transitive JVM requirements and produce required Java/Kotlin adapters |
| A universal late-generated CLR assembly | Not the primary design and not a first-release prerequisite |
| Generation inputs | Explicit C# contracts or type/package roots; not arbitrary unresolved usage |
| Kotlin extraction | Prefer the official metadata reader in a batched JVM indexing helper; retain the managed classfile reader |
| Kotlin lowering | Compile ordinary generated Kotlin against binary dependencies when a bridge is needed |
| Source generators | Thin incremental C# emitters and IDE integration; no JAR scanning or Kotlin compilation inside Roslyn |
| Unsupported rich projection | Explicit diagnostic; exact access is a separate, visible fallback |
| AI | Engineering-time help producing reviewed rules and tests; never required for normal builds |

These are recommendations made to produce a coherent design. The compatibility constraint and the amount of foundational API redesign remain particularly consequential choices for review.

## 2. Three managed representations, with different jobs

### A. Canonical peers: own identity and callbacks

Retain the existing Java.Interop/Android peer machinery. An object crossing between Java and managed code must have one authoritative owning managed peer, with existing lifetime and registry reconciliation.

Use canonical contracts where CLR inheritance or widely shared public types matter: `Activity`, `ComponentActivity`, Fragment, lifecycle contracts, and similar infrastructure.

For the first adoption path, reuse existing canonical platform/AndroidX peers where they are referenced, rather than introducing competing owners simply to use the new projection engine. The new compiler should progressively become the producer of foundational contracts, with an explicit compatibility profile for established APIs.

A redesigned typed facade can sit above an existing erased canonical owner. It does not need to register another peer.

### B. Local views: describe what a library wants to do

A view is a normal C# facade that holds a strong reference to an authoritative peer through a shared carrier. It:

- Is not another `IJavaPeerable` owner and does not register itself in the peer registry.
- Can expose only the methods the containing library needs.
- Can coexist with another assembly's different projection of the same Java object.
- Uses the correct JVM member and dispatch mode regardless of the facade's C# name.
- Does not imply CLR type equivalence, shared CLR inheritance, or reference equality between views.

Make generated views **internal by default**. A reusable library should normally expose its own domain API, a canonical foundation type, or the shared carrier at an explicitly Java-facing boundary.

This is the recommended default for the large, fast-changing Maven ecosystem.

Preserve dispatch intent per operation: ordinary view calls use Java virtual dispatch, while a managed override's base call must retain the appropriate nonvirtual base behavior. The experiment exercised that distinction; there must not be one global invocation policy for both.

### C. Semantic adapters: own an operation or subscription

An async operation, subscription, collection adapter, or resource wrapper has behavior beyond naming JVM methods. Model that behavior explicitly.

For example, a coroutine operation owns cancellation registration and a Kotlin job; a subscription owns detachment. These may be disposable. An ordinary object view should not acquire such ownership just because it has methods.

### A safer carrier than the experiment's borrowed Peer property

Introduce one small common managed carrier, provisionally named `JavaObject`, in the shared interop runtime. This is proposed API, not an existing type definition.

It should retain the canonical peer without normally exposing a raw pointer or a disposable borrowed peer. It must not create a second independently owning JNI reference graph.

Recommended lifetime policy:

- Ordinary carriers/views are GC-managed and do not implement `IDisposable` merely to release their managed root.
- Acquiring another view does not transfer object ownership.
- Explicit Java resource operations such as `close`, and operation/subscription disposal, remain distinct.
- If an existing owner is explicitly disposed elsewhere, views become invalid and calls fail appropriately.
- Do not claim concurrent external-owner-disposal safety from the experiment. Reuse verified reference-safe invocation mechanisms and establish the supported race contract before shipping.

The experiment's disposable views proved that view disposal can release only a managed root. The recommendation intentionally avoids making routine view disposal part of the default consumer experience.

Provide statically generated factories such as `WidgetView.From (javaObject)`. They perform Java assignability checks. No reflective activator or runtime-generated wrapper type should be needed.

Use an explicit Java identity operation when needed; neither numeric JNI handle equality nor `ReferenceEquals` between views is the identity contract.

## 3. Two authoring experiences, one engine

The API names and configuration below are illustrative.

### Explicit contracts for focused use

```csharp
[JavaView ("com.example.widgets.Widget")]
internal partial class WidgetView
{
    [JavaMember ("getValue")]
    public partial int GetValue ();

    [JavaMember ("setValue")]
    public partial void SetValue (int value);
}
```

The developer chooses the managed surface; the engine supplies invocation and conversion code. Normal cases should not require descriptors or XML edits.

Match declarations against the semantic index. An ambiguous overload, unsafe conversion, or unsupported callback must produce a specific diagnostic, not a heuristic choice. Permit an exact JVM signature selector for genuinely ambiguous cases.

This remains ordinary C# with marker attributes and partial declarations, not a separate user-facing binding language.

### Root selection for larger surfaces

```xml
<AndroidProjection Include="com.example.widgets.**"
                   Surface="Full"
                   Representation="LocalView" />
```

This requests the full supported surface of the selected Java family, still as views. It does not recursively generate every type in every dependency.

The planner computes separate closures for:

- Invocation and marshalling.
- Types deliberately exposed in managed public contracts.
- Interface implementation.
- Constructors, protected access, abstract obligations, and subclass callbacks.

Selecting a callback/subclass capability expands the necessary contract deliberately. Selecting three outbound methods does not require the same expansion.

An unselected referenced object type can remain a checked opaque carrier unless the chosen managed contract requires a named projection. Such a fallback must be visible; it is not equivalent to a fully typed return.

### IDE assistance without build-time inference magic

Provide an API catalog/member picker and code actions that create or extend the persisted contract. Offer unused-projection analysis later.

Do not make the first release discover its entire surface solely from unresolved C# expressions. Roslyn generators do not provide a general iterative rebinding loop, and cannot rely on another generator's output being available as input.

## 4. Foundational bindings are a product, not an exception

Maintain a limited set of canonical, managed-only foundation packs. Organize their public contracts by stable managed ownership, not by whichever Maven artifact currently contains a Java class.

Initially preserve existing assembly identities where compatibility requires them. Do not combine previously distinct assemblies merely because a new packaging layout looks cleaner.

A foundation profile must specify:

- The supported public/protected surface and hierarchy.
- Canonical CLR names and assembly owners.
- Generic, nullability, collection, and async conventions.
- Callback, constructor, and protected-member behavior.
- The Java shape it requires, independent of artifact provenance.
- An API compatibility baseline.

The full-binding backend must fail when it cannot implement an essential abstract/interface obligation. It must not quietly remove a problematic interface until compilation succeeds.

### Be honest about release cadence

A precompiled managed class still needs an updated managed assembly to add instance members or virtual slots. This design does not evade CLR rules.

The practical blend is a stable canonical foundation plus locally generated views/helpers for newer outbound APIs. A new helper cannot add a virtual slot or become a partial class declaration in another assembly.

For newer canonical contracts, publish an updated foundation pack using the improved compiler. That concentrates intentional release and compatibility work in a small set of important APIs, rather than every Maven library.

### Why not put every generated type in one final assembly?

A deliberately late-linked contract assembly remains a possible later mode. It is not the recommended dependency for making this system useful.

It requires compatible reference contracts across separately compiled libraries, precise identity/signing/version rules, a deliberate union process, and callback/inheritance compatibility. Giving different DLLs the same assembly name does not implement that process.

Local views already address the common cross-library object-exchange problem without solving universal CLR identity. Keep the late-assembly option behind a separate proof and a demonstrated customer need.

## 5. The compiler model

Build a new planning pipeline around reusable existing components. Do not simply host today's full XML generator inside Roslyn.

### Preserve facts instead of pruning them

The index/model should retain:

| Model | Essential contents |
|---|---|
| JVM facts | Binary class/member names, exact descriptors, hierarchy, flags, annotations, actual classpath provider |
| Language semantics | Scoped type variables, substitutions, bounds, bridges, Kotlin declarations, nullability and lowering features |
| Projection policy | Requested managed surface, names, assembly ownership, conversions, callback capability, semantic adapters |
| Executable plan | Invocation kind, marshalling, reference handling, callback slots, adapter requirements, rooting/shrinker requirements |

Keep actual ABI signatures independent from managed signatures.

Reuse the current classfile reader, type-system work, marshallers, runtime invocation, and tests. Do not reuse destructive validation unchanged: a missing nice managed representation must not delete an otherwise known Java declaration from the fact model.

### Kotlin extraction: choose the official reader by default

Use a small JVM helper based on `kotlin-metadata-jvm` for Kotlin semantic extraction, batched when the index changes. Exchange versioned deterministic data with the managed planner.

Keep classfile parsing managed where it already works. Source attachments can improve documentation and diagnostics, but cannot be required for correctness.

This adds a host tool dependency. It is worth evaluating because maintaining another evolving Kotlin metadata implementation is exactly the kind of burden we want to reduce. Do not introduce a permanent daemon initially; measure cold startup and batch/cache the work.

Unsupported metadata versions must produce an explicit support state. Lenient parsing is not permission to claim complete semantics.

### Generics: erase at the ABI boundary, not in the semantic model

Resolve type variables and substitutions across the complete Java hierarchy before projecting them. Preserve JVM bridges and erased callback slots.

Use typed managed facades over erased canonical contracts where that is the simpler correct representation. Use restricted read/write views for variance or wildcard shapes C# cannot express directly.

Do not turn every `List<T>` into a copied `List<T>`, or every unknown type argument into unrestricted mutable `object`. Keep live versus copied collection semantics explicit.

The first substantial generic target should be `ActivityResultContract<I, O>` and representative concrete/custom result contracts. Its repeated real-world metadata adjustments make it a more useful test than a generic-container demo alone.

## 6. Kotlin adapters and async defaults

### Direct JNI first; Kotlin compilation when it earns its cost

Simple callable methods should stay direct JNI operations. Use generated Kotlin source when it avoids reimplementing difficult language lowering:

- Calls omitting default arguments.
- Supported boxed/unboxed value-class operations.
- Suspend invocation and stream collection.
- Later, finite reified specializations or difficult Kotlin override shapes.

Compile against resolved binary dependencies. Do not require their original source, introduce a second Maven resolver, or add KSP/FIR/compiler plugins before a specific requirement justifies them.

### Recommended async contract

Expose supported suspend operations as `Task`/`Task<T>` through a shared adapter. Kotlin owns coroutine execution and its `Job`; managed code owns the task-facing completion contract.

Choose isolated operation failures by default rather than an implicit shared scope in which one call cancels unrelated siblings. Permit an explicit lifecycle/parent scope. Use the normal/default dispatcher for the general profile; APIs requiring Main use an explicit Android UI profile or call context, not a guessed property of the C# caller.

These defaults do not prove an arbitrary API is thread-safe. Honor supported threading information and expose policy choices when intent is unknown.

Cancellation requests the operation's job cancellation. It does not merely abandon the task, and cannot promise to terminate non-cooperative work. Define error/cancellation mapping and resource cleanup once in the shared adapter.

### Recommended stream contract

Provide:

- A collection-per-enumerator adapter for `Flow<T>`, without asserting that all flows are cold.
- A state facade exposing `Value` and updates for `StateFlow<T>`.
- A subscription/broadcast facade for `SharedFlow<T>`.

Default the managed enumeration bridge to demand-driven delivery without an unbounded queue or speculative prefetch. Buffering and overflow changes require an explicit policy.

Preserve Kotlin's own state equality/conflation. Lifecycle cancellation/restart is a named optional profile, not an automatic side effect of seeing a flow return type.

Compose hosting and arbitrary cross-language reified generics should not be first-release completeness claims.

## 7. Build ordering: do not ask a source generator to run the build

This is a critical implementation boundary. Kotlin adapters cannot be selected only after the C# compiler runs if their compilation must also be a build input.

Use a shared, deterministic planner callable from both MSBuild tooling and IDE generation.

### Library build

1. Consume the existing resolved classpath and produce/reuse the semantic index.
2. Plan explicit C# contracts and project-root selections before C# compilation, using original source and references. In the first release, another source generator cannot be the sole producer of these contracts.
3. Generate Kotlin adapter recipes where needed. Compile and inspect probes against the build's binary classpath, with caching.
4. Seal the verified plan, including actual adapter ABI information.
5. Let the incremental source generator emit C# from that plan. It does not invoke Gradle or Kotlin.
6. Emit the assembly and its requirements/adapter manifest.

Do not emit duplicate C# from both MSBuild and Roslyn. Roslyn is the normal C# output owner.

### Final application build

1. Gather manifests from project references, NuGet libraries, foundation packs, and the app's own plan.
2. Validate combined requirements against the actual selected classpath and canonical contracts.
3. Compile the required Java/Kotlin adapters using the selected toolchain; verify the resulting entry points.
4. Complete managed compilation, existing callable-wrapper generation, Java/dex processing, shrinking, and AOT through correctly ordered build integration.

Actual target placement must respect today's callback/Java compilation dependencies. Adapter callback interfaces must be defined through stable source/ABI contracts rather than creating a C#-needs-Kotlin-needs-C# cycle.

Design-time generation uses cached index data and the same pure planner, without external compilation. Missing/stale data gets an actionable restore/build diagnostic. Normal builds reject stale or unverified plans.

## 8. Packaging and the final application's contract

### What a new-system library ships

Its package contains:

- Its managed implementation, including its private generated views.
- A versioned requirement manifest.
- Required generated adapter source/recipes, not upstream Android binaries.
- Optional index/profile data where useful.

It does not need a binding NuGet corresponding to every Maven dependency, nor a bundled vendor JAR/AAR.

Side-by-side adoption must also account for legacy NuGets that still embed Java payloads. Do not double-package those classes alongside their Gradle-resolved copies or silently discard arbitrary payloads. The first supported route should use managed-only foundation packs or explicitly reconciled legacy inputs.

Host-side Java tool dependencies are a separate issue from redistributing Android application libraries. Adapters ultimately become Java/Kotlin bytecode in the APK/AAB; the goal is not to eliminate that implementation from the application.

### Why adapters should ship as recipes/source

If a library needs a generated Kotlin adapter, verify it during the library build but normally have the final app compile its recipe against the final selected dependency graph.

The already-compiled managed library needs stable adapter class/member identities. Assign those deterministically using producer identity, recipe content, and ABI version. Do not deduplicate across producers initially unless identity and compatibility are proven.

Before compilation, validate the original operation requirements. Generate explicit argument types/signature choices and inspect adapter output so a newer overload cannot silently redirect the library to a different operation.

### Manifest minimum

Record:

| Area | Required information |
|---|---|
| Producer | Managed assembly identity and manifest schema |
| Java operations | Binary names, descriptors, invocation/access requirements |
| Relationships | Required hierarchy, interface/default/override obligations |
| Projection | Profile/version, marshalling and semantic-adapter ABI |
| Kotlin | Metadata/features and compiler compatibility requirements where relevant |
| Adapters | Stable entry-point identities, source/recipe hashes, required runtime support |
| Deployment | Shrinker rules, callback roots, required runtime features |
| Provenance | Original providers and relevant input digests, without treating artifact name as type identity |

Use a versioned, inspectable interchange format first. Optimize storage only after measuring it.

The final app unions these **requirements**, not the CLR definitions of private views. It validates every producer, not merely the graph used to compile the app's own source.

An unchanged class moving to another artifact should pass. A missing method, incompatible hierarchy, unsupported adapter ABI, or conflicting public owner should fail with the responsible producer and reason.

Structural validation does not establish semantic compatibility with every version. Preserve dependency constraints and declared tested compatibility ranges rather than declaring two library releases interchangeable merely because the JNI descriptors match.

## 9. Correctness, diagnostics, and performance

### Every selected API needs an outcome

Distinguish typed projection, explicit exact-access fallback, intentional policy exclusion, and unsupported operation. Never report success by quietly removing difficult declarations.

Raw access is not a license to expose internal/compiler infrastructure or assume a lowered reified/composable entry point is meaningful as an ordinary call.

For callbacks and subclasses, missing obligations are build errors. For ambiguous managed names, preserve JVM identity and use deterministic disambiguation; published public aliases need an API baseline.

Generate and test shrinker/rooting information from the same plan. Explicit keep rules in the experiment do not prove this product feature.

For the first release, prefer conservative, targeted retention of selected JNI entry points, adapter classes, and callback members over inventing new obfuscation/remapping behavior. Prove minimal managed callback rooting without rooting the entire test assembly.

Initially target the normal application classloader. Custom classloader/plugin identity and lifetime are a separate capability, not something established by matching a binary class name in the build index.

### Cache the right things

Separate artifact extraction caches from classpath-dependent semantic/planning caches. Include relevant tool/profile versions, annotations, Android platform, and transitive hierarchy/substitution facts.

Make shared cache outputs immutable and atomically published. Do not mutate global process environment or working directories in build tasks. New MSBuild integration must follow this repository's multithreaded task/environment requirements.

### Initial performance gates

For a warm build after an unrelated C# edit:

- Zero JAR rescans.
- Zero Java/Kotlin compiler launches attributable to unchanged projections.
- No rewritten generated sources when the plan is unchanged.

Measure clean/warm indexing, selected/full family generation, dependency upgrade, IDE response, memory, output size, and runtime overhead separately. Absolute latency and allocation budgets require a baseline on specified hardware; none have been demonstrated yet.

## 10. Implementation sequence and release gates

| Stage | Deliverable | Gate |
|---|---|---|
| 1 | A thin semantic-index/planner/manifest slice for the pilot | Preserve declarations and ABI facts; classify current transforms; unsupported features are explicit |
| 2 | Generate the two-library shared-peer experiment | Match all existing identity, dispatch, callback, disposal/GC outcomes without making views owners |
| 3 | First real minimal/full-view SDK pilot | Same engine supports selected contracts and a complete selected family; project/NuGet consumption both work without vendor binaries |
| 4 | Generic Activity result contracts and canonical foundation pilot | Standard and custom contracts work without per-concrete-type repairs in the supported new profile |
| 5 | Suspend/default/value-class and Flow adapters | Binary-only fixtures, races, lifecycle, reference lifetime, and final-app recipe compilation pass |
| 6 | Productization and compatibility matrix | Transitive manifest validation, relocation/upgrades, IDE behavior, schema/toolchain compatibility, R8, and actual callback rooting work |

Keep the first vertical slice small: one existing Java fixture, two independent producer assemblies, one app, one manifest format, and direct JNI calls. Expand semantic coverage as later gates require it; do not make a complete new intermediate representation for every Kotlin feature a prerequisite to the first working slice. Do not begin by rewriting all bindings or building an elaborate IDE.

For device-relevant stages, cover CoreCLR and NativeAOT and the relevant ABIs. Include synchronous/suspended completion, boxing, generic bridge dispatch, exceptions, negative casts, Java/managed subclassing, and lifetime.

For automatic-generation claims, use held-out real library versions. Requiring a new per-artifact XPath fix is a recorded failure of the supported feature claim, not an invisible maintenance step.

## 11. What I would explicitly not build first

- A replacement JNI runtime or peer registry.
- A universal app-generated CLR binding assembly.
- A dynamic C# provider as the primary API.
- Usage-only inference or generator iteration tricks.
- A custom Kotlin compiler plugin or Compose authoring system.
- AI repair during customer builds.
- Guaranteed rich projection of every future binary.
- A new Gradle/Maven dependency-management system.

These exclusions are scope discipline, not conclusions that all such approaches are impossible.

## 12. Evidence, risks, and the decisions that could change the design

**Demonstrated:** The handwritten two-assembly view experiment shared one authoritative peer and passed 11 cases under Debug CoreCLR, Release CoreCLR/R8, and Release NativeAOT. It did not require a production runtime change.

**Observed in source:** Today's generator has substantial generic and Kotlin functionality, but Java dependency pruning and managed projection validity can cause cascading loss. Real Activity transforms repeat generic/override adjustments. Those observations motivate a new semantic boundary; they do not prove every historical transform remains necessary.

**Not demonstrated:** A general generator for these views, the proposed carrier API, automatic shrinker rules, a complete canonical foundation profile, Kotlin-to-C# async adapters, final-app recipe compilation, concurrent disposal guarantees, or performance targets.

The most important risks are callback/generic correctness, build-stage ordering, canonical contract coexistence, and adapter/toolchain compatibility. Each has an explicit gate above; none should be hidden inside a claim that source generation is the solution.

The choices most likely to change the recommendation are:

- A hard demand for existing managed source/binary compatibility everywhere.
- A requirement that independently compiled libraries share every projected type through ordinary CLR assignment with no explicit adaptation.
- A requirement to expose new subclassable foundational APIs without ever updating a shared managed contract.
- A requirement to author general Compose semantics directly in C#.

Absent those constraints, the recommended architecture delivers the main benefits with less new machinery: **local views for breadth, canonical contracts for shared infrastructure, a semantic compiler for correctness, Kotlin adapters for language lowering, and final-app validation for composition.**

### Research basis

This recommendation builds on the session documents *A future for Android library access from C#* and *Binding projections: smaller surfaces, stronger generation*. The latter contains the detailed pinned-source inventory.

Representative primary evidence:

- [Current generator pipeline and cascading validation](https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/CodeGenerator.cs#L105-L329).
- [Actual Activity binding transforms](https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/androidx.activity/activity/Transforms/Metadata.xml).
- [Kotlin metadata strict/lenient reading](https://github.com/JetBrains/kotlin/blob/19c36e179da4ff601be5043468aefb63dcb1f201/libraries/kotlinx-metadata/jvm/src/kotlin/metadata/jvm/KotlinClassMetadata.kt#L381-L418).
- [Kotlin compiler binary classpath support](https://github.com/JetBrains/kotlin-web-site/blob/459a12fc020d3a5a6bf8bc45c1bf551f3a845946/docs/topics/compiler/compiler-reference.md#L344-L357).
- [Suspend ABI](https://github.com/Kotlin/kotlin-spec/blob/0f762a2314a9304e4b3fc386b1aceef1d56c7e4c/docs/src/md/kotlin.core/coroutines.md#L72-L110) and [continuation cancellation/race contracts](https://github.com/Kotlin/kotlinx.coroutines/blob/7e0d1b3e9ca83652427ae00dda7d1673f058a743/kotlinx-coroutines-core/common/src/CancellableContinuation.kt#L43-L81).
- [Roslyn incremental generation constraints](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md).
