# Scope and portability

The supported local path is the C# runner under `eng/Workbench/`, its safeguard
tests and the authored hello sample. Bootstrap uses pinned upstream plus the
tracked patch and **only local additions**. It has no research-archive download,
interpreter or previous-session dependency.

The [source manifest](../upstream/manifest.json) now describes 264 files:
91 tracked patch outputs and 173 additions. The
[snapshot change record](../provenance/snapshot-change.json) explains the
transition from the older 277-file research snapshot, with exact previous
revision/hashes and omitted automation entries. Retained compiler/runtime/native
fixture bytes are unchanged. Git history and published releases are not rewritten.

| Area | Current status |
| --- | --- |
| Bootstrap, checksums, four host tools, package and hello commands | Supported workbench entrypoints |
| Generator, package-task and workbench safeguards | Run by `test` |
| C# no-device package fixture | Run by `verify-package`, using a new output directory |
| Canonical ownership, compatibility extraction/selection/verification | Built host tools with explicit inputs |
| Earlier corpus/MAUI/native/device automation | Historical research, not a promise of current turnkey entrypoints |
| Kotlin runtime native bridge | Separate source-built runtime/native integration; not rebuilt by compiler `pack` |

The runner derives paths from the repository, uses
`ProcessStartInfo.ArgumentList`, checks source hashes and keeps generated output
under ignored `.artifacts/`. Standard public NuGet/Gradle caches are allowed.
Sample/fixture compiler caches are keyed by nupkg hash and restored bytes must
match the selected feed.

Full product preparation follows the original pinned upstream instructions.
Its `eng/Versions.props` pins `12.0.100-alpha.1.26523.103`, separately from the
installed SDK 11.0.100 used for host/package work. `dotnet-local.sh` only works
after preparation and searches Release before Debug. Do not copy a previous
prepared SDK or silently install/replace global workloads.

Keep native alternatives and owner registries in their correct graph/layout.
A 717-record coverage report is not a compatible combined classpath or a newly
regenerated 717-record per-artifact tree. Compatibility profiles are a bounded
compiled-contract CLI pilot, not automatic native-version reconciliation or a
public MAUI-profile MSBuild switch.

Current validation does not rerun devices, commercial services, full corpus,
Kotlin runtime packaging, or app R8/NativeAOT. The historical evidence remains
useful within its originally recorded scope.
