# Binding projections: smaller surfaces, stronger generation

Research supplement to *A future for Android library access from C#*, 28 October 2026.

**System-design follow-up:** [Recommendation: a contract-driven Android binding system](android-binding-system-recommendation.md) turns these findings into recommended defaults, component boundaries, build ordering, packaging, and release gates. Its compatibility assumption is not an approved user requirement.

## Recommendation

Build **one semantic binding engine with several projection policies**, not competing generators for small and full bindings.

The useful distinctions are independent:

| Decision | Options |
|---|---|
| How much API to expose | Explicit members, public type surface, full public API family |
| Who owns the managed identity | Canonical managed peer, assembly-local view over an existing peer |
| What the consumer can do | Invoke Java, implement an interface, subclass a Java class |
| How idiomatic to be | Exact JVM access, typed language projection, semantic adapters such as async |

For pervasive infrastructure such as AndroidX Activity, use a stable, canonical managed contract with sufficiently complete inheritance and callback support. For a library privately using a few SDK methods, generate small assembly-local views. Both should consume the same classpath index, language model, marshalling plans, and diagnostics.

The highest-leverage improvement is **separating JVM facts from C# projection choices**. Today, a problem in a pretty C# representation can cause a member, an interface, and then its dependents to be removed. That failure mode is not inherent to JNI.

There is a credible path toward no per-artifact manual repairs for a defined, substantial feature set. There is not a credible basis to promise that every future binary can automatically recover its original source semantics or yield an ideal, backwards-compatible .NET API.

**Evidence boundary:** This phase inspected the generator, its Kotlin metadata handling and regression tests, representative real AndroidX/Kotlin transforms, and primary documentation. It did not implement a new generator or benchmark generation. The previously completed shared-peer experiment demonstrates a runtime building block, not all the proposed projection machinery.

## 1. What generation actually does today

The checked-out Android source revision is `097fd4f7eb6cab27a798e04254ac838c9e8d0369`. The external binding examples below are pinned separately to `dotnet/android-libraries` revision `2393441675a6d82bb817ab7aa2df698c7ec929ce`.

### The pipeline is already more capable than "read Java, print C#"

The MSBuild pipeline distinguishes libraries to bind from reference libraries, extracts class information into XML, runs an XML/type-resolution adjustment, and invokes the C# generator with managed references, transforms, annotations, documentation, namespace policy, and interface-generation options. Generated C# is then added to the project. [G1][G2]

Inside `CodeGenerator.Run`, the principal stages are:

1. Import Java and managed reference information.
2. Resolve/adjust Java API XML.
3. Apply metadata and namespace transforms.
4. Import the managed generator model.
5. Apply generator-level Kotlin corrections.
6. Validate the projected type graph.
7. Form properties and enums and repair overrides/interface implementation.
8. Emit C# and mapping information. [G3]

There are **two Kotlin-aware layers**: classfile/metadata corrections before XML export, and managed-projection corrections after import. There is also an existing Java type system that represents type parameters, generic arguments, wildcard bounds, and base-member relationships. This is not a blank slate with no generics or Kotlin support. [G4][G5]

### The brittle part is graph-wide projection validity

`JavaTypeResolutionFixups` explicitly removes members/types whose Java dependencies cannot be resolved. It warns about missing root types and writes a cascading-resolution report. `JavaTypeCollection.ResolveCollection` repeats resolution when whole types disappear. [G4][G5]

Separately, `CodeGenerator.Validate` repeats managed validation because an invalid member can invalidate an interface, whose removal can invalidate other signatures. The source documents that coupling directly. [G3]

This matters to minimal projections: **a missing managed projection is not necessarily a missing Java type**. A complete Java classpath should establish the JVM facts even if we choose not to create a named C# class for every referenced Java type.

For outbound calls, an unprojected object can often remain an opaque checked reference or receive a local view. For subclassing and interface implementation, more complete callback and hierarchy information is genuinely necessary. Those are different closures and should not share a single "remove anything not representable nicely" rule.

### Existing Kotlin work is valuable, but coverage is uneven

The bytecode layer already recovers source visibility, parameter names, properties, unsigned/value-class information, and source names for mangled methods. Its parameter mapping knows that extension receivers, continuations, and synthetic receiver parameters can differ from Kotlin source parameters. The continuation adjustment is bookkeeping for matching metadata to JVM parameters; it is not itself a generated `Task` adapter. [G6]

Primitive-backed `@JvmInline` classes are already projected as distinct readonly structs in appropriate unboxed positions. A regression test drives real compiled Kotlin fixtures through extraction, import, validation, and generation, and checks distinct typed overloads and properties. This is positive evidence for extending the existing work, not a feature we need to invent from scratch. It is generated-output evidence, not comprehensive device ABI coverage. [G7]

There are important boundaries:

- The inline-class classifier explicitly limits this struct treatment to primitive backing descriptors. Nullable, generic, and other boxed positions cannot be treated as the same primitive call.
- Generator-level mangled class methods are made nonvirtual because the current Java-source callable-wrapper path cannot override those JVM names.
- Remaining C# signature collisions among renamed mangled methods can be resolved by dropping a sibling with a coded warning.
- The inspected metadata decoder records the metadata version, but `ParseMetadata` directly handles class and file/package kinds, not every declared metadata kind. Its shown parse path does not enforce a documented supported-version range. Failure recovery elsewhere warns and proceeds without the corresponding Kotlin recovery. [G6][G8][G9]

