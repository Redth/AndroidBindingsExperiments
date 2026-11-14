# Android binding compiler: corpus comparison checkpoint (historical)

**Superseded by `android-binding-compiler-results.md`.** The final 621-test run
completed all 444 records: 362 independent compilations have zero C# errors,
and the remaining bindable EmojiPicker archive compiles and runs in a
regenerated Gradle-native dependency closure. The details below preserve the
earlier fourteenth-pass checkpoint, not the current completion status.

## Status

**The complete corpus run has reached zero semantic C# compilation errors:
359 generated libraries compile, versus 138 failing legacy compilations.**
The requested all-green, functionally equal-or-better result has not yet been
established: four input graphs fail before generation, and API/behavioral review
remains separate from compilation.

The source configuration is `dotnet/android-libraries` commit
`2393441675a6d82bb817ab7aa2df698c7ec929ce`. Its 444 in-scope records contain
350 AndroidX and 94 Google Play Services entries, including explicitly
nonbinding and dependency-only records.

Comparisons use exact Maven archives, raw class-parse XML, separate old/new
generation, identical compiler/reference arguments, tool snapshots, and saved
input/reference hashes. References are matching published NuGet dependency
DLLs, not an entirely regenerated dependency closure.

## Latest completed full pass

Evidence: `binding-corpus/runs/850b5a89075327a3fc3d/summary.json`, relative to
this session's `files` directory.

| Classification | Records |
|---|---:|
| Compiles without a registered-API-loss flag | 320 |
| Compiles, registered API changes require review | 39 |
| Compiles, but projection is empty | 0 |
| C# compilation fails | 0 |
| Input preparation fails | 4 |
| No generated sources; requires review | 1 |
| Archive has no JVM classes | 80 |
| Total | 444 |

Complete-pass C# failure counts progressed through 47, 35, 22, 20, 41, 10,
1, and 0. The increase to 41 was a real invoker regression: actual emitted
abstract properties were missing from its authoritative declaration inventory.
That regression was repaired, not hidden by combining focused results.
The fourteenth pass uses the current 618-test snapshot. Preference 1.2.1 compiles
after explicit interface adapters retain the required `Formatted` suffix.
The actual GMS Games V2 `AuthScope.zza` field also remains registered.
The command still exits unsuccessfully because input failures and API-review
statuses are not treated as successful comparisons.

The preceding pass's API review found another real field loss: two native Places fields,
`PlaceDetectionApi` and `PLACE_DETECTION_API`, project to the same CLR name.
The compiler now allocates distinct names in deterministic native-name order.
The 618-test suite and nine-check Android probe cover this repair; the fourteenth
complete pass confirms that both actual Places fields remain registered.
Across all 359 compiled records, no registered native types or fields are lost.
The inventories add 15,277 types, 49,847 methods, and 17,097 fields. Much of
this expansion comes from retaining public obfuscated-looking types, not new
hand-designed APIs; the larger surface and build cost still need evaluation.

The seven previously empty compilations no longer have empty output after
retaining public types with obfuscated-looking names. This is not a claim
that every recovered internal-looking API is useful or production-ready.
No-JVM artifacts, input failures, and API-loss reviews remain separate categories.

Activity 1.13.0, Activity Compose 1.13.0, Fragment 1.9.1, Autofill 1.3.0,
Google Play Services Base 18.11.0, Basement 18.12.0, and Tasks 18.4.1
all compile with zero C# errors in that full pass.

## Compiler changes exercised

- Imported managed contracts retain their registered native descriptors and
  actual property metadata rather than reconstructing an approximate ABI.
- Override families preserve imported names, enum contracts, and erased
  interface requirements. Native invocation and logical override identities
  remain separate.
- Java visibility widening uses a protected C# override and a public forwarding
  method instead of emitting an illegal public C# override.
- Type/namespace and member/type collisions receive explicit allocation rules.
- Semantic legacy callbacks distinguish JVM-native method identities even when
  Kotlin overloads share the same primitive JNI parameter signature. Connector
  metadata, delegate references, callback names, and targets agree.
- Unboxed Kotlin callback parameters are reconstructed as their actual
  projected structs, preventing ambiguous calls between primitive-backed
  overloads.
