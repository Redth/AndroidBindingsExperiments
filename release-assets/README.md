# Snapshot downloads

Large archives and historical NuGet packages are intentionally **not in git**.
The [snapshot release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/experiment-snapshot-2026-11-14)
is their download location. If the release is not visible yet, publication is
still in progress. The current compiler can also be rebuilt locally with
`python3 eng/workbench.py pack`.

The release separates the freshly tested compiler
**`0.1.0-preview.workbench.1`**, its matching `.provenance.json`, and
`workbench-package-SHA256SUMS` from the **18 historical assets** below.
Use that separate release checksum file for the fresh package and provenance;
they are deliberately not added to the historical `assets.json` or `SHA256SUMS`.

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

The fresh local compiler has the distinct version
`0.1.0-preview.workbench.1`. Its nupkg and matching `.provenance.json` are under
`.artifacts/feed/`, outside this historical asset index. Never substitute one
byte snapshot for another package with the same ID/version in a NuGet cache.
