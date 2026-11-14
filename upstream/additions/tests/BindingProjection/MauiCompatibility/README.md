# MAUI protected-binding compatibility pilot

This fixture regenerates the real AndroidX SwipeRefreshLayout binding with the
semantic compiler, keeps its old CLR owner, and uses an **unchanged precompiled
consumer** built against the shipped binding. The actual shipped MAUI assemblies
are not rebuilt or rewritten.

The baseline is MAUI `11.0.0-rc.2.26505.5`, including its selected AndroidX
SwipeRefreshLayout `1.2.0.3` NuGet / `1.2.0` native archive. The replacement keeps
`Xamarin.AndroidX.SwipeRefreshLayout, Version=1.0.0.0, PublicKeyToken=null`.

The driver extracts a full profile from all resolved `Microsoft.Maui*.dll`
Android assemblies, then selects obligations using MAUI plus the frozen
consumer's actual compiled usage. It records parameter/signature exposure
separately from member calls, construction, subclasses, overrides, and
interface implementations. Unused legacy overloads need not be restored.
Implicit legacy JNI virtual-dispatch obligations are included even when absent
from ordinary IL member references.

```sh
./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj \
  --filter 'FullyQualifiedName~BindingCompatibilityProfileTests' -nr:false -v:minimal

python3 tests/BindingProjection/MauiCompatibility/verify-pilot.py \
  --work-dir "$SESSION_FILES/maui-compat-check" \
  --stock-dotnet /path/to/stock/dotnet \
  --jdk "$JAVA_HOME" --android-sdk "$ANDROID_HOME" \
  --source /configured/nuget/feed \
  --serial emulator-5594
```

Use a fresh session-owned directory and an explicit owned device. Omit
`--serial` for extraction, generation, compilation and contract validation
without device execution. The driver never chooses the default ADB device or
starts/stops an emulator. Build the official descriptor tool with its documented
`installDist` command first; the repository must already be prepared.

Device runs require **13/13 checks** in Debug, Release/full-trim/R8 and NativeAOT:
real MAUI inheritance/measurement; old-signature round trips and property calls;
Java-to-managed virtual dispatch into a frozen subclass; both frozen callback
interfaces; canonical object identity; and a newly available
`InvokeOnMeasure` API on the same legacy instance. App output hashes must match
the replacement DLL and unchanged consumer. The AOT APK is checked for its
native application library and absence of the CoreCLR JIT library.

`App/compatibility-driver.pro` retains only the test's manually invoked Java
driver, which has no generated binding to describe its JNI calls. It does not
retain the whole binding/native graph or hide missing JNI contracts.
`CompatibilityBaseline=true` builds a diagnostic original-binding control.

The report includes commands/results, profile scope/counts, unchanged MAUI/
consumer hashes, original/replacement hashes and APK hashes. This proves the
selected real MAUI/custom-consumer path, not every AndroidX type or arbitrary
third-party plugin. The regenerated DLL is not a complete replacement NuGet:
the fixture retains the baseline's matching native payloads/resources.
