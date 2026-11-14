# What the experiment can do now

This is the current summary of the preserved prototype, not a shipping support
commitment. Earlier research notes deliberately explore alternatives and are
not all descriptions of the final code.

## The practical path

The runnable product-shaped entrypoint is the **project-local semantic binding
compiler NuGet**. A binding project selects the archives through normal Android
SDK inputs, then adds a private `Microsoft.Android.BindingGenerator.Semantic`
package reference and `AndroidBindingGenerator=Semantic`.

The package provides its own matched parser, official Kotlin metadata/ASM
descriptor reader, semantic planner and existing JNI C# emitter. It imports at
the SDK's late binding-target seam. It does not replace the installed workload,
globally change the compiler for dependency consumers, or resolve native Maven
dependencies itself. Blank or `Legacy` returns to the SDK's generator.

Generic views and Kotlin conveniences use the separate
`Microsoft.Android.Kotlin` runtime. Its authored native bridge is **source** in
that package and must join the app's one selected Gradle graph. Vendor JAR/AAR
payloads do not need to be embedded in managed binding NuGets; they are still
needed as build inputs and ultimately supplied through the app's native graph.

The preview's ordinary R8 consumer rules conservatively keep public/protected
members of locally emitted peers. The current trimmer cannot infer every JNI
slot invoked only by a managed view. This is a declared size tradeoff, not
proof that app-use-minimal native retention has been solved.

## Different identities remain different

One selected native class/ABI, a managed JNI peer owner, and an ordinary managed
view of that peer are different things. Views are not duplicate registered
native owners. Cross-assembly full-name equality does not imply CLR type
equality. Checked conversion through the actual native object is not an
automatic rewrite of existing managed signatures or inheritance.

The steady-state corpus output defaults to one DLL per Maven artifact, with
version-independent names and a canonical owner registry. Existing reservations
survive APIs disappearing or moving native containers. Conflicting first owners
and duplicate native class definitions fail instead of making more projections.

The large consolidated historical snapshot uses the earlier **Maven group**
layout. The later MapLibre/Sentry/Koin snapshots use **artifact** layout. These
are separate outcomes and should never be mixed or relabeled.

## Compatibility option demonstrated

The compiler can regenerate a selected old binding type while preserving its
managed owner and selected public/protected contract, while permitting concrete
additions. The compiled MAUI profile tool extracts both a full contract and a
usage-selected one.

Selection distinguishes signature/identity exposure from construction, direct
member calls, subclassing, overrides and interface implementation. The absence
of a direct IL call is not by itself proof that subclass infrastructure is
unnecessary: legacy JNI dispatch requires threshold/peer properties indirectly.

The real bounded pilot regenerates SwipeRefreshLayout and its two callback
interfaces. Shipped MAUI DLLs and a frozen old consumer run unchanged, while a
new caller invokes an additive API on the same legacy instance. This is not a
guarantee for unlisted plugins, reflection, or arbitrary future subclass paths.
Keep a fuller contract or explicitly include those consumers where needed.

This profile feature is currently a compiler CLI plus separate compiled
verification tool. There is no implemented public MAUI-profile MSBuild switch
in the historical generator package.

## What remains deliberately incomplete

- Imported-owner modern generic views are not generally generated automatically.
- Arbitrary historical handwritten/generic/property/event contract reconstruction
  is not implemented; compiled validation must reject unsupported differences.
- The complete 717-record corpus has not been regenerated in the newest
  per-artifact layout. Coverage counts and old generated outputs are distinct.
- Native API version incompatibilities do not disappear because CLR contracts
  match. The selected Java/native graph must remain valid for every caller.
- No binary-rewriter makes all old binding identities magically interchangeable.
- Source migration, automatic native-payload reconciliation across arbitrary old
  NuGets, app-use-minimal projection closure and finer R8 retention need more work.
- Windows/Linux and every Android workload revision are not certified by the
  historical macOS ARM64 proof.
- The tests exercised bounded offline JNI paths, not commercial service
  operations, real purchases, ad requests or token-based map rendering.

## Reading the evidence

Use the current workbench validation report for commands actually rerun during
the repository handoff. Files under historical research/results and the release
inspection archives are labeled snapshots. They preserve original decisions,
generator output, provenance and bounded test results without implying those
commands are current turnkey entrypoints.
