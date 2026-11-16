# Workbench commands

Run from the repository root with Git, .NET SDK **11.0.100**, a .NET 10 runtime
and **JDK 21**. The console runner and its safeguard tests use only the .NET base
class library. `global.json` selects the installed SDK; no command installs a
workload or replaces an installed Android SDK.

Set `JAVA_HOME` to JDK 21. Android compilation also needs an installed .NET 11
Android workload and an Android SDK selected by `ANDROID_HOME` or
`ANDROID_SDK_ROOT`. The validated host is macOS ARM64 / Android workload 37.2.31.

## End-to-end workflow

```sh
dotnet run --project eng/Workbench -- doctor
dotnet run --project eng/Workbench -- verify-preservation
dotnet run --project eng/Workbench -- bootstrap
dotnet run --project eng/Workbench -- build
dotnet run --project eng/Workbench -- test
dotnet run --project eng/Workbench -- pack
dotnet run --project eng/Workbench -- generate
dotnet run --project eng/Workbench -- verify-package --work-dir .artifacts/package-check
```

Bootstrap uses only a shallow fetch of the exact upstream pin, the checked-in
patch and local additions. No external research bundle is required. Host builds
restore dependencies from the upstream public NuGet/Gradle feeds. The large API
documentation submodule and full product preparation are not needed.

| Command | Behavior |
| --- | --- |
| `doctor` | Reports Git, selected/installed SDKs and Java |
| `verify-snapshot` | Verifies all local source hashes and rejects missing/unexpected payload files |
| `verify-preservation` | Also verifies 172 retained research/evidence/input files and the historical asset index |
| `bootstrap` | Fetches the pinned upstream, applies the patch, copies 173 local additions and verifies all 264 source outputs |
| `build` | Builds generator, class-parse, grouped-bindings and binding-compatibility; tests/installs the JVM descriptor |
| `test` | Runs generator, package-task and .NET workbench safeguards |
| `pack` | Requires an unchanged source inventory; builds parser/generator/descriptor, packs the compiler and writes matching provenance |
| `inspect` | Inspects the current local nupkg and writes its content list |
| `generate` | Compiles authored Java, generates a binding and compiles a separate C# consumer |
| `verify-package --work-dir NEW_DIR` | Runs the full no-device C# integration fixture |
| `tool NAME ...` | Discovers a built host tool's evaluated `TargetPath`, then invokes it |

Use a **new source directory** for this snapshot if an older workbench already
created `.artifacts/source`. The runner refuses to overwrite an unrecognized,
nonempty or different-snapshot checkout:

```sh
dotnet run --project eng/Workbench -- --source .artifacts/my-source bootstrap
dotnet run --project eng/Workbench -- --source .artifacts/my-source build
dotnet run --project eng/Workbench -- --source .artifacts/my-source test
dotnet run --project eng/Workbench -- --source .artifacts/my-source pack
```

Use that same `--source` for the fixture and host tools. Repeating bootstrap on
its own completed checkout verifies the pin and hashes without overwriting it.
If an initial fetch/apply fails, preserve the diagnostics and choose a new empty
directory rather than overwriting unknown work.

## Outputs

| Path under ignored `.artifacts/` | Contents |
| --- | --- |
| `source/` or selected source | Materialized upstream and the current source snapshot |
| `feed/` | `0.1.0-preview.workbench.3` compiler and package/source/runner provenance |
| `hello-dotnet/` | Disposable native, binding, consumer and generated output |
| `sample-packages/`, `fixture-packages/` | NuGet caches keyed by actual package SHA-256 |
| `package-contents.txt` | Inspected package entries |
| `tmp/`, `dotnet-tests/` | Temporary and safeguard-test outputs |

Prebuilt `workbench.3` packages and matching provenance are available from the
[current workbench release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/workbench-2026-11-16).
The older `workbench.2` release is historical; do not mix package/provenance
from different snapshots.

Edit `samples/hello/`, not its disposable build copy. The hello and full fixture
check restored compiler bytes against the local nupkg. Repacking under a
development version cannot silently test a stale cached package.

Packing verifies every declared source hash and the complete tracked/untracked
source-change inventory, so its provenance describes the selected snapshot.
Research edits can be built/tested separately, but publishing them needs a new,
reviewed manifest and version rather than reusing an existing package identity.

## Package fixture

Choose a new work directory each run:

```sh
dotnet run --project eng/Workbench -- verify-package \
  --work-dir .artifacts/package-check \
  --jdk "$JAVA_HOME" --android-sdk "$ANDROID_HOME"
```

This checks raw parser facts, descriptor/semantic reports, transforms, native
consumer rules, unchanged-build stability, missing-source regeneration,
metadata/native invalidation, Legacy selection, multi-targeting, design-time
compilation, strict multithreaded tasks, private package isolation and unchanged
installed workload files. It does not run an Android device or app R8/NativeAOT.

## Host tools and other options

```sh
dotnet run --project eng/Workbench -- --dotnet /path/to/installed/dotnet doctor
dotnet run --project eng/Workbench -- generate --jdk "$JAVA_HOME" --android-sdk "$ANDROID_HOME"
dotnet run --project eng/Workbench -- tool class-parse -- --help
dotnet run --project eng/Workbench -- tool binding-compatibility extract \
  .artifacts/full-profile.json /path/to/references /path/to/Microsoft.Maui.dll
dotnet run --project eng/Workbench -- tool binding-compatibility select \
  .artifacts/full-profile.json .artifacts/selected-profile.json \
  /path/to/references /path/to/Frozen.Consumer.dll
```

Host names are `generator`, `class-parse`, `grouped-bindings` and
`binding-compatibility`. Child failures retain their nonzero exit code.
Some original tools report usage with a nonzero exit code; the runner does not
hide it. Advanced native/runtime/MAUI/device paths remain separate; see
[scope and portability](../docs/portability.md).