These observations identify research and test targets. They do not prove every multifile Kotlin API is absent, nor that all boxed value-class paths fail.

## 2. What actual manual transforms tell us

I inspected real transforms in `dotnet/android-libraries`, rather than treating all metadata edits as arbitrary customer preference.

| Sample | Observed maintenance | Likely underlying category |
|---|---|---|
| AndroidX Activity | Repeated `managedType`, `managedReturn`, `type`, `return`, and override adjustments around `ActivityResultContracts.*` | Generic substitution, erasure, bridge/override representation, and compatibility policy |
| AndroidX Activity | An explicit `getOnBackPressedDispatcher` addition on `ComponentDialog` | A declaration/projection gap to investigate with its exact binary and hierarchy |
| AndroidX Activity | Parameter-name correction for `OnContextAvailableListener` | Missing/recovered naming information or a shared naming preference |
| Lifecycle Runtime Android | `LifecycleRegistryOwner.getLifecycle` managed return correction | Covariant/base-contract projection |
| Kotlin Coroutines Core JVM | Internal-package exclusions, experimental-method exclusions, a return adjustment, and a mangled-name rename | Source visibility, support policy, erasure, and naming |

Sources: [B1][B2][B3][B4].

The striking example is Activity: numerous concrete result contracts repeat variants of the same kind of adaptation. This suggests a high-value general solution for **generic inheritance plus erased callback slots**, rather than a new handwritten rule for every concrete contract.

But these files alone do not establish which edits remain necessary against the newest generator. Some may be historical, deliberate public API compatibility decisions, or no longer match current inputs. A serious modernization should replay them against pinned inputs and classify each active effect before claiming a maintenance reduction.

### Replace one bucket of "fixups" with distinct categories

1. **Missing facts:** unresolved dependency, missing annotations/source names, unsupported metadata version.
2. **Extraction/model defects:** a declaration or substitution was not represented correctly.
3. **Language mismatch:** Java wildcard capture, erased generic overrides, or a JVM name not expressible in Java source.
4. **Projection policy:** naming, events, properties, package visibility, experimental API exposure.
5. **Semantic policy:** cancellation ownership, lifecycle, collection copying, subscription lifetime.
6. **Compatibility commitments:** the historical managed API must remain stable even when a nicer new projection exists.

The first three primarily call for engine improvements. The next two need explicit, reusable profiles. The last requires versioned contracts or adapters; a generator cannot silently redesign an already published API.

XPath transforms can remain a compatibility escape hatch, but they should no longer be the primary place where missing language semantics are repaired.

## 3. Managing projections: a blended authoring model

### A. Small explicit contracts

For internal SDK use, let a consumer declare the desired managed contract and generate its implementation. For example, the following is **proposed syntax, not an available API**:

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

Ordinary cases should not require JNI descriptors. The generator can match the C# declaration against indexed JVM candidates. Ambiguous overloads, boxing choices, and semantic conversions must produce diagnostics or accept an explicit signature selector, rather than guess.

This approach also lets the managed API intentionally remain much smaller and more stable than the underlying SDK. The generated view retains a canonical peer; it does not become another registered owner.

### B. Type/package roots with policy

For consumers wanting familiar generated APIs, permit selection by Java type, package, or named API-family profile. A conceptual configuration could say:

```xml
<AndroidProjection Include="androidx.activity.**"
                   Surface="Full"
                   Representation="CanonicalPeer"
                   Profile="AndroidFoundation" />

<AndroidProjection Include="com.example.widgets.Widget"
                   Surface="Selected"
                   Representation="LocalView"
                   Members="getValue;setValue" />
```

These item names and values are illustrative. Selecting a member name could conservatively include its overload group; precise signature selection would also be available.

Roots must compute a **deterministic, explainable closure**, not recursively bind every Maven dependency:

- Outbound-call closure: required callable signatures, marshallers, and selected object views.
- Public managed-contract closure: types appearing in exported .NET signatures.
- Callback closure: all obligations needed to implement the requested interface.
- Subclass closure: constructors, inherited abstract methods, protected access, override slots, dispatch rules, and the authoritative owner.

A report should explain why each additional member/type was included. A large dependency graph need not imply a large generated managed API.

### C. Full foundational contract packs

AndroidX Activity, Fragment, Lifecycle, and similarly pervasive contracts deserve a different product experience from a private three-method SDK view:

- Stable public CLR names and assembly ownership.
- Complete supported public/protected inheritance and callback surfaces.
- Carefully versioned generic, nullability, async, and collection conventions.
- Familiar discovery, documentation, and tooling.
- No requirement that their NuGets redistribute the corresponding JARs/AARs.

Such packs could contain precompiled managed contracts, generation recipes/index data, or reference contracts plus deliberately late-generated implementations. Those are delivery choices; "full" need not mean "ship Java binaries."

There is a real cadence tradeoff: a precompiled canonical pack still needs a managed release to add members to its public classes. A recipe-driven full projection can expose newly discovered supported APIs without that particular release, but must solve compilation and ownership through the explicit contract scheme. A practical intermediate blend is a stable canonical component baseline plus generated local views or extension helpers for new outbound APIs. Helpers cannot add virtual slots or turn into partial declarations of a class in another assembly.

