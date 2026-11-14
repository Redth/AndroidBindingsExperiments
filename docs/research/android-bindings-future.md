# A future for Android library access from C#

Research and architectural analysis, 28 October 2026.

## Executive judgment

**System recommendation:** [Recommendation: a contract-driven Android binding system](android-binding-system-recommendation.md) makes the proposed architecture and first implementation sequence concrete. It distinguishes recommended assumptions from confirmed requirements.

The most promising direction is **not to replace shipped bindings with one universal source generator or one dynamic object**. It is to separate responsibilities that today's binding packages bundle together:

1. The resolved Java dependency graph, already supplied by the existing Gradle integration.
2. Exact JVM type/member identities and invocation plans.
3. Optional, opinionated C# API projections.
4. Java-to-managed callback implementations.
5. Managed public API compatibility for reusable .NET libraries.

My recommendation is a layered system:

- A graph-aware, content-addressed JVM API index.
- Deterministic generation of ordinary bindings or small explicitly requested contracts, using an offline-capable cache.
- A deliberately less ambitious, exact JVM access layer when an idiomatic projection is unsupported.
- Kotlin-compiled adapters for features where the Kotlin compiler is the right semantic authority.
- Existing static callable-wrapper machinery for subclasses, with a generic Java interface-proxy mechanism as an optional convenience.
- Curated, binary-free .NET facades only where a stable, idiomatic managed API has real customer value.

The central change in the maintenance model is this:

> Ship a binding system and reviewed binding knowledge, rather than requiring a synchronized NuGet product release for every upstream Maven release.

That does not make upstream API changes disappear. It moves routine artifact/version synchronization into deterministic tooling, while concentrating human work on semantic exceptions and managed compatibility commitments.

**Follow-up result:** A bounded assembly-local-view experiment is now implemented and exercised on Android. Two independently compiled libraries expose different minimal projections of one Java class, share one authoritative managed peer, and preserve managed-subclass callbacks and lifetime without production runtime changes. All 11 cases passed on Debug CoreCLR, Release CoreCLR with full managed trimming/R8, and Release NativeAOT. The implemented model, exact commands, limitations, and implications are recorded under section 10 below. This makes local views a demonstrated architectural option, not just a dynamic-interop suggestion.

**Projection-generation follow-up:** [Binding projections: smaller surfaces, stronger generation](binding-projection-generation.md) examines the current generator, real AndroidX/Kotlin transforms, generic override repair, mixed full/minimal projection management, and compiler-assisted Kotlin adapters. It refines the Kotlin recommendations using additional primary-source research. No new generator or adapter implementation is claimed by that supplement.

**Evidence status:** The current checkout and the primary external sources listed below were inspected. Except for the explicitly reported shared-peer experiment, architecture, performance expectations, API sketches, and rollout gates remain proposals, not implemented or benchmarked features. Source inspection is not proof of runtime behavior. The checkout contains forward-looking .NET 12 cleanup documentation; its state is not a claim about a currently shipped SDK. During the original investigation, direct web fetching was unavailable and web search failed; primary documentation was successfully read through browser automation instead. The later projection-generation supplement also used successful direct documentation retrieval and pinned GitHub source inspection.

## 1. Reframe the problem before selecting a technology

### Removing binaries from NuGet is independently solvable

The current documentation already separates `Bind` and `Pack`. `Pack="false"` permits generating managed bindings without distributing that Java library in the package. [R1]

Thus, "binary-free NuGets" is a useful near-term delivery change, but it does not by itself solve binding maintenance, dependency compatibility, callback generation, or C# type ownership. The final Android application still needs the resolved Java implementation. This proposal removes its redistribution through binding NuGets, not its existence in the APK/AAB.

The existing Gradle resolver is an input to this work, not something to rebuild.

### A perfect idiomatic projection of every JAR is the wrong prerequisite

There are at least four distinct meanings of "correct bindings":

| Level | What is being promised? | What can establish it? |
|---|---|---|
| Structural access | Correct class, method/field descriptor, invocation kind, primitive ABI, and reference handling | Bytecode inspection, call-plan validation, runtime conformance |
| Language projection | Correct inheritance, generics, nullability, properties, value classes, overloads | Metadata, compiler checks, feature-specific rules |
| Domain semantics | Correct cancellation, threading, ownership, lifecycle, error behavior | Documentation, adapters, runtime tests, explicit contracts |
| Stable .NET product API | Existing C# source/binaries continue working across updates | Deliberate compatibility policy and versioning |

A descriptor is an authoritative account of the JVM calling shape. It is not a complete specification of any of the other three levels.

An annotation or metadata record cannot generally tell us the intended lifetime of a subscription, whether an operation must run on the UI thread, whether cancellation stops underlying work, or how an application should recover from a particular exception.

We therefore do not need 100% automatic *idiomatic* bindings to support build-time generation. We need a sharply defined supported subset, complete reporting of unsupported projections, and a safe alternative for the unsupported cases. Correctness is non-negotiable within that subset; completeness of the nice projection is a separate goal.

The alternative is not "emit questionable C# and hope." It is "emit a verified lower-level call, request an adapter, or fail with an actionable diagnostic."

### There are three different kinds of upstream movement

| Upstream change | Actual implication |
|---|---|
| Same JVM binary name and members, different Maven owner | Discovery and packaging changed; direct JNI invocation need not change |
| Same class name, changed members, hierarchy, or semantics | Calls and/or callback implementations may require regeneration or repair |
| Changed JVM package/class name | A genuine identity change; needs an explicit compatibility map or adapter if compatibility is desired |

A fourth, independent issue is moving the *managed* type to a different assembly. CLR type identity includes the assembly. It can break compiled consumers even when the Java class is unchanged.

This distinction substantially reduces the problem: Maven artifact ownership should be provenance, not the primary identity of a bound Java class.

## 2. What the current implementation tells us

### We already have runtime method resolution

Generated C# does not ordinarily contain a direct native function export for each Java method. It carries a JVM identity and calls Java.Interop. `JniPeerMembers.JniInstanceMethods` resolves and caches constructors and method IDs. [R2]

Consequently, a future design need not invent runtime JNI dispatch. It can reuse existing dispatch and peer infrastructure while changing how managed declarations, invocation plans, and metadata get produced.

The missing product layer is not simply "make JNI dynamic." It is safe discovery, projection, static diagnostics, callbacks, shrinker integration, and managed API identity.

### The existing generator mixes facts and projection policy

The generator loads `api.xml`, applies metadata transforms, imports types, runs Kotlin fixups and validation, fills properties, updates enums, fixes overrides, and generates code. Its validation loop explicitly accounts for cascading invalidation of interfaces and members. [R3]

This is understandable for a generator producing one coherent, inheritance-heavy C# API. It is also why "run the same generator as a Roslyn generator" does not remove its current failure modes.

A more conservative access surface can avoid many policy decisions: no inferred events, no guessed enum conversion, no forced property fusion, no requirement to model every Java generic relationship as CLR inheritance.

### Callbacks are a separate code-generation problem

The checkout's trimmable TypeMap pipeline scans managed metadata and emits Java callable wrappers plus managed registration/entrypoint code. The emitter has real `UnmanagedCallersOnly` and `RegisterNatives` machinery. [R4]

That is an important architectural asset. We can make outbound library access demand-driven without requiring runtime generation of Activity subclasses or new arbitrary managed callback signatures.

