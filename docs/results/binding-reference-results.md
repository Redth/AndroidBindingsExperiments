# Customer-shaped binding projection workflow

## Result

The four proposed next steps now work together in one opt-in vertical slice:
an ordinary Android app, independently compiled C# libraries, explicit canonical
ownership/minimal views, incremental generation, and final native contract
validation.

The source is in `tests/BindingProjection/`. It builds with:

```sh
./dotnet-local.sh build tests/BindingProjection/Reference.slnx -c Debug -nr:false
```

The final documented solution build passed in 4.94 seconds. It does not need
a Python generation/comparison driver or published AndroidX/Kotlin managed
binding packages.

## Application and library boundaries

The interactive app increments a real Preferences DataStore counter and observes
it through generated typed Flow conveniences. It derives from regenerated
ComponentActivity and ties collection to the actual Lifecycle owner.

The app also directly exercises generated `IDataStore`, `Preferences`, and key
APIs from C#, rather than testing only a Kotlin wrapper. The authored native
repository is ordinary source-built business logic using DataStore.

Reader and Writer build independently. Both contain an internal type named
`Reference.Local.SettingsView`, but Reader projects observation and Writer
projects incrementing. They are distinct CLR types and do not register duplicate
JNI peers. Both delegate through the same canonical repository peer.

A Java dispatch from Writer reaches a managed listener implemented in Reader,
preserving the original Activity peer across the boundary.

**Confirmed ownership decision:** the user chose one canonical foundation owner
for this pilot. `Reference.Foundation` is explicitly named and owns 2,459
projected types. The optional Kotlin runtime remains a separate owner.

The ownership planner records Maven provenance and suggested partitions:
39 resolved Maven archives, 23 groups, and 22 cycle-safe partitions. The actual
cycle is Lifecycle/SavedState. These are planning/provenance outputs, not 22
new foundation assemblies; materializing those assemblies is intentionally
deferred by the chosen pilot scope.

## Minimal projections and requirements

The binding compiler accepts versioned JSON view contracts selecting native
types/operations and their canonical managed methods. Unambiguous operations
do not require handwritten JNI descriptors; ambiguous overloads require an exact
selector.

Generated views are small ordinary internal C# facades, with no second peer
registry, owning JNI reference graph, or view-disposal contract. They do not
reduce the canonical foundation's full surface.

Each library embeds a native requirements manifest. The final app validates
required owners, exact JVM slots, invocation kind, and available Kotlin return
contracts against the selected graph before compilation.

Using the real precompiled Reader/Writer DLLs, integration checks confirmed:

| Final graph change | Outcome |
|---|---|
| Current compatible API/owners | Accepted, two requirements validated |
| Required native operation removed | Rejected with the missing JVM member and consuming assembly |
| Instance operation changed to static | Rejected with invocation-kind diagnostic |
| Flow element Kotlin contract changed | Rejected with Kotlin return-contract diagnostic |
| Canonical CLR owner changed | Rejected with conflicting-owner diagnostic |

Unit coverage separately confirms that moving a JVM type between Maven groups
does not change its explicit canonical CLR owner.

This is selected-contract validation, not a proof of every JVM behavioral,
serialization, or public binary-compatibility rule.

## Real upgrade and incremental behavior

The native graph is resolved through the existing Gradle integration and checked
in as a dependency lock. DataStore was upgraded from **1.2.0 to 1.2.1** using its
normal dependency setting and lock update. The app rebuilt without binding
metadata or API XML fixes.

Measurements on this machine, with warm dependency caches:

| Scenario | Time | Observed invalidation |
|---|---:|---|
| Clean managed solution/app build | 45.64 s | Native index, canonical output, local views and libraries rebuilt |
| Final unchanged rebuild | 6.02 s | No native index, canonical source, minimal view or measured library DLL rewritten |
| Real C#-only edit in Writer | 12.26 s | Only Writer DLL changed among the measured library/index/view outputs |
| DataStore 1.2.0 → 1.2.1 | 50.22 s | Shared index, ownership, canonical binding, views and libraries invalidated correctly |

The ordinary final solution build also passed in 4.94 seconds. These are
single-machine observations, not controlled throughput guarantees or cold
dependency-download measurements.

The cached native index is shared through Foundation. Neither Reader nor
Writer scans JARs inside Roslyn. There is no Roslyn authoring frontend yet:
the current explicit JSON frontend runs in the normal MSBuild pipeline.

## Bugs exposed and repaired

The customer-shaped workflow found integration issues that the earlier
comparison harnesses did not exercise:

- SDK Gradle initialization assigned all subprojects the same output directory.
  The selected module now keeps its expected output path while other projects
  receive isolated directories. Root-module and existing child-module workflows
  were exercised.
