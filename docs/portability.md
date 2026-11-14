# Portable commands and advanced paths

Current supported commands are the BCL-only C# runner under `eng/Workbench/`,
its .NET safeguard tests, and the authored hello sample. No Python executable
is required and no Python files are tracked.

The original source/curated manifests are unchanged. Their Python entries are
resolved through the [explicit external archive mapping](python-archive.md).
The materialized source may naturally contain upstream Python scripts, but
supported commands do not execute them. The .NET no-device integration fixture
replaces the old Python package driver.

| Location | Classification |
| --- | --- |
| `eng/Workbench/` | Current .NET bootstrap, preservation, build/test/pack, tool discovery, sample and full no-device fixture |
| `eng/Workbench.Tests/` | Current .NET path/hash/inventory/archive/process/cache safeguards |
| `samples/hello/` | Current native/binding/downstream compile sample |
| Original Python corpus tests | Archived historical coverage, not rerun by the .NET `test` command |
| Original native/corpus/MAUI/device Python drivers | Archived advanced prepared-product-SDK research, not supported Python-free commands |
| Historical Python helpers and former runner | External archive only; retained for research/provenance |

The runner derives paths from the repository, uses `ProcessStartInfo.ArgumentList`,
checks declared source/archive hashes and keeps generated output under ignored
`.artifacts/`. It does not depend on a creator's session or private cache.
Normal public NuGet/Gradle restore caches may accelerate a build. Cached compiler
packages are keyed by nupkg SHA and their bytes are checked against the feed.

`--block-python` uses per-invocation fail-fast PATH shims on Unix without
changing host installations. It is not an OS sandbox against arbitrary absolute
interpreter paths. Current commands never request an interpreter.

## Advanced host tools and native work

The current host `tool` command asks MSBuild for each built tool's actual
`TargetPath`. The compatibility CLI can extract/select/verify compiled profiles
with explicit resolved references and consumer inputs, without running Python.
This is not a public automatic MAUI-profile MSBuild switch.

Archived corpus and MAUI drivers intentionally remain source-exact in ignored
materialized upstream. Their old `dotnet-local.sh`, product output and prepared
runtime assumptions are not silently converted into stock-workload commands.
The 22 old Python corpus unit tests remain historical, not newly passing tests.

The separate Kotlin runtime package/native bridge is preserved source and a
historical inspection artifact; the .NET workbench migration does not claim
a fresh runtime, MAUI/device or whole-corpus rebuild. Its native bridge belongs
in the app's one selected Gradle graph, not in an extra incompatible archive.

Full product preparation follows the original pinned upstream instructions.
Its `eng/Versions.props` pins `12.0.100-alpha.1.26523.103`, separately from the
installed SDK 11.0.100 used by host/package work. `dotnet-local.sh` only works
after preparation and searches Release before Debug. Do not copy a previous
prepared SDK or silently install/replace global workloads.

Keep native alternatives and owner registries in their correct graph/layout.
A 717-record coverage report is not a compatible combined classpath or a newly
regenerated 717-record per-artifact tree. Device/commercial-service experiments
are outside this migration's fresh validation scope.
