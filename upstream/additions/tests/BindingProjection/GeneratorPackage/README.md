# Stock-workload compiler package fixture

This fixture deliberately runs outside the repository's SDK imports against an
**unmodified installed .NET for Android workload**. The compiler is selected only
by a private package reference and `AndroidBindingGenerator=Semantic`.

First build the descriptor and local compiler/runtime packages, as documented
in `src/Microsoft.Android.BindingGenerator.Semantic/README.md`. For example:

```sh
./dotnet-local.sh test \
  tests/Microsoft.Android.BindingGenerator-Tests/Microsoft.Android.BindingGenerator-Tests.csproj \
  -nr:false -v:minimal
./dotnet-local.sh pack src/Microsoft.Android.Kotlin/Microsoft.Android.Kotlin.csproj \
  -c Debug -o "$FEED" -nr:false
./dotnet-local.sh pack \
  src/Microsoft.Android.BindingGenerator.Semantic/Microsoft.Android.BindingGenerator.Semantic.csproj \
  -c Debug -o "$FEED" -nr:false
```

Use a fresh directory under the session's files/artifact directory, not `/tmp`,
and a `dotnet` host with an installed .NET 11 Android workload:

```sh
python3 tests/BindingProjection/GeneratorPackage/verify-package.py \
  --work-dir "$SESSION_FILES/generator-package-check" \
  --feed "$FEED" --dotnet /path/to/stock/dotnet \
  --jdk "$JAVA_HOME" --android-sdk "$ANDROID_HOME" \
  --source /configured/nuget/feed
```

`--source` may be repeated for the configured feeds needed by framework/runtime
packages. `--upgrade-version 0.1.0-preview.2` also verifies a second locally
packed compiler version; pack it with `-p:PackageVersion=0.1.0-preview.2`.

For device checks, add an **explicit owned device** with `--serial`, for example
`--serial emulator-5594 --rid android-arm64`. The driver never selects the default
ADB device or starts/stops an emulator itself. It installs only the fixture's
package, `net.dot.android.packagetest.app`.

## Coverage

- Package files, private tooling and absence of transitive compiler selection.
- Raw class-parse facts plus the packaged official descriptor.
- Metadata/namespace transforms and handwritten additions.
- No-op builds skip every compiler tool without rewriting outputs.
- A missing generated source regenerates; metadata/native changes invalidate
  the relevant stage; Legacy/Semantic toggles work without a manual clean.
- Android/non-Android multi-targeting and design-time compilation.
- Strict `-m -mt -nr:false -warnaserror:MSB4286,MSB4287,MSB4288` package tasks.
- Optional Kotlin runtime reaches downstream consumers; build tooling does not.
- Scoped JNI retention rules reach the SDK-generated binding AAR.
- Optional device runs require **11/11** checks in Debug, full-trim/R8 and
  NativeAOT. Checks cover real native calls, generic reads/writes, nulls,
  canonical peer borrowing, managed callbacks and Java exceptions.

The native runtime is source-built from the restored Kotlin package as a child
of the fixture's ordinary Gradle graph. Gradle writes into the fixture's build
directory, not the package cache. This is sample native integration, not a
replacement Maven resolver.

The driver records exact commands, outcomes, timings, workload-file hashes and
APK hashes in `results.json`. It checks that R8 really ran and that the NativeAOT
APK contains the native application rather than the JIT runtime.

Validated initially on macOS ARM64, .NET SDK `11.0.100-rc.2.26504.105`, Android
workload `37.2.0-rc.2.84`, JDK 21 and an API 35 ARM64 emulator. Windows/Linux and
other workload revisions are not claimed validated by this result.