- Automatic AAR discovery beside managed assemblies could introduce a second
  native graph. An experimental private opt-in makes the explicitly resolved
  graph authoritative; existing adjacent-AAR behavior remains the default.
- RID publish discovery skipped binding generation/compiler execution but still
  overwrote the outer compiler cache using an incomplete source list. That
  forced otherwise unchanged bindings to rebuild. Discovery no longer poisons
  the outer compile cache.
- Kotlin runtime source staging used many-to-many timestamp comparisons and
  never became up to date. It now has one-to-one source/destination mappings.
- Gradle input discovery now includes lockfiles/version catalogs and excludes
  generated build/cache directories.
- Indirect generic interface mapping indexed a type parameter even when its
  inherited mapping was already a concrete type. Concrete mappings are preserved.
- The generator rejected an invocation supplied entirely through a response
  file. That normal-build path now works.

All experimental build switches remain private/opt-in. There are no new
production MSBuild task classes.

## Actual Android verification

API35 ARM64 device execution:

| Configuration | Checks | Persistence |
|---|---:|---|
| Debug | 9/9 twice | Activity recreation and process restart passed |
| Full trimming + R8 | 9/9 twice | Activity recreation and process restart passed |
| NativeAOT + full trimming + R8 | 9/9 twice | Activity recreation and process restart passed |

The final descriptor-free Debug build was independently rerun and passed both
cycles again. Each run uses unique log identities and records the exact APK
hash. The second process must observe the first process's committed value.

The first owned emulator later crashed after its successful initial run.
Final matrices were rerun successfully on an isolated network-simulator/
software-rendering instance. The owned test application was uninstalled and
the emulator was stopped afterward.

NativeAOT retains the existing Mono.Android IL3053 warning. Fresh full-surface
foundation builds also report existing generated binding warnings; they are
not described as warning-free.

## Host validation and commands

Commands ran from the worktree root with JDK 21, the Android SDK, and an isolated
NuGet cache:

| Command | Result |
|---|---|
| `./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj -c Debug -nr:false -v minimal` | 682 passed |
| `./dotnet-local.sh test src/Xamarin.Android.Build.Tasks/Tests/Xamarin.Android.Build.Tests/Xamarin.Android.Build.Tests.csproj -c Debug -nr:false --filter 'FullyQualifiedName~ExperimentalBindingsTests' -v minimal` | 6 passed |
| `./dotnet-local.sh test bin/TestDebug/net10.0/Xamarin.Android.Build.Tests.dll --filter 'FullyQualifiedName~AndroidGradleProjectTests&Name~BindLibrary&Name~CoreCLR&Name~Release' -v minimal` | 2 existing child-module/default-generation integration cases passed |
| `python3 -m unittest discover -s external/Java.Interop/tests/generator-Tests -p 'test_compare_binding_corpus.py' -q` | 14 passed |
| `./dotnet-local.sh build tests/BindingProjection/Reference.slnx -c Debug -nr:false -v minimal` | Passed |
| `./dotnet-local.sh build tests/BindingProjection/App/Reference.App.csproj -c Release -nr:false -v minimal` | Passed |
| `./dotnet-local.sh build tests/BindingProjection/App/Reference.App.csproj -c Release -p:PublishAot=true -nr:false -v minimal` | Passed |
| `git diff --check` and project/target XML parsing | Passed |

`tests/BindingProjection/verify-reference.py` only installs/runs a normally
built APK; it is not part of binding generation. Its README provides commands.
The source change set contains no generated JAR/AAR/class payload. New Maven
dependencies belong to the non-packable reference test app, so this phase adds
no third-party payload to product notices.

## Evidence and boundaries

Relative to this session's `files` directory:

- `binding-reference-final-evidence.json`: source hashes, ownership/cycle counts,
  native versions, benchmark commands, negative checks, APK hashes/device results.
- `binding-reference-measurements/`: per-build JSON, logs and binlogs.
- `binding-reference-device/`: identity-tagged process/recreation checks.
- `binding-reference-contract-validation/`: compatible and rejected graph cases.
- `binding-reference-apks/`: preserved optimized APKs.
- `binding-reference-generator-all.log`, `binding-reference-sdk-tests.log`,
  `binding-reference-existing-gradle-tests.log`, and `binding-reference-solution.log`.

The work is an opt-in vertical slice, not a universal replacement binding
product. One foundation owner is intentional. Automatic per-group assembly
materialization, a Roslyn/IDE authoring frontend, fully generic CLR projections,
and universal JVM compatibility checking are not claimed.

All changes remain local and uncommitted. No branch was pushed and no pull
request was created.
