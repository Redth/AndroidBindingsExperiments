# Semantic Android binding compiler: final spike results

## Conclusion

**The requested corpus experiment is complete: every in-scope archive with a
bindable API has compiled through the experimental compiler without C# errors.**
That is 362 independent-library comparisons plus EmojiPicker 1.7.0 in a
separately regenerated, Gradle-resolved dependency closure. No library-specific
binding metadata or hand-written C# binding repairs were introduced.

The evidence supports continuing with this compiler direction. The outputs
retain more actual native APIs, repair incorrect JNI descriptors and CLR
contracts, and pass targeted Android dispatch tests. This is a successful
compiler spike, **not** a production-ready Kotlin replacement or proof that
every generated member of every library behaves correctly.

My assessment is that the reviewed baseline surfaces are functionally at least
as good, with meaningful improvements in native ABI accuracy and recovered APIs.
The baseline is the existing generator on identical raw inputs, not an
assembly-level compatibility comparison with every hand-corrected shipped NuGet.

## Exact scope and accounting

The upstream configuration is pinned to `dotnet/android-libraries` commit
`2393441675a6d82bb817ab7aa2df698c7ec929ce`: 444 records, comprising 350 AndroidX
and 94 Google Play Services entries. Dependency-only and explicitly nonbinding
records remain accounted for.

The final complete independent-library run is
`binding-corpus/runs/16cbf0c447c43193f322/summary.json`, relative to this session's
`files` directory. It uses the 621-test compiler snapshot.

| Independent-library classification | Records |
|---|---:|
| Compiles without a registered-API-loss flag | 321 |
| Compiles with conservative API-review flags | 41 |
| C# compilation fails | 0 |
| Published-reference input graph cannot restore | 1 |
| Contains only package metadata, not a bindable class | 1 |
| Contains no JVM classes | 80 |
| **Total** | **444** |

Legacy generation fails C# compilation for **138 of those independent-library
comparisons**. The semantic pipeline compiles all 362 prepared binding outputs.
The corpus command still exits with code 1: unresolved published-reference
inputs and conservative inventory-review flags deliberately remain visible.
This exit code is not being presented as an all-green automated test result.

The metadata-only record is Work GCM 2.12.0. Its exact AAR contains only
`androidx/work/impl/background/gcm/package-info.class`; there is no application
class missing from its output.

## Resolving the input graphs honestly

Three previously blocked adaptive-library graphs now restore after a verified
WindowCore promotion from 1.5.1.2 to 1.5.1.3. That selection satisfies every
observed declared range. The harness preserves original constraints, promotion
evidence, and both build logs. It never suppresses a downgrade warning or relaxes
an incompatible range.

EmojiPicker 1.7.0 is different. Its published-reference graph requires Emoji2
`[1.2.0.2,1.2.1)`, while selected AppCompat requires at least 1.6.0.3. Those
constraints genuinely conflict and remain an explicit input failure.

The existing Gradle resolver produces a coherent native graph for that same
EmojiPicker archive. Gradle module metadata selects **Emoji2 1.7.0**, not the
older version suggested by looking at the POM alone. The saved graph includes
AppCompat 1.6.1, Activity 1.6.0, Fragment 1.3.6, Lifecycle 2.5.1, Kotlin stdlib
2.1.20, coroutines 1.9.0, and Guava 32.0.1-android. Its lockfile is preserved in
`emoji-native-closure/gradle.lockfile`.

We consumed that already-resolved graph; dependency resolution was not redesigned.
The separate closure result does **not** relabel the incompatible NuGet restore
as successful.

## Common-assembly ownership experiment

`compare-binding-closure.py` regenerates **36 AndroidX modules into one
BindingClosure.dll**, using 49 resolved native archives. It imports no published
AndroidX/GMS binding DLLs. Fifteen published non-AndroidX support assemblies,
including Kotlin/Guava bindings, remain; this is not a completely regenerated
Java ecosystem.

Evidence: `binding-closures/closure-fd39c48ff195/comparison.json`.
Legacy output fails compilation; semantic output compiles with zero C# errors.

An application derives from the regenerated AppCompatActivity and uses the
regenerated Fragment, Lifecycle, SavedState, and EmojiPicker types. All five
checks passed on the owned API35 ARM64 emulator in each configuration:

| Configuration | Passed |
|---|---:|
| Debug/CoreCLR | 5/5 |
| Release, full trimming, R8 | 5/5 |
| Release NativeAOT, full trimming, R8 | 5/5 |

The checks cover common assembly ownership, activity services, a real Java
lifecycle callback into a managed observer, a default interface method whose
native ancestor is inaccessible, and Kotlin widget construction/property
round trips. NativeAOT reports an assembly-level **IL3053 warning for
Mono.Android**; it is not warning-free.

Only the selected native graph is packaged: support DLLs are staged without
adjacent NuGet AARs and embedded native-resource extraction is disabled.
Extracted-library provenance contains no unexpected archive origins in the
optimized runs. This prevents a second native dependency graph from leaking
back in through the support bindings.

This is concrete evidence for the user's common-assembly ownership idea.
It does not establish independently regenerated per-artifact ownership or
cross-assembly type equivalence.

## API quality review

Across all 362 independent compilations, the inventories lose **no registered
native types or fields**. Relative to legacy output, they add 15,277 registered
types, 49,855 methods, and 17,113 fields.

There are 213 apparent method-registration differences:

