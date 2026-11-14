# Generator package validation

**Working local preview on an unmodified installed workload.**

- Compiler: `Microsoft.Android.BindingGenerator.Experimental.0.1.0-preview.1.nupkg`
- SHA-256: `3b837384dd899727af559477173a43500611a5e44bdabce7357d389051e0a8d5`
- Environment: macOS ARM64, .NET SDK 11.0.100-rc.2.26504.105, Android workload 37.2.0-rc.2.84, JDK 21, API 35 ARM64.
- Package task suite: **24 passed, 0 failed, 0 skipped**.
- Actual JNI checks: **11/11 Debug, 11/11 full-trim/R8, 11/11 NativeAOT**.
- Installed workload target/tool hashes remained unchanged.
- The final documentation-only repack preserved all 141 tested tool/build-target payloads byte-for-byte.

## Main commands and outcomes

`./dotnet-local.sh test tests/Microsoft.Android.BindingGenerator-Tests/Microsoft.Android.BindingGenerator-Tests.csproj -nr:false -v:minimal` — passed, 24 tests.

`./dotnet-local.sh pack src/Microsoft.Android.BindingGenerator.Experimental/Microsoft.Android.BindingGenerator.Experimental.csproj -c Debug -o <session-files>/generator-package/feed -nr:false -v:quiet` — passed.

`./dotnet-local.sh pack src/Microsoft.Android.Kotlin/Microsoft.Android.Kotlin.csproj -c Debug -o <session-files>/generator-package/feed -nr:false -v:quiet` — passed.

`python3 tests/BindingProjection/GeneratorPackage/verify-package.py --work-dir <session-files>/generator-package/final-package-validation --feed <session-files>/generator-package/feed --upgrade-version 0.1.0-preview.2 --jdk <JDK21> --android-sdk <AndroidSDK> --source https://api.nuget.org/v3/index.json --serial emulator-5594` — passed, 34 command stages plus exact device checks.

`dotnet build Binding/Preview.Binding.csproj -t:Rebuild --no-restore -c Debug -m:2 -mt -nr:false -warnaserror:MSB4286,MSB4287,MSB4288 -p:KotlinProjections=true -p:AndroidSdkDirectory=${ANDROID_HOME} -p:JavaSdkDirectory=${JAVA_HOME} -v:minimal` — passed, zero warnings/errors.

## Pipeline stages

| Stage | Result | Seconds |
| --- | --- | ---: |
| javac | Passed | 0.369 |
| jar | Passed | 0.104 |
| sdk-version | Passed | 2.046 |
| semantic | Passed | 7.43 |
| sdk-paths | Passed | 0.315 |
| incremental | Passed | 1.159 |
| missing-source | Passed | 2.12 |
| metadata-change | Passed | 2.123 |
| native-change | Passed | 2.555 |
| legacy | Passed | 2.351 |
| semantic-again | Passed | 2.365 |
| invalid-mode | Expected XA4350 rejection | 0.916 |
| multitarget | Passed | 6.892 |
| design-time | Passed | 2.034 |
| strict-package-tasks | Passed | 0.447 |
| runtime-reference | Passed | 3.139 |
| package-upgrade | Passed | 3.988 |
| native-runtime | Passed | 7.108 |
| device-ready | Passed | 0.04 |
| device-debug-build | Passed | 14.033 |
| device-debug-install | Passed | 0.692 |
| device-debug-stop | Passed | 0.074 |
| device-debug-clear-log | Passed | 0.134 |
| device-debug-launch | Passed | 0.617 |
| device-release-r8-build | Passed | 25.186 |
| device-release-r8-install | Passed | 0.545 |
| device-release-r8-stop | Passed | 0.068 |
| device-release-r8-clear-log | Passed | 0.112 |
| device-release-r8-launch | Passed | 0.167 |
| device-nativeaot-build | Passed | 22.414 |
| device-nativeaot-install | Passed | 0.214 |
| device-nativeaot-stop | Passed | 0.045 |
| device-nativeaot-clear-log | Passed | 0.107 |
| device-nativeaot-launch | Passed | 0.13 |

Exact argument arrays, APK hashes, workload hashes and the original tested archive hash are preserved in `results.json`.

## Important findings and limits

The stock SDK can accept the package's late-imported parser/descriptor/generation pipeline without replacing workload files or using fork-only task parameters.

R8 initially removed a getter invoked only by a typed view. The fix is shipped in the compiler package, not hidden in test-only rules: the SDK binding AAR exports retention for locally generated peer classes. This deliberately retains public/protected native members more conservatively than a future projection-aware app linker.

The Kotlin runtime is separate and flows to consumers; its native bridge is source-built in the existing Gradle graph. No descriptor/tool JARs become app payloads.

The NativeAOT APK was checked for the native application library and absence of the CoreCLR JIT library. Compilation alone was not treated as runtime success.

The compiler is project-local. It does not rewrite precompiled dependencies, change CLR ownership, or resolve incompatible native graphs. Windows/Linux and other workload revisions remain unverified.

The owned read-only emulator was stopped; the unrelated emulator-5554 was not modified.