The forward-looking cleanup notes also retire the old expression-marshaling APIs rather than presenting them as the supported direction. [R5] A new design should not assume those old experiments are a production foundation.

### Kotlin support exists, but includes meaningful boundaries

The checked-out parser already reads `kotlin.Metadata`, hides synthetic constructor shapes, and has explicit primitive-backed value-class handling. It checks the actual erased JVM descriptor before applying a value-class projection. [R6]

The generator's Kotlin transformation also disables virtual overriding for certain mangled methods because it cannot generate the corresponding Java-source wrapper, and reports/deduplicates projected name collisions. Tests cover both collision cases and a real-classfile-to-C# value-class path. [R7]

These are not reasons to abandon generation. They are evidence that "callable JVM member," "nice C# declaration," and "implementable Java override" must be represented separately.

## 3. Approaches worth considering

The options below are not all mutually exclusive. Their combination matters more than selecting a single winner.

| Approach | Main benefit | Main unresolved cost | Judgment |
|---|---|---|---|
| A. Precompiled binary-free binding NuGets | Lowest migration risk | Managed release/version matrix remains | Start here for migration, not the end state |
| B. Cached graph-wide binding compiler | Moves routine generation off product release cadence | Generator limits and CLR ownership still need design | Best backbone |
| C. Explicit small C# contracts | Small generation surface, clear failures | More declaration/tooling work; less automatic full-library discovery | Best early new UX |
| D. Exact JVM access with optional C# `dynamic` sugar | Broad escape hatch | Weaker tooling, overload ambiguity, R8 roots | Supporting tier, not default |
| E. Kotlin-compiled semantic adapters | Delegates Kotlin semantics to Kotlin | Adds adapter compilation and an adapter compatibility surface | Essential for difficult Kotlin |
| F. Generated Java invocation bridges | JVM compiler/R8 sees real call edges | Extra generated Java, possible boxing overhead | Valuable backend to benchmark |
| G. Cached reference/implementation binding artifacts | Fast IDE and repeated builds | Contract/implementation consistency, ownership, provenance | Useful optimization after B |
| H. Curated stable .NET facades | Product-quality .NET semantics | Intentional ongoing semantic maintenance | Reserve for high-value areas |
| I. AI-authored, verified binding recipes | Amortizes human expertise | Verification and governance remain essential | Workflow multiplier across B-H |

### A. Keep compiled bindings; stop shipping upstream binaries

Use the existing separation of binding and packaging. Ship a managed assembly plus machine-readable requirements for the JVM classes/members it depends on. Consume upstream implementations only through the resolved Gradle graph.

Add a build-time compatibility audit. Report the exact missing type/member, expected descriptor and actual provider, rather than letting a mismatched package combination fail at runtime.

Do not make "NuGet version equals Maven version" the only compatibility rule. A binding can potentially work across multiple upstream versions if its required contract still exists.

But distinguish:

- The public managed contract required by compiled .NET consumers.
- The Java methods actually required by reachable runtime code and callbacks.
- The extra methods a full binding happened to generate.

A coarse validator that requires every historical wrapper member can reject harmless upgrades. A validator that only checks currently observed calls can miss public library contracts, reflective paths, or callbacks. Begin conservatively and make the validation mode explicit.

**Advantages:** Familiar consumption, fast application builds, existing public types, straightforward gradual adoption.

**Limits:** New C# API surface still requires a new managed artifact. Removing `.aar` files does not remove the need to review genuine API changes. Ranges need evidence; semantic compatibility cannot be established from signatures alone.

This is especially appropriate for already popular bindings with established managed public APIs.

### B. Compile bindings from the resolved graph, once per relevant input

Move binding generation into a deterministic build stage with reusable outputs, rather than requiring us to publish a new binding package for each upstream release.

The input is the authoritative resolved graph, including the selected Android variant and classpaths. The output is a binding model and generated managed artifacts.

The index is graph-aware, because bases, interfaces, descriptors, and annotations cross artifact boundaries. Generation should be partitioned, not necessarily one huge monolithic assembly.

Cache separate stages:

1. Per-artifact bytecode and Kotlin-metadata extraction.
2. Graph composition, duplicate-owner diagnostics, dependency resolution.
3. Projection under a selected recipe/policy set.
4. Emission and managed compilation.
5. Callback and shrinker artifacts, where their inputs differ.

A cache key must include more than a Maven coordinate: actual content hashes, reference API fingerprints, platform inputs, generator/runtime contract versions, projection options, recipes, and relevant compiler/tool versions. Include full artifact content where correctness depends on it. In particular, Kotlin inline bodies can affect generated adapters even if ordinary ABI signatures do not change.

ABI fingerprints are useful to avoid unnecessary downstream invalidation. They are not substitutes for content integrity or complete inputs.

This can start by hosting the current generator outside the IDE, preserving existing metadata transforms. Then incrementally introduce the more conservative projection described below.

**Advantages:** No synchronized per-artifact NuGet release bottleneck, exact agreement with selected dependencies, reuse across applications.

**Risks:** A cold cache still has real work. Existing generator fragility is not automatically fixed. Canonical managed type ownership must be settled before promising transparent use across separately built libraries.

### C. Generate the contract the application actually wants

Instead of generating an entire library, let the application or library author declare a small C# contract. The tool resolves it against the JVM index, diagnoses mismatches, and emits invocation code.

Conceptually:

```csharp
// Proposed syntax only; these attributes/APIs do not exist today.
[JavaClass ("com/example/catalog/Client")]
public partial class CatalogClient
{
    [JavaMethod ("lookup", "(Ljava/lang/String;)Ljava/lang/String;")]
    public partial string? Lookup (string key);
}
```

An IDE action could populate names and descriptors from the index. Most users should select a method rather than type a descriptor.

This reverses today's flow: instead of projecting every Java declaration and deleting the problematic ones, request the small managed contract and prove it can be implemented.

For referenced object types, offer tiers:

- Existing canonical Android/.NET Java peers when available.
- Generated full peers where inheritance and callbacks are required.
- Opaque or tagged Java references for auxiliary types that do not need a full managed class.

A tagged reference must not pretend to provide CLR inheritance it does not have. Conversions require checked Java assignability, and conversions into existing bindings must preserve the shared peer/reference ownership model. Public tag types also have managed assembly identity; they do not magically solve cross-library ownership.

**Advantages:** Much smaller surface, precise diagnostics, explicit ABI, natural incremental generation. The application does not need a nice projection of every transitive dependency.

**Tradeoff:** It is not a drop-in replacement for browsing an entire AndroidX assembly. Tooling must make discovery and declaration generation pleasant.

A useful initial compromise is **explicit type/package roots**, with full bindings for those types and conservative reference closure. Member-level inference can follow after the product is usable.

Do not depend on unresolved C# calls being repeatedly rebound until generation discovers a fixed point. Roslyn generators are additive and do not see each other's generated output. [E7] Explicit roots or declarations provide a stable seed; a single generator can compute a closure internally from the JVM model.

### D. Provide dynamic access, but make exactness available underneath it

The important API is not the `dynamic` keyword. It is an invocation plan with:

- Declaring class and class-loader context.
- Exact JVM member name and descriptor.
- Static, virtual, nonvirtual, constructor, or field operation.
- Checked argument/return conversion and ownership rules.

