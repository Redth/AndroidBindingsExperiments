# Generator package validation

**Working local preview on an unmodified installed workload.**

- Compiler: `Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.1.nupkg`
- SHA-256: `05f35c9eac213c4c48271158c111ef0ef9a737ec3abea1170be30f745c4d3268`
- Environment: macOS ARM64, .NET SDK 11.0.100-rc.2.26504.105, Android workload 37.2.0-rc.2.84, JDK 21, API 35 ARM64.
- Package task suite: **24 passed, 0 failed, 0 skipped**.
- Previous actual JNI checks: **11/11 Debug, 11/11 full-trim/R8, 11/11 NativeAOT**.
- Installed workload target/tool hashes remained unchanged.
- After renaming, 24 task tests and the 17-stage stock-workload fixture passed.
- Generator/parser/descriptor payloads and the Kotlin runtime package are byte-identical to the prior JNI-validated preview; device tests were not repeated for this build-tool/package rename.

## Main commands and outcomes

`./dotnet-local.sh test tests/Microsoft.Android.BindingGenerator-Tests/Microsoft.Android.BindingGenerator-Tests.csproj -nr:false -v:minimal` — passed, 24 tests.

`./dotnet-local.sh pack src/Microsoft.Android.BindingGenerator.Semantic/Microsoft.Android.BindingGenerator.Semantic.csproj -c Debug -o <session-files>/generator-package/feed -nr:false -v:quiet` — passed.

`Microsoft.Android.Kotlin.0.1.0-preview.1.nupkg` — unchanged from the previously validated runtime.

`python3 tests/BindingProjection/GeneratorPackage/verify-package.py --work-dir <session-files>/generator-package/semantic-package-validation --feed <session-files>/generator-package/feed --upgrade-version 0.1.0-preview.2 --jdk <JDK21> --android-sdk <AndroidSDK> --source https://api.nuget.org/v3/index.json` — passed, 17 command stages including strict MT package tasks.

## Pipeline stages

| Stage | Result | Seconds |
| --- | --- | ---: |
| javac | Passed | 0.371 |
| jar | Passed | 0.099 |
| sdk-version | Passed | 2.059 |
| semantic | Passed | 3.649 |
| sdk-paths | Passed | 0.325 |
| incremental | Passed | 0.986 |
| missing-source | Passed | 2.069 |
| metadata-change | Passed | 2.087 |
| native-change | Passed | 2.367 |
| legacy | Passed | 2.302 |
| semantic-again | Passed | 2.671 |
| invalid-mode | Expected XA4350 rejection | 0.934 |
| multitarget | Passed | 4.927 |
| design-time | Passed | 3.871 |
| strict-package-tasks | Passed | 0.517 |
| runtime-reference | Passed | 3.602 |
| package-upgrade | Passed | 3.505 |

Exact rename-validation argument arrays and workload hashes are preserved in `results.json`; prior JNI evidence and APK hashes are labeled separately.

## Important findings and limits

The stock SDK can accept the package's late-imported parser/descriptor/generation pipeline without replacing workload files or using fork-only task parameters.

R8 initially removed a getter invoked only by a typed view. The fix is shipped in the compiler package, not hidden in test-only rules: the SDK binding AAR exports retention for locally generated peer classes. This deliberately retains public/protected native members more conservatively than a future projection-aware app linker.

The Kotlin runtime is separate and flows to consumers; its native bridge is source-built in the existing Gradle graph. No descriptor/tool JARs become app payloads.

The NativeAOT APK was checked for the native application library and absence of the CoreCLR JIT library. Compilation alone was not treated as runtime success.

The compiler is project-local. It does not rewrite precompiled dependencies, change CLR ownership, or resolve incompatible native graphs. Windows/Linux and other workload revisions remain unverified.

The owned read-only emulator was stopped; the unrelated emulator-5554 was not modified.