The late-assembly option retains the constraint discussed earlier: separately compiled libraries need consistent reference identities and compatible declarations. Ordinary generators do not merge arbitrary DLLs, and type identity is not repaired just by giving two local views the same namespace/name.

### D. Usage-assisted discovery, not usage-only correctness

An IDE can suggest missing projections, offer a member picker, and scaffold explicit contracts from a broad indexed API catalog. An analyzer can identify unused generated members and propose tightening a manifest.

I would not initially make arbitrary unresolved C# usage the sole generation specification. Overload resolution, generic inference, chained expressions, and output from other generators create a bootstrap problem. Roslyn generators are additive and unordered; they do not get an automatic fixed-point loop over one another's generated output. [P1]

A useful hybrid is: broad indexed discovery in the IDE, explicit persisted roots/contracts for repeatable builds, and conservative usage analysis as an optimization.

### Surface selection and assembly ownership must stay orthogonal

It should be possible to request a full local view for convenience, or a small canonical callback contract for implementation. Full and minimal do not inherently determine ownership.

For cross-library boundaries, prefer shared canonical public contracts or an explicit shared carrier and checked adaptation. Do not silently equate independently defined CLR view classes. Keep library-private views internal whenever possible.

Each compiled producer should also carry a machine-readable record of its required JVM operations and callback contracts. The final application can union those requirements, validate them against its actual classpath, and aggregate shrinker/rooting requirements. It need not union the CLR identities of already-compiled local views.

This makes project references and packaged libraries consistent: each library compiles its own private projection; the app validates the combined Java contract. A deliberate shared-assembly/reference-contract mode remains a separate choice. The final app cannot retroactively replace another DLL's public managed signatures without rebuilding or rewriting that DLL.

## 4. A semantic binding compiler, not a larger bag of XML patches

Retain and extend the working class parser, Java type system, marshallers, callback infrastructure, and regression corpus. The long-term boundary should nevertheless be a richer immutable model:

```text
Resolved classpath
    -> classfile facts + annotations + Kotlin semantic overlay
    -> Java/Kotlin declaration and override graph
    -> projection roots + versioned policy
    -> exact JVM call/callback plans
    -> C# declarations + optional Kotlin bridges + shrinker/rooting data
```

### Preserve three different signatures

For every operation retain:

1. **JVM identity:** binary declaring class, actual JVM name, descriptor, invocation kind.
2. **Language declaration:** type parameters/substitution, nullability, Kotlin source name, receiver/default/suspend/value-class information.
3. **Managed projection:** CLR identity, chosen names/types, overload policy, ownership and conversion contract.

Never mutate a pretty managed type and then infer the native ABI from it. Existing value-class work and explicit JNI-signature compatibility checks are useful precedents for keeping the two distinct. [G6][G7][G10]

Track provenance and confidence in *facts*: classfile descriptor, generic `Signature`, recognized Kotlin metadata, external annotation, optional source attachment, or explicit profile rule. Unknown nullability is not the same as non-null; metadata parsing success is not proof every newer flag was interpreted.

### Keep exact access when the nice projection fails

Within the declared supported ABI/access policy:

- Preserve the callable operation with an exact low-level signature and checked object carrier when rich type projection is unavailable.
- Preserve colliding operations with stable disambiguation based on their actual identity; do not make source/classfile ordering select the winner.
- Generate a Kotlin bridge when a supported language operation needs compiler lowering.
- Fail the requested rich/callback contract explicitly if it cannot be implemented correctly.

A raw fallback must be visible in the manifest and diagnostics, not silently presented as equivalent to a typed API. It must not bypass private/internal API exposure policy.

Nor does every method present in bytecode constitute a valid ordinary source operation: compiler infrastructure, reified-inline placeholders, and Compose lowering need feature-aware classification. Finding a descriptor is necessary for an exact call, not sufficient to certify meaningful semantics.

Full rich surfaces still need coherent CLR inheritance. An opaque return can rescue many outbound calls; it cannot automatically satisfy an abstract override or make an incompatible managed interface implementable.

### Use compiler output as an oracle where appropriate

Generate and compile tiny probes or adapters for risky shapes instead of guessing at Kotlin lowering. Validate resulting classfile descriptors against the call plan. Validate generated C# with the C# compiler, including actual generic override/interface implementation.

For ordinary Java/Kotlin calls, direct JNI remains the default. Compiler-assisted bridges should be feature-driven, not a requirement to rebuild every library from source or compile one helper per trivial method.

### Keep indexing out of the Roslyn hot path

Classpath resolution is an existing input, not a new dependency-management project. After resolution:

- Extract immutable, content-addressed classfile data once.
- Resolve graph-dependent hierarchy/substitution facts against the selected classpath.
- Give each incremental generator only the selected declaration shards and policy.
- Invalidate on relevant shape, hierarchy, semantic-policy, or toolchain changes.
- Keep design-time generation offline and avoid running Gradle, parsing every JAR, or invoking AI inside the generator.

Artifact hashes alone are insufficient for every cache: an unchanged class can acquire different override relationships when a dependency changes. Conversely, moving an unchanged class between Maven artifacts should not force a new managed identity.

