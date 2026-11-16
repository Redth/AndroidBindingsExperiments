# Packages and inspection downloads

Download the current compiler from the
[workbench release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/workbench-2026-11-16):
`Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.3.nupkg`
and its matching `.provenance.json`. Put the nupkg in a local NuGet feed and use
that version in your binding project's private package reference.

To build it yourself, current source produces
`Microsoft.Android.BindingGenerator.Semantic.0.1.0-preview.workbench.3.nupkg`
and matching `.provenance.json` under `.artifacts/feed/`:

```sh
dotnet run --project eng/Workbench -- bootstrap
dotnet run --project eng/Workbench -- pack
```

The [historical tooling release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/dotnet-workbench-2026-11-14)
contains the older `workbench.2` package and its source/hash provenance.
Never replace existing ID/version bytes in a NuGet cache with a different build.

Fresh compiler packages use a separate `workbench-package-SHA256SUMS` release
file. They are not added to the 18-entry historical `assets.json` or `SHA256SUMS`.
The current bootstrap needs none of these inspection downloads.

## Historical inspection assets

The [historical snapshot](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/experiment-snapshot-2026-11-14)
retains the older results and generated outputs. Large archives stay out of git.
`assets.json` records 18 assets with scope, size and public SHA-256;
`SHA256SUMS` records the same bytes. `public-archive-audit.json` explains
historical text sanitization and preservation of binary/generated-code payloads.

When an archive was repacked, `sourceOriginalSha256` is the earlier source
archive, not the public download hash. Verify against the **public** checksum.
Historical paths/logs are evidence, not portable commands.

| Family | Scope |
| --- | --- |
| Original semantic compiler/runtime preview | Historical `0.1.0-preview.1` snapshot |
| MAUI protected compatibility pilot | One frozen-consumer experiment, not a replacement binding NuGet |
| Consolidated tree/generated source | Earlier compatible group-layout baseline |
| MapLibre/Sentry/Koin artifact bindings | Separate per-artifact graphs; do not merge |
| Third-party SDK validation | Bounded independent profiles, not one universal graph or commercial-service validation |
| Canonical/full-config/native-evolution evidence | Audits, coverage and alternatives, not necessarily generated deliverables |

Old releases and source history remain unchanged. Current source counts and
omitted research automation are documented in
[snapshot-change.json](../provenance/snapshot-change.json).