A convenience layer could allow:

```csharp
// Proposed API only.
dynamic client = JavaDynamic.Wrap (peer);
client.lookup ("item-1");
```

But it must have an explicit alternative:

```csharp
// Proposed API only.
var result = client.CallString (
    "lookup",
    "(Ljava/lang/String;)Ljava/lang/String;",
    "item-1");
```

Dynamic argument values alone cannot always select an overload. `null`, Java interface-typed arguments, primitive widening, boxing, varargs, and different intended declared types all require a policy or explicit selection. C# dynamic conventions are not Java overload resolution or Kotlin overload resolution.

A descriptor-driven engine can choose from a finite set of JNI invocation families and pass `jvalue` arguments using the `...MethodA` APIs. It does not inherently require generating arbitrary managed machine code at runtime. That is a plausible AOT-compatible implementation strategy, not proof that the current C# dynamic binder or an existing dynamic library is AOT-safe.

Treat C# `dynamic` as optional syntax sugar. The production contract should be representable without DLR-dependent behavior, runtime `Reflection.Emit`, or arbitrary new managed generic instantiations.

**Good uses:** Long-tail APIs, exploratory development, access to a new method without a full public binding, plugin boundaries with declared manifests.

**Bad default:** A whole application with runtime-only spelling and overload errors, unbounded reflection retention, no discoverability, and opaque failures.

A useful extension is a "freeze" action: take a successful exploratory call, resolve its descriptor, and generate an explicit checked contract. Runtime traces can suggest contracts and hot-path specialization; they must never be treated as proof that all runtime paths were covered.

### E. Use Kotlin itself to normalize Kotlin

This is the most important complement to a better C# generator.

Generate or author a small Kotlin adapter against the selected dependency graph. Let the Kotlin compiler perform Kotlin overload resolution, default arguments, value-class lowering, suspend calls, and calls requiring inlining. Expose a deliberately simple Java-friendly ABI to C#.

Examples of adapter boundaries:

- Ordinary methods and explicit DTOs instead of value-class-mangled signatures.
- Callback-based completion at the boundary, with coroutine execution and `Result` handling kept on the JVM.
- Subscription objects with explicit cancellation/disposal for `Flow`.
- Concrete entrypoints for particular reified type arguments.
- A Compose-backed `View` or controller, with state and events across the boundary.

This still uses compiled dependencies, not upstream source. Kotlin callers already consume compiled Kotlin libraries. However, the adapter compiler must support the dependency's metadata/features, and inline/reified cases may depend on available inline bytecode. "Use Kotlin" is not "all compiler versions work with all future libraries."

It also does not allow an app to add `@JvmExposeBoxed` to an already published dependency by changing its compiler flags. Those flags affect code being compiled. An external dependency without a boxed API generally needs an adapter or direct ABI handling.

**Advantages:** Stops reimplementing evolving Kotlin semantics in C#; keeps particularly difficult APIs behind a stable, narrow boundary.

**Tradeoffs:** Cold adapter compilation and compiler/plugin compatibility are real costs. Adapter semantics still need review. A Kotlin wrapper does not automatically make threading, lifetime, or cancellation correct.

### F. Generate JVM invocation bridges, not just C# JNI wrappers

Another useful backend is to generate a Java/Kotlin method that directly invokes the library, then bind only that bridge:

```text
C# typed contract
    -> narrow generated JVM bridge
        -> ordinary compiled call to upstream API
```

This moves more checking to `javac`/`kotlinc`, and exposes real call edges to R8. It can also combine a sequence of operations to reduce JNI crossings.

There are two distinct implementations:

- **Typed bridges:** Generated per method or operation, with direct calls and typed parameters.
- **Generic dispatch bridge:** A generated switch over known operation IDs, with a common object/argument envelope.

The generic variant can simplify the native ABI and callbacks, but boxing, arrays, dispatch, exception handling, and lost type information may cost more than direct JNI. Do not select it as the universal backend without measurement.

For hot or latency-sensitive calls, direct generated JNI or typed bridges are more plausible defaults. For coarse operations or rapid adapter creation, a generic envelope may be worthwhile.

This does not remove Java code from the app. It removes upstream binary redistribution and potentially a great deal of handwritten binding maintenance.

### G. Separate API discovery from implementation generation

A JVM API index can power completion and diagnostics without compiling every wrapper into the application on every edit.

Possible cache artifacts include:

- API indexes and documentation.
- Reviewed projection recipes.
- Generated source.
- Compiled binding assemblies.
- Compatible reference/implementation assembly pairs.

A reference assembly by itself is not a runtime implementation. It also cannot expose an enormous API while we silently ship an incompatible implementation. Assembly identity, contract completeness, runtime resolution, and trimming all remain obligations.

Therefore, an initial practical design is **cached ordinary binding DLLs** rather than an elaborate late-bound reference-assembly replacement system.

A later system could give the IDE a stable reference assembly and materialize an implementation with the same contract. This is worth exploring only after evidence shows emission/compilation is the bottleneck and after public type ownership is solved.

A remote prewarming service can make common public graphs fast, but should be optional. Local generation, offline reproducibility, signed/provenanced outputs, and exact cache keys are required. No private customer dependency or API model should be uploaded by default.

### H. Curate stable .NET facades where they earn their cost

For high-value scenarios, a stable managed facade can be a better product than a full mechanical binding.

For example, expose a managed authentication flow, image-loading operation, or database subscription rather than a mirror of every upstream implementation type. Generate a small JVM implementation against the selected version behind it.

The facade owns its cancellation, threading, errors, lifecycle, and version compatibility. It may keep a small stable surface while upstream packages reorganize internal classes.

This is real maintenance, not a way to make maintenance vanish. It should be reserved for areas where the customer benefit exceeds that cost.

Do not assume C# and Kotlin domain types serialize losslessly, or that handles can be replaced by DTOs for APIs involving Views, Contexts, platform identity, or subclassing.

### I. Make AI create verified binding knowledge, not runtime behavior

AI changes the economics of discovering and repairing exceptional cases. It does not make structural compatibility or compiler output automatically trustworthy.

The best unit of AI output is a **reviewable recipe plus a regression example**, not thousands of untracked lines generated afresh for every customer build.

A disciplined workflow:

1. Compare old/new JVM APIs and identify affected contracts.
2. Classify the problem: discovery, invalid projection, Kotlin lowering, callback shape, or genuine behavioral change.
3. Propose a narrowly scoped transform, explicit contract, or Kotlin adapter.
4. Check every declared JVM identity against the selected artifacts.
5. Compile both sides and inspect the resulting signatures.
6. Exercise callback, lifecycle, exception, shrinker, and runtime behavior as appropriate.
7. Review and publish the recipe with provenance and applicability conditions.

Recipes should contain structural preconditions, expected match counts, postconditions, tests, and an explicit fallback/error policy. A recipe that matches zero or unexpectedly many nodes must not silently succeed.

Do not infer a JVM package rename from similarly named types. Any compatibility mapping must be explicit and checked for signatures, assignability, constructors, callbacks, and relevant semantics.

No LLM/network dependency belongs in the normal deterministic build. AI assistance can be opt-in for authoring, IDE exploration, or maintaining the shared recipe corpus. Ordinary builds consume accepted artifacts.

## 4. The callback problem, specifically