Roslyn's incremental cookbook specifically emphasizes value-equatable pipeline models, explicit marker attributes, and avoiding retention of compilation symbols in cached models. Those are concrete design constraints, not evidence that moving today's entire generator into Roslyn will be fast. [P1]

## 5. Generics deserve a first-class redesign

### Erasure is an ABI fact, not a reason to discard the type system

Java's compiler erases type parameters to bounds or `Object` and generates bridges to preserve polymorphism. The optional classfile `Signature` attribute carries richer generic declarations. The generic signature and JVM descriptor are distinct and can even differ in parameter bookkeeping. [P2][P3]

The current generator already has generic symbols and concrete generic collection marshalling. Nevertheless, `GenericTypeParameter` uses `Java.Lang.Object` for its ordinary managed type, and `GenericSymbol.FullName` is the underlying generator type while generic spelling is handled through separate paths. Generic interface implementation also has special-case machinery. This is evidence of a partial/erasure-oriented projection model, not absence of generic parsing. [G11][G12][G13][G14]

The new model should preserve scoped type-variable identity, substitution through complete hierarchies, multiple bounds, wildcards/capture, raw types, bridge methods, and covariant returns independently of how C# ultimately spells them.

### ActivityResultContract is a better proving ground than a toy List

A Java/Kotlin generic result contract has conceptually typed input/output, but its erased callback slots use object references. Concrete contracts can introduce specialized signatures and compiler bridges.

A promising design separates:

- An authoritative erased callback/peer contract.
- A typed managed projection of input/output.
- Generated conversions and bridge/override relationships.

That could avoid repeating object/return/override repairs for every concrete Activity result contract. It needs a real prototype: generic managed owners, callable wrappers, Java bridge dispatch, and existing public API compatibility are not automatically solved by printing `<TInput, TOutput>`.

### Some mappings should use views instead of pretending the languages agree

| Shape | Proposed treatment |
|---|---|
| `List<String>` | Typed live collection view or explicit snapshot policy |
| `List<Int>` from Kotlin | Boxed Java integer elements, not primitive JNI integer parameters |
| `Producer<out T>` | Variant managed interface where CLR variance rules permit it |
| Java/Kotlin generic class covariance/use-site wildcard | Invariant carrier plus suitable read/write capability views or adapters |
| Star projection / raw Java type | Explicit unknown/erased view, not a fabricated precise type argument |
| Intersections and definitely-non-null generic types | Preserve model facts; expose enforceable constraints/checks and diagnose approximation |
| Generic arrays | Preserve runtime array component type and element conversion |

CLR variance applies to interfaces and delegates, not arbitrary generic classes, and its conversions differ for value types. Java/Kotlin variance therefore cannot be translated by blindly copying `in`/`out`. [P4]

Do not silently turn a live Java collection into a copied `List<T>`: mutation, identity, equality, ordering, and observation can change. Kotlin's read-only collection type also does not prove that the underlying object is immutable.

Erasure means a runtime check can establish `java.util.List`, not generally `List<String>`. Element conversion may require checks as values are read. Checked views should not claim stronger runtime verification than Java provides.

## 6. Kotlin changes the extraction and the adapter strategy

### Recover Kotlin declarations, not just Java-shaped bytecode

Kotlin metadata can describe source properties, type parameters and variance, nullable/flexible/definitely-non-null types, receivers, defaults, suspend/inline/reified/value-class flags, and links to actual JVM declarations. Join those facts to the classfile, rather than substituting either representation for the other. The official `kotlin-metadata-jvm` model is a useful semantic authority and conformance reference. [K1]

There are two reasonable extraction implementations to evaluate:

- Extend the current managed reader, with explicit version support and differential tests against the official reader.
- Run a versioned JVM metadata helper during indexing and exchange a stable language-neutral model with the managed generator.

The second could reduce the burden of tracking Kotlin's evolving metadata schema, at the cost of a JVM tool dependency and process/toolchain management. Neither requires redistributing the application's Android libraries through NuGet.

Make recognized strict metadata support, lenient/degraded interpretation, and JVM-only recovery distinct states. The official reader itself distinguishes strict and lenient reading; a successful lenient read need not include all newer semantics. Missing or stripped metadata cannot reliably be reconstructed from JVM descriptors. Classfile annotations remain important, and source-retained annotations are not recoverable from the binary. [K2]

### Compile ordinary Kotlin adapters against binary dependencies

**The dependency's Kotlin source is not required for this approach.** A generated Kotlin compilation unit can call APIs from a JAR/classpath dependency, and the Kotlin compiler performs ordinary symbol resolution, overload checking, accessibility checks, default-argument handling, and supported lowering. This is a more promising default than implementing every lowering convention ourselves. [K3]

KSP can expose dependency declarations from classfiles, but it does not recover source bodies or expressions. FIR/compiler plugins offer deeper access, but their APIs evolve rapidly; official documentation also warns that arbitrary backend IR transformations are not subsequently compiler-checked. Start with metadata plus normal Kotlin source compilation, and introduce a plugin only for a demonstrated capability gap. [K3][K4]

Compiler acceptance is not proof of correct JNI ownership, lifecycle, C# marshalling, or semantics of a cross-runtime adapter. Those still need independent validation.

### Suspend functions: own an operation, not a trailing callback

