# Experimental JVM API descriptor

A build-time Kotlin frontend for the semantic binding compiler. It uses
`kotlin-metadata-jvm` 2.4.21 and ASM 9.8 to inspect compiled classes without
loading or executing the target library. Kotlin describes the native contract;
C# remains responsible for CLR assembly ownership, names, projection, and JNI
marshaling.

## Build and use

From the repository root, with `JAVA_HOME` pointing to JDK 21:

```sh
bash build-tools/gradle/gradlew --no-daemon --console plain \
    -p external/Java.Interop/tools/jvm-api-descriptor test installDist

external/Java.Interop/tools/jvm-api-descriptor/build/install/jvm-api-descriptor/bin/jvm-api-descriptor \
    --archive /absolute/path/to/library.aar \
    --archive /absolute/path/to/dependency.jar \
    --output /absolute/path/to/api-descriptor.json
```

`--archive` may be repeated. JARs and an AAR's `classes.jar`/`libs/*.jar` are
inspected. Alternatively, `--manifest` accepts a JSON array with `file` and
optional `group`, `artifact`, and `version` fields per archive. Repeated paths
are deduplicated; duplicate binary class names across distinct inputs fail
explicitly. A metadata-only archive may legitimately contain no classes.
When the manifest supplies `sha256`, the archive must still match that hash.

For whole-corpus auditing, use `--output-directory DIRECTORY` instead of
`--output FILE`. Each archive receives a separate descriptor with its original
coordinate and hashes; `index.json` is written atomically only after every input
succeeds. This preserves conflicting native definitions for analysis without
merging them or claiming they can coexist in one Android classpath. The index
records descriptor hashes, and the C# catalog checks exact native-manifest
coverage and provenance before applying the shared compiler policy. Failed
batch extraction removes the old success index.

Pass the output to the existing generator together with its unadjusted
class-parse input:

```text
--experimental-semantic-model
--experimental-api-descriptor=/absolute/path/to/api-descriptor.json
```

The importer verifies physical binary slots before applying semantic facts.
The existing bytecode frontend still supplies some Kotlin helper/ABI lowering;
this is not a wholesale replacement of class-parse or the C# emitter.
[`tests/BindingProjection`](../../../../tests/BindingProjection/README.md)
demonstrates ordinary Gradle/MSBuild integration and typed generic views.

## Contract and version policy

Schema version 1 records archive/class hashes, physical access flags and JVM
descriptors, annotations/type annotations, nesting/hierarchy, Java signature
trees, and official Kotlin source visibility and type contracts. Bridge facts
include bytecode invocation targets, so overload
adaptation is not inferred merely from a shared method name and argument count.
Kotlin type facts
include scoped type-parameter IDs/bounds/variance, nullability, outer/flexible
types, suspend/default declarations, properties/accessors, and value classes.
Multifile facades inherit the source contracts of their verified part classes;
private declarations are not confused with public facade slots. Missing or
unsupported part metadata marks the facade unverified.
Arrays retain primitive versus reference representation. Nonfinite numeric
constants are tagged with their raw bits rather than written as invalid JSON.
String constants containing unpaired UTF-16 surrogates are tagged with their
code units, preserving the value without producing invalid UTF-8/JSON.

The reader tries strict metadata decoding first. A lenient result is explicitly
marked, not promoted to a confidently supported contract. C# strips guessed
semantic conveniences for unsupported/lenient input and reports unverified
facts while retaining the raw JVM path. Reader acceptance is not a guarantee
that every Kotlin feature has a rich C# projection.

Output is deterministically ordered, written atomically, and removed before a
failed regeneration can leave a stale success-shaped descriptor. The output
cannot overwrite an input archive.

## Isolation

The tool has its own Gradle runtime classpath. ASM, Gson, and the Kotlin metadata
reader must not enter the app's resolved native dependency graph, DEX, or NuGet
runtime payload. When included as a child project, outputs live under the
consuming root build directory rather than the tool source directory. The
surrounding Gradle settings provide repositories.

JVM tests exercise real Kotlin/Java fixtures, deterministic/stale output,
generic scopes and variance, suspend/value/default metadata, array
representation, and nonfinite constants. C# importer and projection regressions
live in `external/Java.Interop/tests/generator-Tests`.