- Bridge descriptors cannot merge methods with different Java names. This
  corrected a regression in the GMS converter bindings.

The semantic changes remain opt-in; the default generator path is unchanged.

### Latest native and managed contract repairs

- Generic explicit interface adapters invoke the exact interface JNI slot,
  rather than forwarding through a guessed typed C# target.
- String conveniences do not retype nested CharSequence collection elements
  while retaining incompatible collection marshalers.
- Class invokers consume the actual emitted abstract methods and properties.
  Imported CLR declarations and source-ancestor synthesized declarations are
  handled separately; explicit adapters do not create phantom overrides.
- Real synthetic entry points implementing abstract base or interface slots
  are preserved, including obfuscated methods with the JVM bridge flag cleared.
  A bridge's own entry point does not require guessing its typed target.
- Concrete native peers retain inherited/default Java interface operations
  that their managed bases do not expose.
- Public types are not discarded solely because their names begin with `zz`
  or are short. Explicit obfuscation exclusions remain honored.
- Nested types cannot reuse their enclosing managed type name; fields cannot
  hide required inherited methods. Erased final overloads can be named
  independently of required base overrides.
- Interface callbacks select the original declaring CLR interface in diamonds.
- Public facades for omitted variant properties invoke their native slots,
  rather than assigning to unrelated inherited typed properties.
- The latest field repair prevents a newly recovered static method from
  causing an existing native field to disappear.

## Subsequent focused repairs

All four previously failing protobuf artifacts now compile. Generic interface
slots are real abstract declarations rather than throwing placeholders, and
reference returns use the correct managed symbol and marshaler.

Evidence: `binding-corpus/runs/edacf0258ca83445d6b1/summary.json`.

| Artifact | Errors before this repair | Errors after |
|---|---:|---:|
| Datastore preferences external protobuf 1.2.1 | 363 | 0 |
| Health Connect external protobuf 1.1.0 | 363 | 0 |
| Protolayout external protobuf 1.4.2 | 363 | 0 |
| Wear Tiles proto 1.1.0 | 590 | 0 |

Later repairs exclude inaccessible Java interfaces from managed contract
families and class invokers, preserve inherited typed interface adapters, and
allocate argument types across unrelated listeners without renaming their
events. Referenced array elements, primitive aliases, nested collections, and
CharSequence property names now retain their actual CLR contracts.

Focused evidence: `binding-corpus/runs/24f01b7dd10216ad4392/summary.json`.
Media3 Exoplayer, Camera Extensions, Camera Lifecycle, AppCompat Resources,
Transition, and GMS Ads API compile with zero C# errors. Exoplayer still requires
registered-API review. The earlier GMS Ads Lite 20.0.0 and 24.0.0 focused
comparisons also compile.

Further native-preserving naming repairs compile Collection JVM, ConstraintLayout
Core and Solver, and Wear Watchface Style. Evidence:
`binding-corpus/runs/af75e9ac1ccbcb7670de/summary.json`. Core and Compose UI
still have failures; these focused successes are not aggregate corpus results.

The sixth pass includes the 560-test snapshot, including inherited setter-property
reconstruction and distinct events for multiple listener connectors. It predates
the latest method/property, static-factory, string-convenience, and
delegate-shadowing repairs.

Later focused comparisons now compile Room, Core, Camera Camera2, Media3 UI,
both VectorDrawable artifacts, and Compose Animation Core. General repairs retain
explicit interface properties, inherited mutable setters, default-interface
overrides, exact enum/Color parameters, inherited CharSequence enumeration,
independent listener implementors, and typed public generic bounds.

Evidence:
- Room: `binding-corpus/runs/be0ccdba14226430cbf7/summary.json`.
- Camera Camera2 and VectorDrawable: `binding-corpus/runs/1d472f1de27ab8f5be09/summary.json`.
- Core and Media3 UI: `binding-corpus/runs/13739c45858728124405/summary.json`.
- Compose Animation Core and dynamic bound-name checks:
  `binding-corpus/runs/68f829469513fb3cebc0/summary.json`.