A lowered suspend function has an additional continuation and an object return. It can complete immediately by returning a value, or return `COROUTINE_SUSPENDED` and complete through the continuation later. The continuation also carries coroutine context. "Attach a callback and ignore the return" is not correct. [K5]

Prefer a generated Kotlin adapter that calls the suspend function normally inside an owned coroutine, exposing a small completion contract and an operation handle to C#. A common managed adapter can then provide `Task<T>` and request cancellation through that operation's Kotlin `Job`.

There is an upstream precedent in `CoroutineScope.future`, including linking future cancellation to the coroutine. It establishes feasibility, not a requirement to use `CompletableFuture` as the Android transport. [K6]

Define the adapter contract once, with selectable family/application profiles:

- Which scope owns the operation and when it is disposed.
- Which dispatcher starts it; managed continuation scheduling is a separate concern.
- Whether a failing call can cancel siblings or whether supervision isolates unrelated calls.
- How Kotlin cancellation maps to task cancellation, token attribution, and exception preservation.
- How cancellation registrations, JNI references, and resource-valued results are released in races.

Canceling a `CancellationToken` should request cancellation of the operation, not merely stop awaiting while foreign work keeps running. Conversely, cancellation is cooperative and cannot promise to stop arbitrary underlying work.

Preserve Kotlin's continuation/job semantics, including cancel/resume races and prompt cancellation at supported suspension points. A managed "first callback wins" gate alone is not the whole operation contract. Test immediate results, `null`, `Unit`, immediate throw, delayed completion, cancellation before start/during suspension/during dispatch, simultaneous failure/cancel, and cleanup when a result cannot be delivered. [K7]

### Flow, StateFlow, and SharedFlow need different projections

| Kotlin abstraction | Proposed managed projection | Semantics to preserve |
|---|---|---|
| `Flow<T>` | An asynchronous-enumeration adapter | Collection, cancellation, backpressure, completion/error, and context rules; the interface itself does not guarantee coldness |
| `StateFlow<T>` | State holder with `Value` and updates/subscriptions | Initial/current value, hot behavior, latest-value replay, equality conflation, no normal completion |
| `SharedFlow<T>` | Broadcast/subscription view, possibly with async enumeration | Replay, per-subscriber behavior, buffering/overflow, no normal completion, no intrinsic failure signal |

Sources: [K8][K9][K10].

An unbounded managed queue would silently alter memory and backpressure behavior. A plain delegate callback may also remove suspending delivery. Choose and document an adapter policy; do not make those changes an accidental implementation detail.

Do not add .NET equality conflation on top of a `StateFlow`: let the Kotlin object apply its own equality rules. Exposing `IAsyncEnumerable<T>` can be useful without pretending it expresses the entire state/broadcast abstraction.

Android lifecycle ownership is another explicit profile. `repeatOnLifecycle` cancels and restarts collection as lifecycle state changes; that behavior does not follow merely from a return type of `Flow<T>`. [K11]

### Defaults, value classes, reification, and interface modes

| Feature | Better automatic treatment |
|---|---|
| Default parameters | Generate Kotlin calls omitting the selected arguments. Metadata records that a default exists, not its potentially side-effecting expression. Do not invent C# constant defaults or guess `$default` masks. |
| Value classes | Use direct unboxed JNI where verified, and compiler-produced boxing/construction/call bridges where needed. Generic, nullable, and interface positions can have different representations. |
| Mangled virtual APIs | Investigate Kotlin-generated subclass/override adapters, which can spell the source operation and let Kotlin emit its JVM name. This could extend today's Java-source callable-wrapper boundary, but is not yet demonstrated here. |
| Inline/reified functions | Offer concrete Kotlin specializations where the compiler can compile them from the binary dependency. Arbitrary CLR type parameters are not automatically Kotlin call-site reification. |
| Interface defaults | Model the actual binary interface/default-implementation/compatibility-bridge layout; do not assume one historical `DefaultImpls` scheme. |
| Top-level/extensions/properties | Recover the Kotlin declaration and its JVM owner/accessors, then choose ordinary C# helpers/properties/extension methods through policy. |

Sources: [K12][K13][K14][K15].

`@JvmExposeBoxed` and related compiler options can help declarations compiled under our control. They do not retroactively regenerate an existing dependency or guarantee inherited coverage. They are an opportunity, not a universal repair switch. [K13]

### Compose belongs in a specialized tier

`@Composable` is not just a method with extra ignorable parameters. It changes calling context and compiler lowering, including composer/change/default machinery. A credible bridge would own a Kotlin composition entry point compiled with the appropriate Compose compiler plugin, with explicit managed state/callback/disposal integration.

That is worth exploring, but it should not be counted as supported merely because a raw generator can expose the lowered JVM signature. Treat it as a separate integration tier with its own compiler/runtime compatibility tests. [K16]

### A realistic automation promise

| Tier | Promise |
|---|---|
| Metadata-aware | Accurately project supported recorded declarations, with version diagnostics and explicit fallback |
| Compiler-assisted | Generate verified Kotlin calls/adapters against binary dependencies for supported features |
| Async/stream | Provide documented, shared operational semantics rather than per-library handwritten wrappers |
| Specialized | Offer finite reified specializations, Compose hosting, or other feature-specific integrations with explicit limits |

The most useful combination is **metadata for semantic recovery, Kotlin for Kotlin lowering, and C# for the chosen managed contract**. That is a stronger route to automatic success than trying to make Java-shaped XML infer everything.