### Interfaces have a useful dynamic escape hatch

Android's `java.lang.reflect.Proxy` creates implementations of interfaces and routes calls to an `InvocationHandler`. The handler receives a reflected method and argument array. It does not subclass arbitrary classes. [E4]

This suggests a reusable platform component:

```text
Java interface proxy
    -> one packaged InvocationHandler
        -> one fixed JNI callback entrypoint
            -> validated dispatch table / precompiled C# delegate
```

A fixed JNI entrypoint can avoid arbitrary per-interface managed code generation. Its storage and callback roots still need correct lifetime management.

Unity's `AndroidJavaProxy` is a concrete precedent for the user experience: implement a Java interface in C#, with calls forwarded from Java. Its documentation also explicitly warns about calling threads. [E5] This demonstrates feasibility of the category, not that Unity's implementation can be copied into Java.Interop or that it meets our performance/AOT requirements.

Requirements for our version include:

- Correct primitive boxing/unboxing, arrays, reference returns, and exceptions.
- Overloads and inherited interface methods identified by full descriptors, not just names.
- Explicit behavior for `equals`, `hashCode`, and `toString`.
- A policy for interface default methods and duplicate interface signatures.
- Correct class loaders and interface accessibility.
- Strong/weak ownership, callback roots, disposal, and callbacks racing with disposal.
- Thread attachment and no implicit promise that callbacks occur on the UI thread.
- NativeAOT-safe dispatch and managed reachability.
- Exact shrinker inputs.

The default should probably remain a statically generated implementation when the interface is known. A proxy is a useful convenience and fallback, especially for infrequent callbacks.

### Abstract classes and Android components still need Java shapes

An API requiring an abstract class, a custom `View`, an `Activity`, or another concrete superclass cannot be satisfied by `Proxy`.

Generate the needed Java/Kotlin subclass at build time:

```text
declared managed override contract
    -> generated Java/Kotlin subclass
        -> existing TypeMap registration and managed dispatch
```

Only the selected subclass shapes and required overrides need to be generated. This is a much smaller problem than dynamically mirroring a whole dependency graph.

The generation must account for constructors, abstract members, visibility, Java finality, Java/JVM naming restrictions, covariant returns, default methods, `super` dispatch, lifecycle, peer activation, and exception transitions.

Never turn a base/super call into another virtual invocation that recursively enters the same managed override.

An advanced alternative is direct bytecode/DEX emission for methods Java source cannot express. It may address particular mangled-name limitations, but it adds verifier, toolchain, and compatibility responsibilities. It should be an isolated experiment, not the first architecture.

**Conclusion:** Dynamic outbound calls do not require dynamic arbitrary subclass generation. Use a hybrid callback model.

## 5. Kotlin changes the architecture, not merely the parser

### Keep JVM facts and Kotlin semantics as separate models

The JVM model needs binary names, flags, descriptors, hierarchy, annotations, generic signatures, and actual providers.

The Kotlin model adds source names, visibility, properties, extension receivers, suspend information, value-class identity, nullability, and other metadata.

The official `kotlin-metadata-jvm` library reads Kotlin metadata, supports bytecode-based extraction, and distinguishes lenient reading from strict compatible reading/writing. [E1] That makes it a serious candidate for a small JVM-side indexing tool alongside the existing Gradle environment.

Do not instantiate arbitrary library classes just to inspect them. The documented bytecode/ASM path is preferable for indexing; class loading can trigger initialization or require an Android runtime.

Lenient reading of newer metadata is useful, but not a promise that an older tool understands every new language feature. Record producer versions and feature support. Diagnose unsupported projection features rather than interpreting unknown metadata as familiar semantics.

A JVM-side tool adds a host-JVM startup cost; measure and cache it. It may still reduce long-term maintenance compared with indefinitely duplicating Kotlin's metadata implementation in managed code.

### Defaults are executable behavior

Kotlin default arguments do not generally appear as a corresponding Java overload set unless appropriate overloads were generated. Official interop documentation describes `@JvmOverloads` and the full-signature Java view. [E2]

Do not copy guessed default values into C# optional parameters. Defaults can be nonconstant expressions and can change. Let compiled Kotlin call sites use the upstream defaults where necessary.

### Suspend is a protocol, not an ordinary callback suffix

The Kotlin specification describes an extra `Continuation<T>` parameter, an `Any?` return, immediate completion, and the `COROUTINE_SUSPENDED` marker. [E3]

Simply supplying a continuation and always waiting for a later callback is incorrect: an operation may return its result synchronously.

A complete Task adapter also needs correct failure decoding, at-most-once completion, cancellation races, coroutine context, dispatcher choice, JVM/managed reference lifetime, and behavior when the managed consumer is disposed.

A Kotlin-side coroutine adapter is often the simpler place to own this protocol. A managed implementation is possible, but must be a deliberately tested component rather than a generic "suspend means Task" naming rule.

### Flow needs a subscription contract

`Flow<T>` to `IAsyncEnumerable<T>` is attractive, but not purely a type conversion. Cold/hot behavior, buffering, backpressure, cancellation, dispatcher/lifecycle ownership, and error propagation need a defined contract.

Build a shared, reviewed bridge, with explicit buffering/disposal semantics, instead of regenerating a new ad hoc implementation for each library.

### Value classes require position-specific lowering

A value class can be represented differently in ordinary, nullable, generic, and boxed positions. The local code already protects against projecting a primitive representation into a reference-shaped descriptor. [R6]

Official documentation now describes experimental `@JvmExposeBoxed`/compiler support, while also documenting its limitations. [E2] It is useful when the upstream publisher supplies it, not something we can assume for every published binary.

Preserve the exact JVM ABI beneath a friendly wrapper. For problematic inheritance or overloads, use a Kotlin adapter.

### Inline and reified APIs may require a real Kotlin call site

The official documentation describes reification through inline functions and call-site substitution. [E8]

Passing a C# generic argument or Java `Class` object is not automatically equivalent to an arbitrary Kotlin reified call. Generate a concrete Kotlin entrypoint for a known type when that is the supported route; do not claim a universal runtime generic bridge.

### Compose is a separate semantic boundary

AndroidX's own design document explains that the compiler plugin rewrites composable calls and injects `Composer` parameters and change-tracking machinery. It warns that some internal details evolve. [E6]

It would be a mistake to treat a bytecode-visible composable function as an ordinary ergonomic JNI API and call the problem solved.

Practical choices:

- Compile Kotlin composable content and expose a `View`, controller, or bounded state/event interface.
- Generate a declarative description consumed by a Kotlin Compose host.
- Treat direct C# authoring of composable semantics as a separate, much larger compiler/runtime project.

The first is the best near-term path. The second may be useful for constrained UI islands. The third should not be hidden inside a generic binding-generator commitment.

The same principle applies to annotation/codegen ecosystems such as serialization and dependency-injection tooling: access the supported generated/runtime API, or compile a semantic adapter. Do not equate every synthetic classfile member with a stable supported public API.

## 6. The hardest issue may be managed type ownership

Consider two independently published .NET libraries:

```text
Library A -> Java type X in binding assembly A.Bindings
Library B -> Java type X in binding assembly B.Bindings
```

Even if both wrappers point to the same Java binary name, they are not the same CLR type.

