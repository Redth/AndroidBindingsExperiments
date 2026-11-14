# MAUI protected-binding pilot

**The selective compatibility approach works for a real MAUI/custom-consumer path.**

The pilot regenerated AndroidX SwipeRefreshLayout from the native archive using
the semantic compiler. It kept the original managed owner and used the shipped
MAUI assemblies plus a consumer compiled once against the old binding. Neither
MAUI nor that frozen consumer was rebuilt when the replacement was tested.

## What was extracted

Baseline: MAUI `11.0.0-rc.2.26505.5`, all five resolved `Microsoft.Maui*.dll`
Android assemblies; AndroidX SwipeRefreshLayout package `1.2.0.3`, native
artifact `1.2.0`.

- **64** distinct AndroidX/Material types exposed at MAUI's public/protected boundary.
- **588** types across **26** binding owners after following the public/protected contract.
- **10,128** entries in the full contract.
- **1,248** selected entries for the listed compiled MAUI/custom consumers.

The selection records identity/signature exposure, member calls, construction,
subclassing, overrides, and interface implementation separately. A type merely
appearing in a parameter does not automatically pin every old constructor or
overload. These counts describe compatibility obligations, not the size of the
generated modern API.

## The actual regenerated set

The pilot protects `SwipeRefreshLayout`, `IOnChildScrollUpCallback`, and
`IOnRefreshListener`. Its owner remains:

```text
Xamarin.AndroidX.SwipeRefreshLayout, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
```

The selected profile pins **25 of the class's 61 entries**, plus one method in
each callback interface. It deliberately omits unused old int-to-enum overloads.
Concrete modern additions are allowed: the new caller uses `InvokeOnMeasure`
on the very same object created by the old consumer. The optional full-contract
regeneration also compiles and reports zero contract differences for the three
protected types; that fallback was host-validated, not separately device-tested.

## Proof

| Mode | Checks | Host |
| --- | ---: | --- |
| Debug | 13/13 | Unmodified .NET 11 Android workload |
| Full-trim + R8 | 13/13 | Unmodified .NET 11 Android workload |
| NativeAOT + R8 | 13/13 | Unmodified .NET 11 Android workload |

The tests exercise the real shipped `MauiSwipeRefreshLayout` hierarchy, old
parameter/property signatures, MAUI's measurement override, Java virtual
dispatch into a frozen custom subclass, both frozen callback implementations,
canonical instance identity, original owner identity, and a new additive API.

The app's binding/consumer input DLL hashes are checked, and unchanged hashes
are recorded for the shipped MAUI assemblies and the frozen consumer. The
replacement hash differs from the original binding. R8 execution and a native
AOT application library without the CoreCLR JIT library are explicitly checked.

## Important finding: static member references are not enough

An initial usage-selected profile passed the structural comparison but failed
NativeAOT during legacy virtual dispatch. The original-binding control passed.
The missing obligations were the legacy `ThresholdClass`, `ThresholdType` and
`JniPeerMembers` properties used indirectly by `XAPeerMembers`.

The selector now retains that infrastructure for constructed/subclassed peers
and records why. This is a real example of being selective without assuming
that absence of an ordinary IL call proves a member is unnecessary.

The only test-specific native keep rule preserves the manually invoked Java
driver, which has no generated binding to describe its JNI calls. It does not
keep all AndroidX types or mask a binding API mismatch.

## Commands and outcomes

```sh
./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj -nr:false -v:minimal
```

Passed: **813**, failed: **0**, skipped: **0**. This includes 14 profile tests
covering removed members, owner/JNI/virtual changes, generic constraints, safe
additions, unsafe abstract additions, selective use and implicit dispatch.

```sh
make all
```

Passed, zero errors. Existing SourceLink repository warnings remain.

```sh
python3 tests/BindingProjection/MauiCompatibility/verify-pilot.py \
  --work-dir <session-files>/maui-protected-delivery \
  --stock-dotnet <installed-dotnet> --jdk <JDK21> \
  --android-sdk <AndroidSDK> \
  --source <configured-cache-or-feed> --source https://api.nuget.org/v3/index.json \
  --serial emulator-5594
```

Passed all recorded command stages and the three exact 13-check device runs.
`results.json` contains the exact executed argument arrays, input/assembly
hashes and APK hashes.

## Boundaries

This is a bounded compiler pilot, not a complete MAUI/AndroidX migration.
The selective guarantee covers listed compiled consumers, not arbitrary future
plugins, reflection, or subclasses. Add those consumers/requirements to the
profile or use a fuller compatibility contract.

The profile currently covers AndroidX/Material owners, not every third-party
dependency exposed by any MAUI package. The generator restores the pilot's
names/slots/virtual contracts and infrastructure; arbitrary historical generic
or handwritten API reconstruction is not implemented. Compiled verification
is mandatory before delivering another regenerated set.

The regenerated DLL is not a standalone replacement NuGet. The test retains
the matching baseline native libraries/resources, and does not prove a native
version upgrade or general binary-payload reconciliation. There are no vendor
JAR/AAR files in this evidence archive.

The prototype uses the compiler's
`--experimental-compatibility-profile` CLI option plus the compiled verifier.
It is **not yet wired as a new public profile property in the earlier
Microsoft.Android.BindingGenerator.Semantic preview package**.

Validated on macOS ARM64, stock .NET SDK `11.0.100-rc.2.26504.105`, Android
workload `37.2.0-rc.2.84`, JDK 21, API 35 ARM64 emulator. The owned emulator was
stopped; the unrelated emulator was not modified.