## 7. Completeness should be measurable

The engine should emit a projection manifest accounting for every API in the selected public/source-visible universe:

- Richly projected.
- Exact-access fallback only.
- Included solely as a dependency/callback obligation.
- Intentionally excluded by a named profile rule.
- Unsupported, with a specific diagnostic and cause.

Keep the raw JVM-public inventory separate from Kotlin source-public policy and synthetic infrastructure. Otherwise, a percentage can be inflated by redefining the denominator or counting implementation bridges as user-facing completeness.

The target is not "C# compilation succeeded after removing the difficult types." It is **zero unexplained loss, zero known ABI mismatches, and zero per-artifact repairs for the declared supported feature set**. Rich-projection coverage and compatibility must be measured separately.

### Use the existing maintenance corpus as a training set for deterministic rules

Replay current metadata against pinned Maven inputs and the baseline generator. For each transform:

1. Determine whether it still matches and changes output.
2. Identify the actual compiler/runtime/public-API reason.
3. Replace mechanical repairs with engine rules and a minimized regression fixture.
4. Move opinionated choices to versioned profiles.
5. Preserve compatibility-specific edits in a legacy profile until intentionally migrated.

This avoids declaring success by simply ignoring old public contracts, and avoids training a new system on stale workarounds.

AI is useful for clustering repeated fixes, minimizing fixtures, drafting rules/adapters, and generating adversarial test cases. Accepted results should become reviewed deterministic code and test inputs. A normal customer build should not need an AI service, network access, or nondeterministic repair.

## 8. Prioritized experiments and acceptance gates

| Priority | Experiment | What would establish progress |
|---|---|---|
| 1 | Shadow analysis of existing Activity/Lifecycle/coroutines transforms | Active effects classified; baseline rich/raw coverage; no unsupported causal claims from XML alone |
| 2 | Generate the already-demonstrated small local views from contracts | Same cross-assembly identity, argument/return, callback, disposal, and GC behavior as the handwritten experiment |
| 3 | Generic Activity result contracts | Representative concrete and custom contracts compile and run without per-contract metadata repairs under a new profile |
| 4 | Compiler-assisted suspend/default/value-class bridges | ABI checked against real compiled Kotlin; completion/cancellation/default/boxing cases pass on device |
| 5 | A foundational ComponentActivity profile | Ordinary app inheritance, lifecycle, Activity results, protected callbacks, cross-library consumers, trimming and R8 all work |
| 6 | Version and ownership matrix | Same-class artifact relocation is tolerated; real ABI/hierarchy changes produce precise diagnostics or supported adapters |

Exercise the affected runtime paths under CoreCLR and NativeAOT; test shrinker behavior and actual callback rooting, not just generated text. Extend across relevant ABIs/toolchain versions as the feature set grows.

The synthetic shared-peer experiment is a useful starting point for priority 2. It did not establish real AndroidX subclassing, generic container adaptation, coroutine correctness, custom classloader support, concurrent disposal safety, or automatic R8 rules.

### Performance and reliability metrics

Measure clean indexing, warm no-op build, one-member projection change, unrelated C# edit, dependency-version change, and IDE latency separately. Record generated LOC, compiler memory, managed assembly/APK size, JNI transitions, allocations, and callback cost. No performance threshold has been measured in this research; establish a baseline and agree numeric budgets before accepting an implementation.

Maintain a pinned corpus across Java/Kotlin compiler versions and representative AndroidX, Kotlin standard library, coroutines, and third-party APIs. Add:

- Differential tests between Kotlin/Java calls and generated C# calls.
- Compilation tests of the complete selected surface, not only snapshots.
- Device tests of dispatch, exceptions, reference lifetime, and adapters.
- Property-based/matrix tests for signatures, generic substitutions, nullability, defaults, and boxed/unboxed positions.
- Held-out library/version upgrades to measure whether new manual repairs are actually required.

## 9. What this means for maintenance and compatibility

The durable thing to ship becomes a **projection engine plus versioned knowledge**, not a synchronized NuGet product release for each Maven ownership change.

That knowledge includes general language rules, optional family-level semantic profiles, stable public managed contracts, and explicit compatibility requirements. A graph-aware build selects the actual Java implementation and validates the required shape.

This does not make upstream API removal or semantic change disappear. A stable precompiled managed contract still constrains the Java APIs it can run against. A newly generated local view can change independently only to the extent its containing library's own public contract permits.

The practical goal is:

> Make new, structurally supported APIs boring to consume automatically; concentrate human decisions on product-level semantics and long-lived managed compatibility, not repetitive descriptor, generic, and naming repair.

## Sources

Repository links below are pinned to the inspected revision. Ranges identify the relevant implementation, not a claim that every nearby branch was executed.