The seventh 444-record comparison completed with the 579-test snapshot. It
includes those repairs but predates the newer effective-invoker declaration work.
With that later repair, Compose UI also compiles with zero C# errors:
`binding-corpus/runs/51c86fbb7a3ba11df72a/summary.json`.
Do not extrapolate focused successes into aggregate counts.

Subsequent general repairs preserve erased static/final overloads under distinct
descriptive names, pack high-arity native callback arguments into value tuples
inside the existing guarded JNI transition, and retain otherwise inaccessible
signature types as opaque peers without changing JNI descriptors. In a rerun of
the seventh pass's 35 failing records, 12 now compile and 23 still fail:
`binding-corpus/runs/bd3d7dcf79fd32c68700/summary.json`.
Those 12 include Compose Runtime, Runtime Saveable, UI, UI Text, UI Tooling,
UI Unit, Material3, Protolayout Expression, GMS Maps, and the three Emoji records.
Four of those compilations retain explicit API-review flags.

The eighth complete pass includes public signatures exposing protected nested
peers and protected classes inheriting omitted private bases. Opaque peers
intentionally weaken CLR typing; these outputs
require usability and Android validation rather than an automatic quality pass.

Two later repairs restore public operations inherited from inaccessible Java
interfaces without inheriting phantom CLR interfaces, and distinguish Kotlin's
Java-source override restriction from the obligation to implement an abstract
CLR slot. Compose Foundation, UI Graphics, and Media3 Exoplayer compile with zero
C# errors in `binding-corpus/runs/747ce74a46e9e485e373/summary.json`; all three
still require API review. Mangled Kotlin managed-subclass callbacks still need
device/build validation; compiling existing peer calls is not proof that Java
callable wrappers can implement every JVM-only method name.

The ninth complete pass, `binding-corpus/runs/0f797e388cc3914e1ce3/`,
completed with 20 failed records. The tenth, `4a6826a7296bb20d1396`, exposed
the 41-record abstract-property invoker regression. The eleventh,
`a6987433343315448008`, reduced compilation failures to ten, while exposing
additional real public GMS types. The fourteenth full result is reported above.
The intervening focused comparisons are diagnostic evidence, not aggregate
corpus counts.

## Native API review

The seventh pass's 23 API-review artifacts lose no registered types or fields.
Of 166 apparent lost method registrations, four match declarations in the raw
native facts; 162 do not. This is an audit triage, not proof of invalid legacy
registrations: inherited JVM lookup and full CLR usability still need review.
Evidence: `binding-corpus/runs/be2dab94ff6d81215f9b/native-signature-review.json`.

The four matching declarations in that seventh-pass audit are three synthetic bridges plus a real
`SegmentDownloaderFactory.create` contract. The latter was exposed by the legacy
`SegmentDownloader.BaseFactory` binding despite being declared on an inaccessible
Java interface. Removing that interface from CLR inheritance must not also remove
its public callable factory operation. The later native-contract repair restores
this operation, including a real abstract declaration and concrete JNI invoker;
the focused Exoplayer comparison has zero C# errors and no longer loses the
`create` registration. Six other method differences still require review.
Evidence: `binding-corpus/runs/1c7e0eb56d967df965ed/summary.json`.
Registration inventory owners come from
source XPath comments and can identify the original interface rather than the
generated managed class; native-declaration matching alone is insufficient.

Separately, `javap -p -s` against the exact Compose UI Maven archive confirms
four apparent method losses are repairs of incorrect legacy descriptors:
`FocusTraversalKt.getActiveChild`, `FocusTraversalKt.isEligibleForFocusSearch`,
`LookaheadLayoutCoordinatesKt.getRootLookaheadDelegate`, and
`LayoutNodeLayoutDelegateKt.isOutMostLookaheadRoot`. Their semantic signatures
retain the actual FocusTargetNode, LookaheadDelegate, and LayoutNode types instead
of the legacy Modifier.Node, Placeable, and Object descriptors. That projection
also adds nine registered methods and 51 fields, losing no types or fields.
The API-review flag remains; this does not establish behavioral equivalence.
Evidence: `binding-corpus/runs/51c86fbb7a3ba11df72a/androidx.compose.ui__ui-android__1.12.1/api-review/javap-descriptors.log`.