Moving everything to per-app source generation does not fix that. Nor can a consuming app add methods to a non-partial type in an already compiled external assembly through ordinary source generation.

There are three workable product boundaries:

1. **Canonical public contracts.** Shared public Java-facing types have stable managed ownership and assembly identity. Generation supplies compatible implementations, not arbitrary new owners.
2. **Private implementation bindings.** Each reusable library hides generated Java wrappers behind its own public .NET facade or an agreed opaque-reference ABI.
3. **Source-distributed managed libraries.** Consumers compile the source in a coordinated graph. This is possible, but changes distribution, build, debugging, and compatibility expectations; it is not transparent replacement for ordinary NuGet libraries.

For initial adoption, prioritize private app bindings and private library implementation bindings. Keep canonical existing AndroidX types for libraries that publicly expose them.

Within an app, maintain a binding ownership manifest. Exactly one owner is chosen for every public mapped Java type, including framework types already provided by the SDK. Independent generators must not each create their own `Context`, `View`, or common dependency wrapper.

Stable namespace policies can survive an artifact move. Stable assembly partitions require an explicit policy too. A single central owner is simpler but can become a large recompilation/cache unit.

Type forwarding can help some planned assembly moves when old forwarder assemblies can be supplied. It cannot retroactively repair all arbitrary changes to inheritance, generic constraints, members, or public surface.

This problem must be included in the first cross-library prototype, not postponed until after an impressive single-app demo.

## 7. A reference architecture

```text
Existing resolved Gradle graph and selected variant
                         |
          Content-verified JVM/Kotlin API index
                         |
        Binding ownership + requested contract roots
                         |
       Exact model + versioned projection/adapter recipes
                         |
         +---------------+-------------------+
         |               |                   |
  Cached C# peers   Small C# contracts   Kotlin/Java adapters
         |               |                   |
         +---------------+-------------------+
                         |
       Existing Java.Interop peer/reference infrastructure
                         |
          Static JCWs / optional interface proxies
                         |
      Exact linkage requirements + shrinker/remapping inputs
                         |
                  Packaged application
```

The durable binding recipe/contract format is arguably a more valuable product asset than the choice of `.cs` versus `.dll` emission.

Its responsibilities:

- Preserve unmodified JVM identities separately from managed names.
- Record provenance and actual provider without making Maven ownership part of type identity.
- Describe supported and unsupported projection capabilities.
- Record exact requested methods and invocation kinds.
- Define stable naming and collision resolution.
- Express semantic adapters and their version/toolchain requirements.
- Carry roots for callbacks, managed trimming, Java shrinking, and name remapping.
- Preserve compatibility imports from existing XML transforms where feasible.
- Provide understandable diagnostics and explainable generated artifacts.

Begin with a small schema. Do not design a universal language before demonstrating a few difficult real libraries.

### Build versus IDE responsibilities

Resolve dependencies and run expensive extraction outside the C# source generator. The build stage produces stable local input artifacts.

The IDE reads those artifacts for completion, contract generation, provenance, and diagnostics. It should not spawn Gradle, download Maven dependencies, or invoke an LLM on each edit.

A Roslyn incremental generator can be a thin emitter for explicit C# contracts and precomputed metadata. It is one frontend, not the entire binding architecture.

A separately cached binding compilation may be faster for large reused APIs. Measure parsing, projection, emission, C# compilation, and design-time reexecution separately rather than labeling all time "source generation."

### Two independent linkers must understand the boundary

ILLink/NativeAOT need the managed peers, dispatch, callbacks, and any required generic instantiations.

R8 needs the JVM classes/members and callback/adapter paths. Android's documentation specifically explains why JNI and reflection use can disappear without correct retention. [E9]

The invocation/contract model should be the source of precise Java retention or supported name-remapping metadata. Reuse the repository's existing mechanisms rather than introducing an incompatible second shrinker pipeline.

Direct compiled JVM bridge calls are visible to R8, but the externally invoked bridge entrypoint still needs retention and appropriate naming/remapping treatment.

Unknown runtime strings cannot produce a complete precise static reachability graph. Require a declared dynamic-access surface, use explicitly broad retention with a size warning, or reject unsupported unbounded use in strict mode. Do not quietly keep all of AndroidX.

### AOT is a design input

Microsoft documents that NativeAOT cannot rely on runtime managed code generation and has restricted dynamic loading and generic behavior. [E10]

Prefer precompiled dispatch, fixed native entrypoints, build-generated callbacks, and exact roots. Some runtime interpretation can be AOT-compatible, but arbitrary CLR type emission is not.

The generic NativeAOT documentation is not the authority for the status of the Android workload's Java interop implementation; the local Android-specific pipeline is separate evidence. We use the general documentation for runtime constraints, not to infer that Android interop is absent.

## 8. Reliability rules that no approach avoids

1. Validate real descriptors and invocation kinds, not just C# compilation.
2. Resolve against the selected graph and variant, not a collection of uncoordinated POM assumptions.
3. Treat duplicate class ownership as a diagnostic; do not silently select the first file.
4. Keep signature/classloader context in lookup caches where relevant.
5. Handle local/global references, peer identity, callback roots, disposal races, and cross-runtime GC explicitly.
6. Surface unsupported members and recipe failures without silent omission.
7. Separate ordinary runtime exceptions from managed callback exception transitions.
8. Preserve `super`/nonvirtual behavior for subclassing.
9. Integrate both managed and JVM reachability/shrinking.
10. Do not infer semantic compatibility from identical names or descriptors.
11. Respect Android API-level availability, access restrictions, lifecycle, and thread requirements; classfile presence alone does not guarantee legal runtime access.
12. Make generation deterministic and offline-capable; cache failures and invalidation must never substitute stale successful artifacts.

This list is why a universal object wrapper is a useful tier but not a complete architecture.

## 9. Analogues and what they actually demonstrate

**Unity:** `AndroidJavaProxy` documents interface callbacks routed to C#, with Java-thread and JNI attachment caveats. [E5] This supports the feasibility of a generic interface callback UX, not arbitrary subclassing.

**Dart JNIgen:** The documented pipeline scans JARs/Java source, creates an API description, and generates bindings using shared JNI infrastructure. It can draw classes from Gradle's compile classpath, separates binding generation from getting classes into the app, and calls out R8 retention problems. [E11] This supports the graph/index/generator separation. It does not prove perfect Kotlin projection or predict .NET performance.

**C# source generators:** Roslyn explicitly supports generating types from additional files and user declarations, but generators are additive and unordered. [E7] This supports small declarative contracts; it argues against magical compiler-rewriting or generator-to-generator fixed-point schemes.

**Kotlin metadata tooling:** The official library exists specifically for structured Kotlin declaration inspection. [E1] This suggests using Kotlin's maintained format support rather than assuming bytecode descriptors alone capture source semantics.

These are architectural precedents, not a claim that one existing product already solves this exact problem.

## 10. What to prototype, and what would change the recommendation

No binding-generation or interop-performance benchmarks have been measured for these proposals. Experiments 1-6 below remain proposed decision gates; the subsequent shared-peer follow-up is separately identified as implemented and tested.

### Experiment 1: Version-independent compiled bindings

Take an established binding. Remove upstream Java binaries from its package, retain the managed assembly, and audit it against multiple resolved upstream graphs.

Include a synthetic fixture where a class moves between artifacts without changing binary identity, and a real upstream movement only after identifying and verifying a concrete example. Separately test a true package rename.