- **G1:** `Xamarin.Android.Bindings.ClassParse.targets:16-83` - extraction and reference inputs.
- **G2:** `Xamarin.Android.Bindings.Core.targets:80-164` - generator inputs/options and C# compilation.
- **G3:** `CodeGenerator.cs:105-245,282-329` - pipeline and cascading managed validation.
- **G4:** `JavaTypeResolutionFixups.cs:15-85` - Java dependency pruning and reports.
- **G5:** `JavaTypeCollection.cs:122-205` - iterative resolution, generic references, base-member resolution.
- **G6:** Bytecode `KotlinFixups.cs:80-182,261-407` - value-class constraints, semantic corrections, and parameter mapping.
- **G7:** `KotlinInlineClassEndToEndTests.cs:13-164` - real-bytecode to generated-C# regression evidence.
- **G8:** Generator transformation `KotlinFixups.cs:20-88` - nonvirtual mangled methods and collision removal.
- **G9:** `KotlinMetadata.cs:14-108,151-159` - version fields and metadata-kind dispatch.
- **G10:** `Method.cs:261-296` - explicit JNI-signature ABI validation.
- **G11:** `GenericTypeParameter.cs:13-35` - managed erasure path and JVM bound.
- **G12:** `GenericSymbol.cs:26-29,93-153` and `ClassGen.cs:78-125` - generic spelling/substitution and interface handling.
- **G13:** `CollectionSymbol.cs:13-35,70-96` - existing concrete generic collection marshalling.
- **G14:** `ClassGen.cs:78-125` - generic/covariant explicit interface implementation.

[G1]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/src/Xamarin.Android.Build.Tasks/MSBuild/Xamarin/Android/Xamarin.Android.Bindings.ClassParse.targets#L16-L83
[G2]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/src/Xamarin.Android.Build.Tasks/MSBuild/Xamarin/Android/Xamarin.Android.Bindings.Core.targets#L80-L164
[G3]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/CodeGenerator.cs#L105-L329
[G4]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.Transformation/JavaTypeResolutionFixups.cs#L15-L85
[G5]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/src/Java.Interop.Tools.JavaTypeSystem/JavaTypeCollection.cs#L122-L205
[G6]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/src/Xamarin.Android.Tools.Bytecode/Kotlin/KotlinFixups.cs#L80-L407
[G7]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tests/generator-Tests/Unit-Tests/KotlinInlineClassEndToEndTests.cs#L13-L164
[G8]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.Transformation/KotlinFixups.cs#L20-L88
[G9]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/src/Xamarin.Android.Tools.Bytecode/Kotlin/KotlinMetadata.cs#L14-L159
[G10]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.ObjectModel/Method.cs#L261-L296
[G11]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.ObjectModel/Symbols/GenericTypeParameter.cs#L13-L35
[G12]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.ObjectModel/Symbols/GenericSymbol.cs#L26-L153
[G13]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.ObjectModel/Symbols/CollectionSymbol.cs#L13-L96
[G14]: https://github.com/dotnet/android/blob/097fd4f7eb6cab27a798e04254ac838c9e8d0369/external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.ObjectModel/ClassGen.cs#L78-L125

- **B1:** [Activity transforms][B1].
- **B2:** [Activity parameter naming][B2].
- **B3:** [Lifecycle Runtime Android transforms][B3].
- **B4:** [Coroutines Core JVM transforms][B4].
- **P1:** [Roslyn incremental generator cookbook][P1], including additive/unordered generation and incremental-model guidance.
- **P2:** [Oracle: Java type erasure][P2].
- **P3:** [JVM specification: Signature attribute][P3].
- **P4:** [Microsoft Learn: CLR covariance and contravariance][P4].
- **P5:** [Microsoft Learn: existing binding metadata customization][P5].

[B1]: https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/androidx.activity/activity/Transforms/Metadata.xml
[B2]: https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/androidx.activity/activity/Transforms/Metadata.ParameterNames.xml
[B3]: https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/androidx.lifecycle/lifecycle-runtime-android/Transforms/Metadata.xml
[B4]: https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/org.jetbrains.kotlinx/kotlinx-coroutines-core-jvm/Transforms/Metadata.xml
[P1]: https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md
[P2]: https://docs.oracle.com/javase/tutorial/java/generics/erasure.html
[P3]: https://docs.oracle.com/javase/specs/jvms/se25/html/jvms-4.html#jvms-4.7.9
[P4]: https://learn.microsoft.com/en-us/dotnet/standard/generics/covariance-and-contravariance
[P5]: https://learn.microsoft.com/en-us/dotnet/android/binding-libs/customizing-bindings/java-bindings-metadata

### Kotlin primary sources

The independent Kotlin research used upstream implementations and official language/tool documentation. These revisions are evidence for the described contracts, not a tested compatibility matrix for a proposed .NET implementation.