The eleventh pass's 39 API-review records add 925 registered types, 3,078
methods, and 1,365 fields relative to their legacy outputs. They have 208
apparent method losses: four match exact native declarations and are synthetic
bridges; 204 do not match exact original declarations. This remains triage,
not proof that inherited lookups or CLR usability are equivalent.
Evidence: `binding-corpus/runs/a6987433343315448008/native-signature-review-eleventh.json`.

That audit also found a genuine lost `AuthScope.zza` field in GMS Games V2:
retaining its previously omitted return type made a static method bindable,
and the old field emitter then skipped the same-named field. The current
compiler allocates a separate managed field name instead. The regression test
requires both native registrations; the thirteenth corpus pass confirms that
the actual artifact retains the field and has no lost native registrations.

The fourteenth pass's 39 review records have no lost registered types or fields
and 209 apparent
method losses. Of those methods, 165 have a same-name native declaration with
the same parameter descriptor but a different return descriptor; 26 have
different native parameters; two exactly match native synthetic bridges; and
16 have no same-owner raw declaration. All 191 descriptor-change cases have
registered replacements matching raw native declarations. This narrows the
review, but does not prove complete CLR API equivalence or resolve inherited
lookups for the remaining cases.
Evidence: `binding-corpus/runs/850b5a89075327a3fc3d/native-signature-review.json`.

The no-source Work GCM 2.12.0 record has been inspected separately. Its exact
AAR contains one JVM class, `androidx/work/impl/background/gcm/package-info.class`,
and no class to bind. It is not a silently discarded application API. The
machine status remains review-required, with this explicit human-readable
explanation; it is not counted as a compiled binding.

## Validation

With the session-isolated NuGet cache:

```sh
./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj --no-restore -v:minimal
```

Result: **618 passed, zero failed**, including actual property inventories,
source/imported abstract native slots, synthetic non-bridge implementations,
inherited interface callback selection, erased-overload allocation, native field
retention, deterministic field-name collisions, and formatted explicit interface names.
Command output: `binding-field-identity-tests.log`.

```sh
python3 -m unittest discover -s external/Java.Interop/tests/generator-Tests -p 'test_compare_binding_corpus.py' -v
```

Result: **6 passed**.

The current-callback Activity 1.11.0 comparison also ran:

```sh
python3 external/Java.Interop/tests/generator-Tests/compare-bindings.py --pilot activity --device-serial emulator-5570 --output ${EXPERIMENT_ARTIFACTS}/full-binding-spike/comparisons
```

Results: legacy **34 errors**, semantic **zero errors**; 75 registered types
and 255 methods in both modes; registered fields **28 to 39**.
The Android probe passed **13/13 checks**, including Java-to-managed generic
overrides and base dispatch. Its median generation times were 1.268 seconds
legacy and 1.353 seconds semantic; these are a limited warmed pilot, not
full-corpus build-performance measurements.
Evidence: `full-binding-spike/comparisons/activity-20261028T221434Z-179f0781/comparison.json`.

A generated Java-contract fixture also runs with the current 618-test compiler:

```sh
python3 external/Java.Interop/tests/generator-Tests/compare-bindings.py --pilot contracts --iterations 1 --output ${EXPERIMENT_ARTIFACTS}/full-binding-spike/comparisons
python3 external/Java.Interop/tests/generator-Tests/compare-bindings.py --probe-comparison ${EXPERIMENT_ARTIFACTS}/full-binding-spike/comparisons/contracts-20261028T223037Z-5bd64647/comparison.json --device-serial emulator-5570
```

Legacy: **three C# errors**. Semantic: **zero C# errors**, 12 to 15 registered
types, 22 to 30 methods, and zero to three fields. Nine generated-binding Android checks pass:
inherited `Iterator.remove` default dispatch, a generic interface implementation,
a Java-to-managed interface-diamond callback, synthesized abstract property
getter/setter invokers for a private native class, an interface-array generic
specialization, a public API inherited from a `zzg` base, a formatted generic
interface adapter, a field colliding with a static method, and distinct native
fields whose CLR names collide. The last check also verifies that the two
fields resolve to different actual Java objects.

