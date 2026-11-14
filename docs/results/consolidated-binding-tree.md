# Consolidated binding DLL tree

This is one real implementation tree: **162 binding DLLs plus one
Microsoft.Android.Kotlin runtime DLL**, containing **123,154
managed types**. Shared dependencies such as Lifecycle appear once. Dependent
assemblies refer to those same CLR types; there are no alternate self-contained
binding bundles in this package.

## Corpus coverage

All **717 original configuration records** are accounted
for. **672 compatible records** are selected for this modern native
baseline; **45 incompatible native alternatives** are explicitly
listed, with conflict evidence, in `baseline-coverage.json`. They are not silently
dropped and are not DLLs that can be added to this tree. The baseline includes
51 of the 59 Firebase roots and 35 of the 36 ML Kit roots.

The inputs are the pinned android-libraries `config.json` at commit
`2393441675a6d82bb817ab7aa2df698c7ec929ce`, its resolved Maven graph, original
JAR/AAR bytecode, and the official Kotlin metadata/ASM descriptors. The selected
baseline contains 599 native archives.
No handwritten per-library metadata transforms were added to make this tree
compile. This still uses the experimental semantic compiler, its recorded
accessibility/publisher policy and the existing JNI peer emitter.

## One owner, including the Android framework

`ownership.json` is the actual implementation inventory, and
`assembly-dependencies.json` lists dependencies. `verification.json` confirms
unique CLR owners, unique public Java peer owners, unique module identities,
and resolved group-owned type/member references. It also checks referenced
canonical owners: Maven's JSON archive does not redefine the platform's
JSONObject/JSONArray types. Those use Mono.Android, not a second projection.
New non-platform JSON types may still belong to the Maven JSON assembly.

This is not a claim that a newer JVM JSON implementation replaces Android's
boot-classpath implementation or adds its newer members to the platform binding.

## Inspect and reference

All 163 DLLs are in the single `assemblies/` directory, each once.
The DLL payload is 336.7 MiB. Open them directly in
ILSpy or another .NET assembly browser. `examples/` contains the exact consumer
C# used for Activity/Lifecycle/SavedState, Firebase, ML Kit and Health Connect.
Firebase and ML Kit results both use the same Google Task CLR owner.

For compile-time inspection with this experimental .NET 11 Android SDK:

```xml
<Import Project="/path/to/extracted/Consolidated.Bindings.props" />
```

The import references all DLLs as a convenience, not a recommended minimum app
dependency set. A real application must supply a compatible resolved native
graph separately, including the source-built Kotlin runtime bridge. This archive
contains no JARs, AARs, APKs, bootstrap DLLs, reference-only DLLs or production NuGet
packages. CLR reference cycles are retained where the real contracts require them.

Maven-group aggregation still brings broad API dependencies: this representative
probe's actual assembly-reference closure contains 64
complete binding DLLs and 428 native archives. It is
not yet a minimum-per-application projection. The probe honors the native
libraries' minimum SDK and supplies Google's documented sample Ads application
ID so the bundled native initialization provider can start.

## Actual validation and limits

The local SDK built successfully. All 776 generator tests and 21 corpus pipeline
tests passed, all per-group implementations compiled, and the complete-tree C#
consumer compiled. Representative checks using the exact delivered binding DLLs
passed **19/19 in Debug, 19/19 with Release/full trimming/R8, and 19/19 with
NativeAOT**. Evidence, DLL hashes, commands and runtime selection are included.

This is experimental compiler output, not proof that every API in every library
works at runtime. Generated code still produces warnings, and some JVM-only or
otherwise unresolved native types cannot be bound. The native resolution report
and warning counts are included rather than presenting compilation as perfect
coverage. The Kotlin runtime also reports trim/AOT analysis warnings; successful
representative checks are not a blanket AOT-compatibility guarantee.
A separate exploratory ModuleInfo fixture test had a JDK-sensitive
constant-pool count failure; it is recorded in the validation summary.