- **K1:** [Kotlin metadata type model][K1] and [JVM signature extensions](https://github.com/JetBrains/kotlin/blob/19c36e179da4ff601be5043468aefb63dcb1f201/libraries/kotlinx-metadata/jvm/src/kotlin/metadata/jvm/JvmExtensions.kt#L65-L150).
- **K2:** [Strict/lenient metadata reading][K2]; source-retention boundary: [Kotlin annotation retention](https://github.com/JetBrains/kotlin/blob/19c36e179da4ff601be5043468aefb63dcb1f201/libraries/stdlib/src/kotlin/annotation/Annotations.kt#L51-L60).
- **K3:** [Kotlin compiler classpath options][K3]; [KSP classpath lookup](https://github.com/google/ksp/blob/93d71709eda9c0e6a39b281c5434834d9fdcec4f/api/src/main/kotlin/com/google/devtools/ksp/processing/Resolver.kt#L262-L272); [KSP's supported symbol scope](https://github.com/JetBrains/kotlin-web-site/blob/459a12fc020d3a5a6bf8bc45c1bf551f3a845946/docs/topics/ksp/ksp-overview.md#L5-L36).
- **K4:** [Compiler-plugin stability and checking boundaries][K4].
- **K5:** [Kotlin suspend ABI specification][K5].
- **K6:** [CoroutineScope.future implementation][K6].
- **K7:** [CancellableContinuation races and cancellation guarantees][K7]; [Job parent/failure behavior](https://github.com/Kotlin/kotlinx.coroutines/blob/7e0d1b3e9ca83652427ae00dda7d1673f058a743/kotlinx-coroutines-core/common/src/Job.kt#L18-L110).
- **K8:** [Flow contract][K8].
- **K9:** [StateFlow contract][K9].
- **K10:** [SharedFlow contract][K10].
- **K11:** [repeatOnLifecycle implementation contract][K11].
- **K12:** [Metadata default-value limitations][K12].
- **K13:** [Value-class boxing][K13] and [boxed JVM exposure](https://github.com/JetBrains/kotlin-web-site/blob/459a12fc020d3a5a6bf8bc45c1bf551f3a845946/docs/topics/jvm/java-to-kotlin-interop.md#L660-L718).
- **K14:** [Reified type parameter rules][K14].
- **K15:** [Interface default compatibility modes][K15].
- **K16:** [Composable calling contract][K16] and [compiler lowering](https://github.com/JetBrains/kotlin/blob/19c36e179da4ff601be5043468aefb63dcb1f201/plugins/compose/compiler-hosted/src/main/java/androidx/compose/compiler/plugins/kotlin/lower/ComposerParamTransformer.kt#L697-L721).

[K1]: https://github.com/JetBrains/kotlin/blob/19c36e179da4ff601be5043468aefb63dcb1f201/libraries/kotlinx-metadata/src/kotlin/metadata/Nodes.kt#L626-L717
[K2]: https://github.com/JetBrains/kotlin/blob/19c36e179da4ff601be5043468aefb63dcb1f201/libraries/kotlinx-metadata/jvm/src/kotlin/metadata/jvm/KotlinClassMetadata.kt#L381-L418
[K3]: https://github.com/JetBrains/kotlin-web-site/blob/459a12fc020d3a5a6bf8bc45c1bf551f3a845946/docs/topics/compiler/compiler-reference.md#L344-L357
[K4]: https://github.com/JetBrains/kotlin-web-site/blob/459a12fc020d3a5a6bf8bc45c1bf551f3a845946/docs/topics/compiler-plugins/custom-compiler-plugins.md#L3-L106
[K5]: https://github.com/Kotlin/kotlin-spec/blob/0f762a2314a9304e4b3fc386b1aceef1d56c7e4c/docs/src/md/kotlin.core/coroutines.md#L72-L110
[K6]: https://github.com/Kotlin/kotlinx.coroutines/blob/7e0d1b3e9ca83652427ae00dda7d1673f058a743/kotlinx-coroutines-core/jvm/src/future/Future.kt#L10-L102
[K7]: https://github.com/Kotlin/kotlinx.coroutines/blob/7e0d1b3e9ca83652427ae00dda7d1673f058a743/kotlinx-coroutines-core/common/src/CancellableContinuation.kt#L43-L81
[K8]: https://github.com/Kotlin/kotlinx.coroutines/blob/7e0d1b3e9ca83652427ae00dda7d1673f058a743/kotlinx-coroutines-core/common/src/flow/Flow.kt#L7-L65
[K9]: https://github.com/Kotlin/kotlinx.coroutines/blob/7e0d1b3e9ca83652427ae00dda7d1673f058a743/kotlinx-coroutines-core/common/src/flow/StateFlow.kt#L10-L66
[K10]: https://github.com/Kotlin/kotlinx.coroutines/blob/7e0d1b3e9ca83652427ae00dda7d1673f058a743/kotlinx-coroutines-core/common/src/flow/SharedFlow.kt#L10-L93
[K11]: https://github.com/androidx/androidx/blob/1451e921fbdc483fabf7fe8be57523cde77587c7/lifecycle/lifecycle-runtime/src/commonMain/kotlin/androidx/lifecycle/RepeatOnLifecycle.kt#L30-L64
[K12]: https://github.com/JetBrains/kotlin/blob/19c36e179da4ff601be5043468aefb63dcb1f201/libraries/kotlinx-metadata/src/kotlin/metadata/Attributes.kt#L543-L549
[K13]: https://github.com/JetBrains/kotlin-web-site/blob/459a12fc020d3a5a6bf8bc45c1bf551f3a845946/docs/topics/inline-classes.md#L104-L140
[K14]: https://github.com/JetBrains/kotlin-web-site/blob/459a12fc020d3a5a6bf8bc45c1bf551f3a845946/docs/topics/inline-functions.md#L173-L201
[K15]: https://github.com/JetBrains/kotlin-web-site/blob/459a12fc020d3a5a6bf8bc45c1bf551f3a845946/docs/topics/jvm/java-to-kotlin-interop.md#L272-L356
[K16]: https://github.com/androidx/androidx/blob/1451e921fbdc483fabf7fe8be57523cde77587c7/compose/runtime/runtime/src/commonMain/kotlin/androidx/compose/runtime/Composable.kt#L19-L31
