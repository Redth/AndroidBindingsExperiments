# .NET-only local commands

Run from the workbench root using Git, .NET SDK **11.0.100**, a .NET 10 runtime,
and **JDK 21**. No Python executable is needed. The C# console project and its
regression-test executable use only the .NET base class library, without extra
NuGet dependencies. `global.json` selects the installed SDK; it installs nothing.

Set `JAVA_HOME` to JDK 21. Android compilation additionally requires an existing
Android SDK (`ANDROID_HOME` or `ANDROID_SDK_ROOT`) and an installed .NET 11
Android workload. macOS ARM64 / Android workload **37.2.31** is the tested path,
not certification of other workload or OS combinations.

## Rebuild

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

`verify-preservation` and bootstrap fetch the
[original-source archive](../docs/python-archive.md) once and check its exact
SHA-256 and every member's bytes. The public download and fresh HTTP bootstrap
have been independently verified with Python blocked. For offline use or an
explicitly selected local archive, provide a verified copy:

```sh
dotnet run --project eng/Workbench -- \
  --archive .artifacts/archives/original-python-sources-6dd45d4.zip \
  verify-preservation
dotnet run --project eng/Workbench -- \
  --source .artifacts/dotnet-source \
  --archive .artifacts/archives/original-python-sources-6dd45d4.zip \
  bootstrap
```

Use the same `--source` selection for subsequent build/test/pack/tool commands.
The default is `.artifacts/source`. An existing original workbench checkout can
be adopted by bootstrap only after its pin and all source hashes verify.
An unrecognized nonempty source directory is never overwritten. Retry a failed
initial bootstrap in a new empty directory rather than deleting user work.

| Command | Behavior |
| --- | --- |
| `doctor` | Reports Git, selected/installed SDKs and Java, with no install |
| `verify-snapshot` | Verifies the original patch/additions across local files and external archive, rejecting unexpected files |
| `verify-preservation` | Also verifies all 190 original curated records, all 33 archived sources and the 18 historical asset checksum records |
| `bootstrap` | Fetches the exact upstream pin, applies original patches, copies all 186 additions and verifies all 277 source outputs |
| `build` | Builds the four host tools and tests/installs the JVM descriptor |
| `test` | Runs generator and package-task host regressions plus the .NET workbench safeguard tests |
| `pack` | Requires an unchanged source inventory, restores/builds parser/generator, builds descriptor, packs/inspects the compiler and writes matching provenance |
| `inspect` | Inspects the current local nupkg and writes its content list |
| `generate` | Builds authored Java, generates an Android binding and compiles a separate C# consumer |
| `verify-package --work-dir NEW_DIR` | Runs the C# no-device package fixture, not the archived Python driver |
| `tool NAME ...` | Discovers a built host tool's evaluated `TargetPath`, then invokes it |

The original **22 Python corpus tests are historical/advanced only** and are
not invoked or counted by the .NET `test` command. The new .NET tests cover
path safety, tampering, exact inventories, CRLF provenance, symlinks, archive
verification, source-overwrite refusal, cached-package bytes and child failures.

Host build/tests and compiler packing do not require `make prepare`,
`dotnet-local.sh`, a prepared product SDK or a previous session's private cache.
They restore pinned dependencies from the upstream public NuGet/Gradle feeds.
They never install or replace globally installed workloads.

## Python-blocked verification

On Unix hosts, add `--block-python` before the command:

```sh
dotnet run --project eng/Workbench -- --block-python doctor
dotnet run --project eng/Workbench -- --block-python test
dotnet run --project eng/Workbench -- --block-python generate
dotnet run --project eng/Workbench -- --block-python verify-package \
  --work-dir .artifacts/python-blocked-package-check
```

Each invocation places fail-fast `python`, `python2`, `python3`, `py`, and
versioned Python shims first in child-process `PATH`. Attempts are logged and
make the command fail. Host installations are untouched. Logs/shims live under
`.artifacts/no-python/`; a successful run reports no Python invocations.
This tests PATH-based invocation, not an OS-wide sandbox against arbitrary
absolute interpreter paths. No supported command asks for such a path.
The .NET tests include a deliberate negative control: requesting `python3`
must reach the shim and fail with 127, without executing a Python interpreter.

## Outputs and options

| Path under `.artifacts/` | Contents |
| --- | --- |
| `source/` or selected source | Exact materialized upstream, including original research scripts as unexecuted data |
| `downloads/` | Verified external source archive cache |
| `feed/` | `0.1.0-preview.workbench.2` compiler and package/source/runner provenance |
| `hello-dotnet/` | Disposable native, binding, consumer and generated output |
| `sample-packages/`, `fixture-packages/` | NuGet caches keyed by actual nupkg SHA-256 |
| `package-contents.txt` | Inspected package entries |
| `tmp/`, `dotnet-tests/`, `no-python/` | Temporary/test/blocking-shim outputs |

Both sample and full fixture verify that the restored compiler nupkg bytes
match the local feed. Repacking under a development version cannot silently
test an older cached package. The package fixture requires a new work directory
and checks late imports, invalidation, Legacy selection, multi-targeting,
design-time compilation, strict MT tasks, consumer rules, private package
isolation and unchanged installed workload files.

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

Host tool names are `generator`, `class-parse`, `grouped-bindings` and
`binding-compatibility`. Child failures retain their nonzero exit code.
Some original tools report usage with a nonzero exit code; the runner does not
hide it. Advanced native/runtime/MAUI/device paths remain separate; see
[portability](../docs/portability.md).