| Difference | Count | Review |
|---|---:|---|
| Same native parameters, different return descriptor | 169 | Registered replacement matches a raw native declaration |
| Different native parameter descriptor | 26 | Registered replacement matches a raw native declaration |
| Exact synthetic bridge declaration | 2 | Typed registered alternatives remain |
| No same-owner raw declaration | 16 | Reviewed CLR inheritance, imported descriptors, and inventory ownership |

The last sixteen were checked with **eight separately compiled C# consumers**
against the final binaries: Media2, Media3, Paging, Preference, Watchface,
GMS Auth, GMS Base, and Wallet. All eight compile.

Five differences retain imported native descriptors rather than reconstructed
Object/CLR-list descriptors; seven remain callable through inherited class or
interface contracts; four are XPath-owner inventory differences in generated
iterator invokers. Paging's operation is callable through
`Java.Util.ISequencedCollection`, not an invented class method. Media3's inherited
decoder results remain erased to Java.Lang.Object; this review does not claim
new typed generic return facades.

Evidence:
`binding-corpus/runs/16cbf0c447c43193f322/native-signature-review.json`,
its `inherited-consumers/results.json`, and
`binding-final-inherited-consumers.log`. The per-difference review is persisted
in the same run's `managed-contract-review.json`.
Inventory analysis and consumer compilation are evidence, not exhaustive
runtime equivalence proofs.

The field review also caught genuine earlier regressions and drove repairs:
AuthScope.zza, a field colliding with a static method, and distinct Places
fields whose managed names collide. Their native registrations are now retained,
and the Android fixture verifies that the two colliding fields refer to
different actual Java objects.

## General compiler repairs

The experiment is opt-in through `--experimental-semantic-model`; default
generation remains unchanged. General rules, rather than artifact-specific
transforms, now cover:

- Immutable JVM facts separated from managed projection decisions and JNI ABI.
- Imported CLR names, properties, enums, collections, and registered descriptors.
- Actual abstract method/property inventories and explicit interface JNI adapters.
- Required synthetic native slots, including obfuscated non-bridge variants.
- Protected CLR overrides with callable Java-public facades.
- Deterministic type, field, overload, listener, and callback name allocation.
- Kotlin value-class struct/boxed-peer pairing and typed public peer bounds.
- Public contracts inherited through inaccessible native interfaces.
- Default methods without phantom private CLR interface implementations.
- No generated Java listener implementors for inaccessible native contracts.

The last three were exposed by the regenerated closure, not by isolated
compilation against manually corrected published AndroidX dependencies.

## Validation and reproduction

Run commands from the worktree root with the session-isolated NuGet cache.

```sh
./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj --no-restore -v minimal
```

**Passed: 621, failed: 0.** Log: `binding-native-listener-tests.log`.

```sh
python3 -m unittest discover -s external/Java.Interop/tests/generator-Tests -p 'test_compare_binding_corpus.py' -v
```

**Passed: 14.** Log: `binding-final-harness-tests.log`.

```sh
python3 external/Java.Interop/tests/generator-Tests/compare-binding-corpus.py \
  --config /absolute/path/to/saved-config.json \
  --config-commit 2393441675a6d82bb817ab7aa2df698c7ec929ce \
  --output /absolute/path/to/corpus-evidence --jobs 4
```

**Completed all 444 records; zero semantic C# failures**, with the explicit
accounting above. Actual command/output: `binding-corpus-seventeenth-pass.log`
and the final summary.

```sh
python3 external/Java.Interop/tests/generator-Tests/compare-bindings.py \
  --pilot contracts --iterations 1 --output /absolute/path/to/evidence \
  --device-serial emulator-5570
```

**Passed 9/9 Android checks with the final compiler.** Semantic output has zero
C# errors; legacy has three. Evidence:
`full-binding-spike/comparisons/contracts-20261028T230645Z-58136b28/comparison.json`.
These cover defaults, generics, interface-diamond callbacks, synthetic abstract
properties, arrays, obfuscated public bases, formatted adapters, and native
field collisions. The earlier 618-test snapshot also passed those nine checks
under Release/full trimming/R8 and NativeAOT/R8.

Closure commands and all three device results are saved in its comparison JSON;
the repository README documents the CLI, including
`--probe-configuration Release` and `--probe-nativeaot`.
Logs: `binding-emoji-closure-debug-final.log`,
`binding-emoji-closure-release.log`, and `binding-emoji-closure-nativeaot.log`.

## Tradeoffs and next implementation slice

The full-surface output is larger: approximately **332.4 MB of generated C#
versus 220.1 MB**, about 51% growth across the 362 comparisons. Much of that
comes from retaining public obfuscated-looking types required by other APIs.
This is API recovery, not proof of better build times or smaller applications.
Parallel corpus timings are not a controlled performance benchmark.

The next recommended slice is to make the effective managed contracts explicit
in the new IR and drive lowering from them, using the now-working native closure
as an acceptance fixture. Follow with exact bridge-target bytecode facts,
verified value-class box/unbox operations, and a documented Kotlin-version and
projection policy.

Suspend/Task, Flow, default-argument adapters, fully generic CLR facades, and
managed subclassing of every JVM-only mangled method are **not implemented** by
this spike. Opaque signatures and some generic erasure remain. Runtime evidence
is targeted and ARM64, not a behavioral matrix of every API/ABI/library.
The legacy emitter still participates. These are explicit productionization
boundaries, not silently successful fallbacks.

All changes and evidence remain local and uncommitted. Nothing was pushed and
no pull request was created.

Both owned probe applications were uninstalled and the read-only emulator was
stopped after validation.