**Pass:** Artifact-only movement requires no managed regeneration when the required ABI is unchanged; genuine missing members fail before packaging with accurate diagnostics.

**Learn:** How much existing maintenance is packaging/release coupling rather than actual projection change?

### Experiment 2: Exact small-contract access

Select a representative Java-first API, a Kotlin-heavy API, and an API returning complex auxiliary objects. Generate only explicit contracts and use opaque references for nonessential types.

Compare against full current bindings.

**Pass:** Every requested operation has a checked invocation plan; unsupported shapes produce explicit diagnostics; auxiliary projection problems do not invalidate unrelated requested operations.

**Learn:** How much of the failure surface disappears when we stop demanding a full idiomatic inheritance model?

### Experiment 3: Both callback directions

Cover a listener interface, an inherited/default interface shape, an abstract callback class, and a custom View/component subclass.

Compare static generated implementations with an interface proxy for the interface cases. Include synchronous return values, exceptions, Java-thread callbacks, peer identity, GC stress, disposal races, and base calls.

**Pass:** Correct behavior on the intended runtimes/ABIs with trimming and Java shrinking; no arbitrary managed runtime code generation; explicit lifetime behavior.

**Learn:** Whether one generic handler is worth its dispatch/boxing/lifetime complexity.

### Experiment 4: Kotlin semantic adapters

Use real compiled fixtures for synchronous and asynchronous suspend completion, failure, cancellation, primitive- and reference-backed value classes, boxed/nullable/generic positions, defaults, inline/reified entrypoints, Flow, and a Compose UI island.

Test supported producer/consumer compiler combinations and diagnostic behavior for unsupported metadata versions.

**Pass:** JVM-side wrappers compile against the selected binary graph, produce a stable simple ABI, and preserve the declared runtime semantics.

**Learn:** Which language features belong in shared deterministic bridges rather than a general C# projection.

### Experiment 5: The cross-NuGet identity test

Build two .NET libraries separately that share Java-facing types, and consume them in one application. Include a library publicly exposing an existing canonical binding type and another with private generated bindings.

**Pass:** No duplicate public CLR identities, working casts/peer identity, predictable assembly ownership, and compatibility diagnostics for conflicting requests.

**Learn:** Whether the proposed distribution model is viable outside a single app.

### Experiment 6: Build economics and update resilience

Measure small, medium, and large resolved graphs, including at least one Compose-heavy graph. Compare:

- Prebuilt managed bindings.
- Full local generation using current tooling.
- Cached graph generation.
- Explicit-contract generation.
- JVM bridge generation.

Record cold/warm extraction, projection, emission, managed/JVM compilation, no-op build time, IDE edit latency and memory, cache hit rate, callback throughput/allocations, startup, package size, and engineer time per upstream update.

Also test: a C#-only edit, one adapter edit, one dependency bump, a recipe update, different classpath variants, clean checkout, offline restore with a populated cache, and corrupt/stale cache rejection.

Candidate product goals might include no generator execution on ordinary no-op builds, no graph rescan for unrelated C# body edits, deterministic output for identical locked inputs, and a warm generation overhead budget of at most 5% of the comparable baseline build. These are targets to negotiate and measure, not promises.

For measured invocation overhead, set per-category budgets: a generic proxy may be acceptable for a UI completion event but not for a per-frame/high-volume callback. Reject a universal backend if its worst important category is unacceptable, even if average microbenchmarks look good.

### Corpus design matters more than a polished demo

Use Java/Kotlin features and whole real applications, not just easy libraries. Include generic bridges, nested types, default methods, inaccessible bases, obfuscated/legal-but-unusual names, conflicting projection names, missing annotations, mixed compiler versions, abstract callbacks, component constructors, and aggressive shrinking.

Compilation is necessary but insufficient. Use golden outputs, descriptor assertions, actual device behavior, and error-path checks. Inspect produced managed metadata and JVM/DEX output where signatures or lowering are in question.

### Implemented follow-up: independent minimal views, one authoritative peer

**Result:** The basic identity/lifetime hypothesis works with the current runtime. We do not have to make two CLR classes equivalent, nor register two independently owning JNI peers, to let separately compiled libraries use the same Java instance.

This is a bounded, handwritten prototype of a generation shape, not a new general-purpose binding generator.

#### What was built

`tests/Mono.Android-Tests/SharedPeerViews/Reader/SharedPeerViews.Reader.csproj` and
`tests/Mono.Android-Tests/SharedPeerViews/Writer/SharedPeerViews.Writer.csproj` are separate
signed Android library assemblies. Neither references the other.

Both intentionally declare the full managed name `SharedPeerViews.Widget`, but expose different Java operations:

| Library | Projected operations |
|---|---|
| Reader | Read a value, round-trip a Java object argument/result, retrieve a callback |
| Writer | Change the value, attach/detach a callback, invoke the callback |

The shared `Widget.cs` source is compiled independently into both assemblies. It provides
checked construction, a borrowed `Java.Lang.Object` carrier, and view disposal. The views
are ordinary managed classes: no `IJavaPeerable`, no `[Register]`, and no additional peer
registration. Holding a view strongly roots its canonical peer; holding two views does not
give the Java instance two callback authorities.

The app supplies `SharedPeerFixture.java` with `Bind="false"`; no complete binding is
generated for that fixture. Plain Java instances can use an ordinary `Java.Lang.Object`
as their canonical carrier. For managed subclassing, one small canonical binding contract
declares the Java constructor and virtual `getValue()` callback. Only that authoritative
owner participates in callable-wrapper generation; neither view does.

An illustrative crossing, using the actual prototype shape:

```csharp
using var reader = new Reader (owner);
using var writer = new Writer (reader.Peer);
writer.SetValue (42);
var value = reader.GetValue ();
```

`Reader` and `Writer` are aliases in this test app, not equivalent types. The deliberately
duplicated public name requires `extern alias` in the harness. A product should normally
keep local projections internal or give public views distinct names, and use an explicit
carrier/adaptation boundary rather than exposing this ambiguity to customers.

#### What the 11 cases establish

The device tests live in
`tests/Mono.Android-Tests/Mono.Android-Tests/Java.Interop/SharedPeerViewTests.cs`,
category `SharedPeerViews`.

| Area | Observed assertion |
|---|---|
| CLR ownership and minimality | Same full view name, different assemblies, no CLR assignability; Reader has no setter and Writer has no getter |
| Shared state | Mutation through Writer is visible through Reader on the same Java object |
| Arguments and results | Another object's carrier crosses the boundary, returns the original managed peer, and passes JNI `IsSameObject`; null round-trips correctly |
| Invalid conversions | Wrong Java object types are rejected before invocation, including incompatible callback arguments; Java exceptions propagate |
| Virtual Java calls | A Java subclass's override executes rather than the base implementation |
| Managed subclass of the viewed object | Both views retain the original managed owner and its state; outbound virtual dispatch reaches its override, whose explicit Java base call does not recurse |
| Interface callbacks | Java invokes the original managed listener and its managed override; returned callbacks resolve to the original owner |
| Repeated conversion | 128 additional reader/writer pairs neither replace the fixture's registered peer nor the callback's registered peer |
| View disposal | Disposing a view is idempotent and leaves the peer and other views usable |
| Explicit owner disposal | All remaining views detect the invalid owner and throw instead of using a deleted JNI reference |
| GC and eventual release | Views retain both plain and managed-subclass peers; the last view's release permits managed and Java collection; a Java-held callback survives GC and becomes collectible after detachment |

