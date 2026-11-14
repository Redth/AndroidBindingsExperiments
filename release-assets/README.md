# Snapshot downloads

Large archives and historical NuGet packages are intentionally **not in git**.
The [.NET-only release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/dotnet-workbench-2026-11-14)
is the location for the current compiler, matching provenance and source-code
downloads from the Python-free C# revision. If it is not visible yet,
publication is still in progress. The current compiler can also be rebuilt with
`dotnet run --project eng/Workbench -- pack`.

The [historical snapshot release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/experiment-snapshot-2026-11-14)
retains the 18 historical inspection assets and
`original-python-sources-6dd45d4.zip` at its original indexed URL. That release's
source-code downloads and old Git history remain explicitly historical.

The .NET-only revision prepares compiler **`0.1.0-preview.workbench.2`** and
matching `.provenance.json` for separate publication. Until those files are
uploaded, the existing `workbench.1` release remains the older snapshot.
Fresh packages/provenance use a separate `workbench-package-SHA256SUMS` release
file, not the **18 historical assets** indexed by `assets.json` and `SHA256SUMS`.

`assets.json` lists 18 historical assets with their scope, size and public
SHA-256. `SHA256SUMS` records the same public bytes.
`public-archive-audit.json` explains sanitization of historical text/logs and
preservation of binary/generated-source payloads. Some archives retain
historical placeholders or raw build paths; they are not portable scripts.
When an archive was repacked, `sourceOriginalSha256` records the old source
archive separately. Verify downloads against the **public** hash, not an older
checksum quoted in a research report.

Key distinctions:

| Asset family | What it represents |
| --- | --- |
| Semantic preview and `0.1.0-preview.1` nupkgs | Historical installed-workload compiler/runtime snapshot; predates later CLI compatibility work |
| MAUI protected compatibility pilot | One frozen-consumer full/selected-contract experiment, not a general replacement binding NuGet |
| Consolidated tree/generated source | Older complete compatible **group-layout** baseline, not newest artifact output |
| MapLibre/Sentry/Koin artifact bindings | Separate complete **per-artifact** graphs; do not merge |
| Third-party SDK validation | Independent bounded profiles, not one universal graph or commercial service validation |
| Canonical/full-config/native-evolution evidence | Audits, coverage, alternatives and API evolution; not necessarily generated deliverables |
| Earlier descriptor/comparison/preflight summaries | Preserved chronology and inspection results, superseded where later reports say so |

The .NET-only revision additionally needs the
[original Python source archive](../docs/python-archive.md) as verified data for
exact source reconstruction. That archive is published on the historical
release at its unchanged indexed URL, and default HTTP reconstruction is
verified. New package assets use the .NET-only release, not the 18-entry
historical manifest.

The fresh local compiler has the distinct version
`0.1.0-preview.workbench.2`. Its nupkg and matching `.provenance.json` are under
`.artifacts/feed/`, outside this historical asset index. Never substitute one
byte snapshot for another package with the same ID/version in a NuGet cache.
The previously released `0.1.0-preview.workbench.1` remains a separate older
snapshot; it is not the package produced by the .NET-only command guide.
