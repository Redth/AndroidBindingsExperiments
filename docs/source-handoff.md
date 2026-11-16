# Android binding experiments: export notes

Historical export context: counts and automation paths below describe the
original handoff. See [the current source snapshot](../upstream/README.md) and
[snapshot change record](../provenance/snapshot-change.json) for today's inputs.

This export is the durable substance of a long experimental `dotnet/android`
worktree. It is not a new supported Android SDK, a NuGet.org release, or evidence
that all experiments were revalidated on the handoff date.

## Exact source reconstruction

`upstream/manifest.json` pins `https://github.com/dotnet/android.git` to
`097fd4f7eb6cab27a798e04254ac838c9e8d0369`. Apply
`upstream/patches/android.patch` at that checkout root, then copy all
`upstream/additions/` files to their recorded paths and modes. The manifest
records SHA-256 for the patch, each added file and each tracked post-patch file.
There are 91 tracked changes and 186 substantive additions.

`external/Java.Interop` is vendored at this revision, **not a submodule**.
The only submodule is the Android API documentation; its commit is recorded,
but the host compiler/parser/descriptor/emitter/test path does not need it.

The source delta is complete, not just the generator package. In particular:

| Area | Source |
| --- | --- |
| Semantic compiler/JVM policy/typed generic views | `external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.Semantics/` plus tracked emitter/importer/object-model changes |
| Kotlin class-file lowering | `external/Java.Interop/src/Xamarin.Android.Tools.Bytecode/` |
| Official Kotlin metadata/ASM descriptor | `external/Java.Interop/tools/jvm-api-descriptor/` |
| CLR owner registry and real artifact implementation splitting | `external/Java.Interop/tools/grouped-bindings/` |
| Compiled full and usage-selected MAUI contract profiles | `external/Java.Interop/tools/binding-compatibility/` and `BindingCompatibility*.cs` |
| Project-local compiler NuGet | `src/Microsoft.Android.BindingGenerator.Semantic/` |
| Shared projection/generic/Task/Flow runtime and native bridge source | `src/Microsoft.Android.Kotlin/` |
| Runnable reference and corpus fixtures | `tests/BindingProjection/` |
| Native peer/view identity and callbacks | `tests/Mono.Android-Tests/SharedPeerViews/`, `SharedPeerViewTests.cs`, authored Java fixture |
| Regression tests | generator, bytecode, package task tests and corpus Python tests |

Use the exact original patch as the preserved research source. Keep necessary
workbench portability/build fixes explicitly separated so reviewers can see
what changed for the runnable handoff.

## Build constraints to respect

The upstream `global.json` does not pin an SDK version; `eng/Versions.props`
pins the full local SDK build to `12.0.100-alpha.1.26523.103`. The compiler/parser
host projects independently target `net10.0`. `external/Java.Interop.override.props`
imports the parent product settings/output locations; inspect those before
assuming a plain host build uses a conventional `bin/Debug/net10.0` path.

`dotnet-local.sh` only works after provisioning a local product SDK; it searches
Release before Debug. Do not copy the creator's built local SDK or require it
for basic host tooling. The workbench should use an explicitly selected
installed .NET SDK for host tooling and separately document full runtime builds.

The package tasks require modern MSBuild with `TaskEnvironment`; initial
stock-workload tests used SDK `11.0.100-rc.2.26504.105`, workload
`37.2.0-rc.2.84`, JDK 21, macOS ARM64. JDK21 and the prebuilt official descriptor
distribution are required when packing the compiler. To build the latter:

```sh
bash build-tools/gradle/gradlew --no-daemon --console plain \
  -p external/Java.Interop/tools/jvm-api-descriptor test installDist
```

Previous prepared-checkout commands (not a substitute for new bootstrap tests):

```sh
dotnet build external/Java.Interop/tools/generator/generator.csproj
dotnet build external/Java.Interop/tools/class-parse/class-parse.csproj
dotnet build external/Java.Interop/tools/grouped-bindings/grouped-bindings.csproj
dotnet build external/Java.Interop/tools/binding-compatibility/binding-compatibility.csproj
dotnet test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj
dotnet test tests/Microsoft.Android.BindingGenerator-Tests/Microsoft.Android.BindingGenerator-Tests.csproj
dotnet pack src/Microsoft.Android.BindingGenerator.Semantic/Microsoft.Android.BindingGenerator.Semantic.csproj \
  -c Debug -o /chosen/local-feed -nr:false
```