Each of the three GC scenarios repeats ten times per run. Eventual release is checked using
both a resurrection-tracking managed weak reference and a JNI weak global reference; the
test does not infer Java identity from numeric handles or claim to measure control-block
allocation counts.

The generated Java callable wrapper was also inspected. The managed-subclass fixture has
one `IGCUserPeer` subclass, one `getValue()` native trampoline, and the normal reference
tracking hooks. The local view classes do not become Java subclasses.

#### Actual validation

The complete local SDK was prepared and built first, including its extra configured API
levels. The test apps then used that locally built SDK. The device was an Android API 35
`arm64-v8a` emulator. Device-side logging identified the app runtime as
`.NET 11.0.0-preview.7.26381.103` and its value manager as
`Microsoft.Android.Runtime.TrimmableTypeMapValueManager`.

The provisioning/build SDK was `12.0.100-alpha.1.26523.103`. The test driver's
`net12.0|arm64` label describes the host driver, not the Android application's runtime.

| Configuration | Final result | Build outcome |
|---|---|---|
| Debug CoreCLR | 11 passed, 0 failed, 0 skipped | Succeeded, 0 warnings, 0 errors |
| Release CoreCLR, `TrimMode=full`, `AndroidLinkTool=r8` | 11 passed, 0 failed, 0 skipped | Succeeded, 38 suite analysis/API warnings, 0 errors |
| Release NativeAOT, R8 | 11 passed, 0 failed, 0 skipped | Succeeded, 11 suite/runtime/NUnit analysis warnings, 0 errors |

The NativeAOT APK contained `libMono.Android.NET-Tests.so` and no `libcoreclr.so`; the
subsequent CoreCLR APK contained `libcoreclr.so`. The two modes were not inferred solely
from an MSBuild property.

The first NativeAOT run exposed a test-harness issue: NUnit's general
`Assert.AreNotEqual(Assembly, Assembly)` attempted unsupported reflection over
`IEquatable` interface maps. Replacing it with `Assert.AreNotSame` expresses the intended
assembly-object identity assertion without that reflection. No JNI or production-runtime
fix was required, and no failing case was skipped.

Commands used, from the repository root with these per-session environment values:

```sh
export NUGET_PACKAGES=${EXPERIMENT_ARTIFACTS}/nuget-packages
export AndroidToolchainDirectory="$PWD/bin/toolchain"
export ANDROID_SERIAL=emulator-5554

make prepare
make leeroy
```

Both succeeded. `make leeroy` was used instead of `make all` because it explicitly builds
the additional stable API levels before configuring the local workload.

```sh
./dotnet-local.sh build -t:Install -c Debug tests/Mono.Android-Tests/Mono.Android-Tests/Mono.Android.NET-Tests.csproj -p:IncludeCategories=SharedPeerViews -p:RuntimeIdentifier=android-arm64 -v:minimal
(
  cd tests/Mono.Android-Tests/Mono.Android-Tests
  ../../../dotnet-local.sh test Mono.Android.NET-Tests.csproj --no-build -c Debug -p:IncludeCategories=SharedPeerViews -p:RuntimeIdentifier=android-arm64 --report-trx --results-directory ../../../bin/TestDebug/TestResults
)

./dotnet-local.sh build -t:Install -c Release tests/Mono.Android-Tests/Mono.Android-Tests/Mono.Android.NET-Tests.csproj -p:IncludeCategories=SharedPeerViews -p:RuntimeIdentifier=android-arm64 -p:PublishAot=false -v:minimal
(
  cd tests/Mono.Android-Tests/Mono.Android-Tests
  ../../../dotnet-local.sh test Mono.Android.NET-Tests.csproj --no-build -c Release -p:IncludeCategories=SharedPeerViews -p:RuntimeIdentifier=android-arm64 -p:PublishAot=false --report-trx --results-directory ../../../bin/TestRelease/TestResults
)

./dotnet-local.sh build -t:Install -c Release tests/Mono.Android-Tests/Mono.Android-Tests/Mono.Android.NET-Tests.csproj -p:IncludeCategories=SharedPeerViews -p:RuntimeIdentifier=android-arm64 -p:PublishAot=true -v:minimal
(
  cd tests/Mono.Android-Tests/Mono.Android-Tests
  ../../../dotnet-local.sh test Mono.Android.NET-Tests.csproj --no-build -c Release -p:IncludeCategories=SharedPeerViews -p:RuntimeIdentifier=android-arm64 -p:PublishAot=true --report-trx --results-directory ../../../bin/TestRelease/TestResults-NativeAOT
)
```

Each build succeeded and each final test command passed all 11 cases. The final execution
order was NativeAOT, Debug CoreCLR, then Release CoreCLR. TRX files are in the three
results directories above; build/test logs and device runtime evidence are retained in
the session artifact folder. The emulator was shut down after validation.

#### What this changes, and what it does not

**Recommendation strengthened:** Treat assembly-local, minimal views over a common peer
carrier as a first-class architecture, alongside the canonical late-linked-binding-assembly
alternative. Outbound projections need not compete over which NuGet owns the one managed
wrapper class for every Java class. A callback-bearing object still needs one authoritative
managed owner, but that owner's static contract can be much smaller than the complete Java API.

This splits three concerns that traditional wrappers conflate: the Java object's identity
and lifetime, each assembly's preferred access surface, and the managed subclass that
implements callbacks. The CLR types remain distinct; what is shared is the actual Java object.

A source generator could emit these small facades and invocation plans from explicit
contracts and an indexed API model, without requiring full projections of every dependency.
This experiment does not implement that generator or prove Kotlin/API projection completeness.

The remaining limits are material:

- No implicit CLR type equivalence, cross-view inheritance, or automatic conversion of
  `List<A.Widget>` to `List<B.Widget>`.
- No proof for arbitrary Android components, abstract callback hierarchies, Kotlin adapters,
  events/multiple subscriptions, callback exceptions, or Java-thread callback stress.
- No custom-class-loader support or concurrency/owner-disposal race guarantees were tested.
  Holding a view roots the owner but does not prevent an explicit concurrent `Dispose()`.
- The fixture uses explicit R8 keep rules. Automatic minimal keep-rule generation and
  obfuscation/remapping of raw JNI names were not tested.
- The test assembly is deliberately rooted by the existing suite; this is not a proof of
  minimal callback rooting for an otherwise unrooted application.
- No NuGet publication/restore matrix, real AndroidX/Maven relocation corpus, build-time
  savings, allocation/throughput benchmark, other ABI, or other Android version was tested.
- `Peer` is a borrowed `IDisposable` object in this prototype. A public product needs a
  deliberate non-owning carrier/lease API so consumers do not accidentally dispose
  another library's authoritative owner.

## 11. Migration strategy

### First: decouple without breaking public APIs

Retain compiled canonical bindings, omit upstream Java binaries where the existing resolver supplies them, and add compatibility auditing. Preserve existing metadata transforms and manual additions.

This delivers value before replacing the generator.

### Next: make bindings an application/library-private build artifact

