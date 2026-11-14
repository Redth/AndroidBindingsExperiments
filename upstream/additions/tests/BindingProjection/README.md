# Binding projection reference application

An opt-in, ordinary multi-project Android application for the experimental
semantic binding compiler. It uses real Activity, Lifecycle, and Preferences
DataStore libraries from one locked Gradle graph. No published AndroidX or
Kotlin managed binding packages are referenced.

The application has an increment button and observes the persisted counter
through typed Flow. Its optional verification mode additionally checks Activity
recreation, cancellation cleanup, cross-library callbacks, direct C# use of the
generated DataStore/Preferences APIs, generic projections, and persistence
across process restarts.

## Projects and ownership

- **Foundation** regenerates the native graph with semantic/Kotlin conveniences.
  `Reference.Foundation` is the explicit canonical owner for this pilot. The
  optional `Microsoft.Android.Kotlin` runtime remains a separate owner.
- **Reader** and **Writer** compile independently. Each requests a different
  minimal internal `Reference.Local.SettingsView` through its `view.json`.
  The views retain the same canonical repository peer; they do not register
  another JNI peer, transfer ownership, or claim CLR type equivalence.
- **App** consumes both libraries, passes canonical peers and callbacks between
  them, and validates their embedded native requirements against the selected
  native graph before compilation.
- **Native** is a source-built Gradle module using real DataStore business logic,
  plus the canonical Kotlin runtime source project. The app also exercises
  generated DataStore/Preferences interfaces directly rather than only testing
  the authored repository facade.

The build also runs the isolated
[`jvm-api-descriptor`](../../external/Java.Interop/tools/jvm-api-descriptor/README.md)
tool over the resolved archives. Official Kotlin metadata and ASM provide source
visibility, annotations, JVM signatures, and generic contracts to the C#
compiler. The tool's ASM/Gson/metadata dependencies are not app dependencies.
Foundation enables supported-API filtering and typed generic views; bytecode
facts and hidden implementation slots remain available for JNI correctness.
No library-specific metadata transforms are required by this fixture.

The single foundation owner is deliberate for this pilot. The generated
ownership report also records Maven provenance and suggested cycle-safe
partitions. The current graph has 23 groups and 22 partitions, including the
Lifecycle/SavedState cycle. It does **not** emit 22 foundation assemblies.

## Build and run

Prepare/build the local Android SDK and compiler tools using the repository
workflow first. Set `JAVA_HOME` to JDK 21 and `ANDROID_HOME` to the Android SDK.
Use an isolated `NUGET_PACKAGES` cache when running alongside other sessions.
The native compile fixture currently uses the installed Android 35 platform.

From the repository root:

```sh
./dotnet-local.sh build tests/BindingProjection/Reference.slnx -c Debug -nr:false

./dotnet-local.sh build tests/BindingProjection/App/Reference.App.csproj \
    -t:Install -c Debug -nr:false -p:AdbTarget="-s emulator-5570"
```

The app defaults to `android-arm64`; override `RuntimeIdentifier` for a different
device. Normal launches show the interactive counter. Binding generation is
part of the project build, not a Python comparison step.

The native graph is authoritative. The experimental private
`_AndroidUseResolvedNativeGraph` switch suppresses automatic discovery of AARs
beside managed assemblies; the app imports only the selected native outputs.
Do not enable it in an app that still relies on such adjacent AAR discovery.

## Explicit minimal views

For example, Reader selects one operation:

```json
{
  "SchemaVersion": 1,
  "Views": [{
    "JavaType": "net/dot/android/reference/SettingsRepository",
    "PeerType": "Net.Dot.Android.Reference.SettingsRepository",
    "OwnerAssembly": "Reference.Foundation",
    "Namespace": "Reference.Local",
    "Name": "SettingsView",
    "Members": [{
      "JavaName": "snapshots",
      "ManagedMember": "SnapshotsAsAsyncEnumerable",
      "Name": "Observe"
    }]
  }]
}
```

The compiler resolves the native descriptor from the immutable class-parse
input, verifies the canonical CLR owner/member, and emits a plain internal C#
facade. An exact `JniSignature` is required only when the selected Java member
is overloaded. `ManagedMember` explicitly selects the canonical raw or
convenience method; this is not yet a C# attribute/Roslyn authoring frontend.

The full native index is shared through Foundation. Reader/Writer do not scan
JARs inside Roslyn, regenerate Kotlin owners, or project unused members.
Each library embeds `Java.Interop.NativeRequirements.json`, including its
required owner, JNI slot, invocation kind, and available Kotlin return contract.
Final-app validation rejects missing slots or conflicting owners before
packaging; it is not an exhaustive Java behavioral/binary-compatibility checker.

## Native dependency upgrades

Change `dataStoreVersion` in `Native/gradle.properties`, update the native lock,
and build normally:

```sh
tests/BindingProjection/Native/gradlew nativeInputs --write-locks
./dotnet-local.sh build tests/BindingProjection/App/Reference.App.csproj \
    -c Debug -nr:false
```

The pilot was upgraded from DataStore 1.2.0 to 1.2.1 without binding metadata or
API XML edits. Generated native requirements and ownership stay explicit.
Lockfiles and version catalogs are tracked as Gradle inputs, while build/cache
directories are excluded.

## Device verification

Build the APK normally, then run the verification-only driver:

```sh
python3 tests/BindingProjection/verify-reference.py \
    --apk tests/BindingProjection/App/bin/Debug/net11.0-android/android-arm64/net.dot.android.bindingreference-Signed.apk \
    --device-serial emulator-5570 --configuration Debug \
    --output /absolute/path/to/evidence
```

It performs two launches separated by a process stop. Each launch must pass
10 workflow checks and 26 generic checks, including an Activity recreation.
The generic checks exercise strings, boxed primitives, nullable arguments,
class/method/inherited generics, nested collections, primitive/reference arrays,
erased bounds, safe type failures, typed fields, managed callbacks, generic
suspend/Flow results, and canonical peer identity/disposal. The second process must
read the first process's committed DataStore value. Identity-tagged logs and
the APK hash are saved.

The generic checks also cover projection-aware native record bounds. Raw peers,
typed views and managed callback peers share `IJavaProjection<TPeer>` without
registering additional peer owners. Constrained class/method arguments retain
their canonical peer identity; explicitly named mapped views support custom
converters while still rejecting native types outside the declared bound.

Repeat with `-c Release` for full trimming/R8 and with
`-c Release -p:PublishAot=true` for NativeAOT. The driver neither boots nor stops
the device, and does not uninstall the application.

## Incremental behavior

Unchanged builds must preserve the shared native index, canonical source, and
both minimal-view sources. A C#-only change in Writer must not regenerate them.
Native graph changes must invalidate the affected outputs and rerun final
requirements validation.

The reference workflow uncovered and repaired Gradle subproject output
collisions, source staging that was never up to date, and RID publish discovery
overwriting the outer compiler cache with a partial binding-source list.
Measurements should distinguish warm dependency caches from a genuinely fresh
dependency download.

SDK/compiler settings used here are experimental private inputs, not a stable
public API commitment. Existing binding generation and adjacent-AAR behavior
remain the defaults for other projects.