The same nine checks pass in Release with full trimming and R8, and in
Release NativeAOT/R8. The build commands use:

```sh
PROBE=${EXPERIMENT_ARTIFACTS}/full-binding-spike/comparisons/contracts-20261028T223037Z-5bd64647/device-f903dae1/DeviceProbe.csproj
./dotnet-local.sh build "$PROBE" -t:Install -c Release -p:PublishTrimmed=true -p:TrimMode=full -p:AndroidLinkTool=r8 '-p:AdbTarget=-s emulator-5570' -v:minimal
./dotnet-local.sh build "$PROBE" -t:Install -c Release -p:PublishAot=true -p:PublishTrimmed=true -p:TrimMode=full -p:AndroidLinkTool=r8 '-p:AdbTarget=-s emulator-5570' -v:minimal
```

Both builds succeeded. NativeAOT reported one assembly-level `IL3053` analysis
warning for `Mono.Android`; it is not a warning-free result. Fresh logcat
captures after cold launches contain all nine checks:
`binding-nine-contract-release-results.log` and `binding-nine-contract-nativeaot-results.log`.
Release packages are not debuggable, so their results were read from fresh
device logs rather than accepting stale `run-as` output.

The nine-contract runs use the current 618-test compiler; the separate Activity
probe uses the earlier 614-test compiler. They do not prove every new override rule,
Kotlin default-interface overload, opaque signature, or boxed value class.
The owned probe application was uninstalled and the read-only emulator stopped.

## Recommended next implementation slices

1. **Validate an entirely regenerated dependency closure.** The current harness
   imports published dependency bindings, which can hide upstream manual
   corrections. Give regenerated assemblies real, distinct identities and
   plan namespaces consistently before compiling dependency-connected graphs.
   Consume the already-resolved Java dependency graph; this does not require
   redesigning the existing Gradle/Maven integration.
2. **Make effective managed contracts explicit in the new IR.** The observed
   imported abstract-slot, property, overload-erasure, and inaccessible-type
   failures are repaired, but much of their modeling still depends on the
   legacy object model and emitter.
3. **Implement a complete value-class representation and marshaling model.**
   A primitive-backed struct alone is insufficient. Real corpus outputs expose
   boxed nullable values and value classes in generic collections. The paired
   primitive-backed struct and registered boxed peer now retain reference
   positions and nested companions. Explicit verified box/unbox operations
   remain unimplemented; audit operations and named values before calling
   these outputs an improvement.
4. **Record exact synthetic bridge targets and widen Android dispatch probes.** Name/arity and descriptors are
   insufficient to establish which typed implementation a bridge invokes.
   Decode supported bridge bytecode into immutable facts and reject ambiguous
   patterns instead of guessing. Reparse raw inputs when adding those facts.
   Expand probes to Kotlin mangled overrides/Java callable wrappers, high-arity
   callbacks, and boxed values; audit actual CLR usability rather than only
   registration counts.

Compilation coverage is much broader, but weaker opaque signatures, imported
hand-authored framework contracts, boxed Kotlin operations, and complete managed
API usability still need validation. The focused `Android.Graphics.Color`
return contract compiles.

Four input graphs also remain unresolved:

| Maven input | Published-reference problem |
|---|---|
| Material3 adaptive navigation suite Android 1.4.0 | WindowCore direct minimum 1.5.1.2 versus transitive minimum 1.5.1.3 |
| Material3 adaptive Android 1.3.0 | Same compatible-range WindowCore downgrade |
| Material3 adaptive layout Android 1.3.0 | Same compatible-range WindowCore downgrade |
| EmojiPicker 1.7.0 | Direct Emoji2 range `[1.2.0.2,1.2.1)` conflicts with AppCompat's minimum 1.6.0.3 |

The three WindowCore graphs permit coherent promotion within their declared
ranges, but the current reference-preparation harness does not perform it.
The EmojiPicker published graph is genuinely incompatible. No restore error
has been suppressed and no incompatible constraint silently overridden.

All work remains local and uncommitted; nothing has been pushed and no PR has
been created.