Introduce the JVM index, explicit roots/contracts, local caching, deterministic generation, and shared ownership manifests.

Initially target app-private code and library-private implementations. Preserve established managed public API owners where needed.

### Then: add semantic adapters and long-tail access

Provide shared Kotlin bridges and exact invocation. Offer interface proxies where useful, and code actions that turn discovery into explicit generated contracts.

Keep unsupported features explicit. Avoid creating a new backend that silently behaves differently from existing peers.

### Finally: decide what deserves a stable facade

Invest in high-value stable managed semantics based on usage and maintenance data. Do not curate the entire Maven universe by hand.

A permanent mixed model is acceptable: old public binding assemblies, new private generated contracts, Kotlin adapters, and occasional exact dynamic access can coexist if they share type ownership, peer infrastructure, and a common reachability model.

## 12. Ideas I would not lead with

**Generate every transitive dependency inside the IDE.** Repeats work, amplifies the known projection failure surface, and ties editing latency to a large graph.

**Require perfect idiomatic binding generation before shipping anything.** Conflates structural access with semantic product design and prevents useful bounded progress.

**Make everything C# dynamic.** Does not solve overload ambiguity, subclassing, public managed APIs, shrinker analysis, or lifetime.

**Generate arbitrary managed types or Java/DEX subclasses at runtime.** Adds deployment, verifier, AOT, startup, debugging, and optimization problems when the required shapes can usually be known at build time.

**Use AI on every build or at runtime.** Adds nondeterminism, privacy/connectivity risk, and an unacceptable correctness boundary.

**Compile arbitrary JVM libraries as Kotlin/Native instead.** Changes execution/runtime assumptions; it is not a general way to consume existing Android JVM AARs and AndroidX APIs.

**Put all interop behind JSON/IPC.** Useful for coarse isolated services, not a general replacement for Views, object identity, subclassing, synchronous callbacks, or fine-grained platform APIs.

**Assume renaming/remapping repairs any upgrade.** It cannot restore deleted semantics or arbitrary ABI, hierarchy, and lifecycle changes.

## Final recommendation

Fund three bounded tracks, with one shared model:

1. **Migration:** binary-free canonical managed binding packages plus exact compatibility auditing.
2. **Backbone:** graph-aware indexing, deterministic cached generation, and small explicit C# contracts.
3. **Semantic boundary:** Kotlin-compiled adapters and a hybrid callback model, built on existing peer/TypeMap machinery.

AI should accelerate recipe creation, compatibility analysis, and regression-corpus growth across those tracks.

If only one new prototype can be funded, build **small explicit C# contracts backed by the resolved JVM graph, with a Kotlin-adapter escape hatch and both interface and abstract-class callbacks**. Include the two-independent-NuGet identity scenario from the beginning.

That prototype tests the biggest change in the problem definition: we do not need to bind the whole world beautifully to make the part the developer actually needs correct, discoverable, and maintainable.

## Sources

### Local repository evidence inspected

These references identify the working checkout inspected for this analysis, not a verified published SDK release or a pinned remote commit.

| ID | Source | Relevant evidence |
|---|---|---|
| R1 | `Documentation/docs-mobile/binding-libs/advanced-concepts/distributing.md:26-55` | Independent Bind/Pack behavior, including `Pack="false"` |
| R2 | `external/Java.Interop/src/Java.Interop/Java.Interop/JniPeerMembers.JniInstanceMethods.cs:46-113,171-191` | Cached JVM lookup; constructor and nonvirtual invocation |
| R3 | `external/Java.Interop/tools/generator/CodeGenerator.cs:160-235,282-329` | Transform/projection pipeline and cascading validation |
| R4 | `Documentation/guides/internals/JavaJNI_Interop.md`; `src/Microsoft.Android.Sdk.TrimmableTypeMap/Generator/JcwJavaSourceGenerator.cs:1-90`; `src/Microsoft.Android.Sdk.TrimmableTypeMap/Generator/TypeMapAssemblyEmitter.cs:43-65` | Current checkout callback generation, TypeMap, static entrypoints |
| R5 | `Documentation/release-notes/expression-marshalling-retirement.md` | Forward-looking retirement of old expression-marshaling APIs |
| R6 | `external/Java.Interop/src/Xamarin.Android.Tools.Bytecode/Kotlin/KotlinMetadata.cs:1-112`; `external/Java.Interop/src/Xamarin.Android.Tools.Bytecode/Kotlin/KotlinFixups.cs:15-76,81-173,263-278` | Kotlin metadata parsing and position-specific value-class constraints |
| R7 | `external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.Transformation/KotlinFixups.cs:17-85`; `external/Java.Interop/tests/generator-Tests/Unit-Tests/KotlinFixupsTests.cs`; `external/Java.Interop/tests/generator-Tests/Unit-Tests/KotlinInlineClassEndToEndTests.cs` | Callback/mangled-name limits and existing regression coverage |
| R8 | `src/Xamarin.Android.Build.Tasks/MSBuild/Xamarin/Android/Xamarin.Android.Bindings.Core.targets:75-158` | Existing incremental binding stage and inputs |

### Primary external sources read

- **E1:** Kotlin, [Kotlin Metadata JVM library](https://kotlinlang.org/docs/metadata-jvm.html). Structured metadata, lenient/strict modes, bytecode extraction, compiler/version relationship.
- **E2:** Kotlin, [Calling Kotlin from Java](https://kotlinlang.org/docs/java-to-kotlin-interop.html). Defaults, overloads, visibility, variance, JVM naming, default-method modes, experimental boxed value-class exposure.
- **E3:** Kotlin language specification, [Asynchronous programming with coroutines](https://kotlinlang.org/spec/asynchronous-programming-with-coroutines.html). Continuations, CPS, immediate return versus suspension.
- **E4:** Android API reference, [java.lang.reflect.Proxy](https://developer.android.com/reference/java/lang/reflect/Proxy). Interface-only dynamic proxies, dispatch, class-loader and method constraints.
- **E5:** Unity, [AndroidJavaProxy](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/AndroidJavaProxy.html). Interface implementation from C#, callback-thread and JNI attachment constraints.
- **E6:** AndroidX runtime design, [How Composition Works](https://android.googlesource.com/platform/frameworks/support/+/androidx-main/compose/runtime/design/how-compose-works.md). Compiler-injected Composer and call transformations; evolving implementation details.
- **E7:** Roslyn, [Source generators cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md). Additive/unordered generation, additional-file inputs, explicit declarations.
- **E8:** Kotlin, [Inline functions](https://kotlinlang.org/docs/inline-functions.html). Reification and call-site inlining.
- **E9:** Android Developers, [Keep rule use cases and examples](https://developer.android.com/topic/performance/app-optimization/keep-rule-examples). JNI/reflection reachability and precise retention.
- **E10:** Microsoft Learn, [Native AOT deployment overview](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/). Managed runtime generation, loading, trimming, and generic restrictions.
- **E11:** Dart tools, [JNIgen](https://pub.dev/packages/jnigen). API extraction/generation pipeline, Gradle classpath inputs, separation from Java packaging, R8 caveats.
- **E12:** Microsoft Learn, [Java bindings metadata](https://learn.microsoft.com/en-us/dotnet/android/binding-libs/customizing-bindings/java-bindings-metadata). Human fixups, `api.xml`, preserved JNI names versus managed names, metadata transforms.
