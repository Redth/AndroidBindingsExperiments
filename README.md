# Android binding experiments

A team workbench for an experimental .NET Android binding compiler. It brings
together the source changes, research, recorded results, and a way to rebuild
the tools and preview packages without changing an installed Android workload.

This is a research snapshot, not a supported product or an official .NET release.

Start with [what works and what does not](docs/CURRENT-STATE.md), the
[local command guide](eng/README.md), and the
[current .NET-only validation](docs/results/dotnet-workbench-validation.md).

## Run it locally

Install Git, .NET SDK **11.0.100**, a .NET 10 runtime, and JDK 21.
The hello sample additionally needs an installed .NET 11 Android workload and
Android SDK. The commands never install or replace workloads.

```sh
dotnet run --project eng/Workbench -- doctor
dotnet run --project eng/Workbench -- verify-preservation
dotnet run --project eng/Workbench -- bootstrap
dotnet run --project eng/Workbench -- build
dotnet run --project eng/Workbench -- test
dotnet run --project eng/Workbench -- pack
dotnet run --project eng/Workbench -- generate
```

No Python executable or tracked Python files are required. The C# runner uses
only the .NET base class library. Original Python research sources are preserved
in a [checksummed external archive](docs/python-archive.md), not renamed or
stored in an in-git archive. Bootstrap restores their exact bytes only into
ignored materialized upstream source and never executes them.

The first build needs access to public NuGet/Gradle feeds. `bootstrap` downloads
only the pinned upstream tree, not the large API-documentation submodule.
`build`, `test`, and compiler `pack` do not require a prepared product SDK.
`generate` compiles an authored Java class, generates its Android binding with
the rebuilt package, and compiles a separate C# consumer; it does not run an app.

The current compiler package is written to
`.artifacts/feed/Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.2.nupkg`,
with a matching `.provenance.json` containing package and source hashes.
The [.NET-only release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/dotnet-workbench-2026-11-14)
is the download location for `0.1.0-preview.workbench.2`, matching provenance
and source-code downloads from the Python-free C# revision.
The [historical snapshot](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/experiment-snapshot-2026-11-14)
retains the 18 inspection assets, original Python source archive and earlier
`workbench.1` package. Its source-code downloads and history are historical,
not the current Python-free workbench. Large binaries stay out of git; the
[download guide](release-assets/README.md) explains the separate checksums
and scopes.

## What the package looks like

The build-time package is `Microsoft.Android.BindingGenerator.Semantic`.
The preserved historical preview is `0.1.0-preview.1`; this workbench rebuilds
current source as **`0.1.0-preview.workbench.2`** so NuGet cannot confuse it with
either older byte snapshot.
A binding project opts in explicitly:

```xml
<PropertyGroup>
  <AndroidBindingGenerator>Semantic</AndroidBindingGenerator>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Android.BindingGenerator.Semantic"
                    Version="0.1.0-preview.workbench.2"
                    PrivateAssets="all" />
</ItemGroup>
```

`Microsoft.Android.Kotlin` version `0.1.0-preview.1` is a separate runtime
dependency, not a private build dependency. Its native bridge must be built
from source through the Android build's existing Gradle graph.

Ordinary `AndroidLibrary`, `AndroidMavenLibrary`, and `AndroidGradleProject`
items still select native inputs. This experiment does not add a separate
native dependency resolver.

The package path is gated to .NET 11 / Android 37.x. Only macOS ARM64 has been
verified historically. The MAUI compatibility-profile pilot is a compiler CLI
feature; it is not part of the older packaged MSBuild interface.

## Repository map

| Location | Purpose |
| --- | --- |
| `upstream/` | Exact source pins, patches, and browsable experimental additions |
| `eng/` | Local bootstrap, build, generation, packaging, and verification commands |
| `samples/` | Small examples and their native inputs |
| `docs/research/` | Design and investigation material |
| `docs/results/` | Recorded results, with their original limitations |
| `.artifacts/` | Ignored materialized source, build output, and local packages |

The source baseline is
[`dotnet/android@097fd4f7eb6cab27a798e04254ac838c9e8d0369`](https://github.com/dotnet/android/commit/097fd4f7eb6cab27a798e04254ac838c9e8d0369).
The experiment was not committed upstream: the patches and additions are part
of the source snapshot, not optional changes.

The [source map](upstream/README.md) links to the compiler, Kotlin runtime,
ownership emitter, compatibility tooling, and fixtures. The
[research guide and capability inventory](docs/README.md) distinguish runnable
work from historical-only evidence and unfinished ideas.

## Results are not interchangeable

Historical reports cover host tests, preview-package fixtures, MAUI projection
fixtures, and real-device runs. A report copied here is not a new successful
run. Host-tool validation does not establish that an Android app builds or
runs; package inspection does not establish runtime correctness.

The full 717-configuration corpus contains mutually incompatible SDK
alternatives and cannot be treated as one dependency graph. Its older archive
uses group layout; later three-SDK demos use per-Maven-artifact layout.

See the [research guide](docs/README.md) for the evidence and remaining limits.