Use `-getProperty:TargetPath` or evaluated metadata for tool output discovery.
The existing test `GroupedAssemblyTests` includes generated tiny Java fixtures,
cyclic CLR implementations, canonical registry checks and executable IL tests.
A simple first sample can compile an authored Java class with `javac`, run
class-parse, generate semantic bindings against the installed Android reference
assemblies, then compile a downstream C# consumer.

## Product and version boundaries

- Keep package name `Microsoft.Android.BindingGenerator.Semantic`.
- User surface: project-private `PackageReference` plus
  `AndroidBindingGenerator=Semantic`; `Legacy` restores the stock generator.
- Native input selection remains `AndroidLibrary`, `AndroidMavenLibrary`,
  `AndroidGradleProject` and the existing Gradle dependency integration.
- `Microsoft.Android.Kotlin` is a separate normal runtime dependency for rich
  generics/Kotlin conveniences. Its native bridge is source-built once in the
  app's own selected graph, not shipped as vendor JAR/AAR payload.
- Preserve precise same-Java-type CLR ownership. New views are ordinary CLR
  views, not duplicate registered JNI peers.
- Artifact layout is now the corpus driver's default. The older complete
  consolidated archive uses **group** layout. Do not relabel or merge them.
- Separate incompatible native profiles stay separate. No all-SDK graph is
  promised.
- MAUI full/usage-selected preservation is a CLI pilot. The historical
  `0.1.0-preview.1` package artifacts predate this latest profile code; do not
  imply a ready MSBuild profile switch exists in them.

## Evidence and preservation

`docs/` contains the narrative research/results; some early recommendations
were superseded. The team README should point to the current recommendation and
provide a chronology rather than imply every early design is implemented.
Machine paths in copied standalone text were replaced by placeholders.
They describe historical runs, not commands to paste.

`inputs/android-libraries-config.json` is the complete 717-record
`dotnet/android-libraries` snapshot at
`2393441675a6d82bb817ab7aa2df698c7ec929ce`.
Its selected native graphs, relocations and reviewed Android/JVM module aliases
are recorded under `evidence/`. Regenerate executable local paths through the
resolver rather than interpreting the placeholder paths as existing files.

`historical/session-helpers/` preserves authored exploratory helpers that were
not yet checked into the original source. They are **not turnkey entrypoints**.
Several refer to old retained corpus directories. Preserve useful logic but
either port it or clearly classify it as historical. The pinned third-party
configs are independently preserved under `evidence/third-party/`.

`release-assets/assets.json` and `SHA256SUMS` enumerate public historical
archives and locally tested packages. Narrative/evidence paths were normalized
to placeholders, and the one raw logcat file was filtered to probe/crash lines.
`public-archive-audit.json` records original/published archive hashes and each
text change. Every executable/generated-code payload hash was preserved.
The standalone NuGets and the giant generated-source-only archive remain
byte-identical. Large generated binaries/source snapshots belong in a release,
not ordinary git history. Historical command arrays are not turnkey commands.

The historical validation results include:

- 813 generator tests, including14 compatibility-profile regressions.
- 24 package task tests; 17-stage renamed package stock-workload fixture.
- Package JNI11/11 per Debug, full-trim/R8 and NativeAOT.
- MAUI unchanged-binary selective contract JNI13/13 in each of those modes.
- Full717-record input coverage; compatible consolidated672 roots with45
  incompatible alternatives; all59 Firebase roots accounted for.
- RevenueCat9 Android modules plus Mapbox/Stripe/Coil/networking independent
  profiles; later MapLibre OpenGL/Sentry/Koin complete per-artifact graphs.

No purchases, telemetry uploads, token-based map rendering or service
credentials were used. MapLibre default Vulkan failed on a no-Vulkan software
emulator; same-version OpenGL passed and produced verified snapshot pixels.

## What this export deliberately excludes

NuGet/Gradle caches, emulator homes/temp data, bin/obj/build intermediates, raw
full logcat (which can include emulator authentication public keys), repeated
build attempts, screenshots of unrelated apps, and obsolete generated copies.
The source manifest lists the only excluded untracked code-tree items:
Python bytecode and a Gradle problems report. Original published attachments
remain in the source session until the repository/release is verified remotely.
