# Original Python source preservation

There are no tracked Python files or disguised executable Python scripts in the
current workbench. Supported commands are implemented in C#/.NET.

The original 33 Python files were not discarded, renamed to another source
extension or hidden in a git archive. They are byte-exact Git blobs from
`Redth/AndroidBindingsExperiments@6dd45d4b641d16b0ee8701fe593f1b0312a5f5b4`:

| Classification | Files |
| --- | ---: |
| Original upstream additions, including corpus and native/device drivers | 13 |
| Historical research/audit helpers | 18 |
| Previous workbench runner and its tests | 2 |

The external archive is
[original-python-sources-6dd45d4.zip](https://github.com/Redth/AndroidBindingsExperiments/releases/download/experiment-snapshot-2026-11-14/original-python-sources-6dd45d4.zip),
105481 bytes, SHA-256:

```text
9f1b3bbea0b2c404dd10d31e259ffa07cf577a8f94ead8aae4302be542b945a3
```

The archive, original index and checksums are published on the historical
snapshot release. Their server digests were verified. The coordinating session
then cleared the default archive cache and independently exercised the .NET
HTTP download, preservation verification and a new source bootstrap with
Python blocked: all 190 curated records, 33 archive files and 277 source outputs
verified without invoking a Python interpreter.

The indexed archive URL remains on that historical release. The current C#
workbench and package use the separate
[.NET-only release](https://github.com/Redth/AndroidBindingsExperiments/releases/tag/dotnet-workbench-2026-11-14).
The `--archive` override remains available for a verified local copy or offline
use; it is not required for normal public download.

[archived-python.json](../provenance/archived-python.json) records every original
repository-relative member path, SHA-256, Git blob ID, size and mode, as well as
the archive URL/hash. The original standalone index has SHA-256
`a270bd1bf11c9547c3f6c75de7fb0c09ccbcb2c431bc88939965dd6fb6c1576e`;
the checked-in adaptation adds download metadata without changing file records.

The original `upstream/manifest.json` and `provenance/curated-files.json` remain
unchanged. The .NET verifier resolves their archived entries through this
explicit mapping and verifies archive bytes and member hashes. It rejects
missing, duplicate, unsafe or altered members; extra directory metadata is
allowed only for actual source parents.

Bootstrap combines pinned upstream + original tracked patch + local non-Python
additions + archived Python additions to reconstruct **all 277 original source
outputs**. Only the 13 original upstream scripts are restored into the ignored
materialized checkout. They are not executed by supported workflows. Historical
helpers and the former workbench remain archive-only.

The original Python corpus tests and prepared-SDK/device drivers are historical
or advanced research, not a Python-free supported workflow. Their old commands
can appear in frozen research notes and source READMEs. Use
[the .NET command guide](../eng/README.md) for current commands; it never invokes
the archived Python runner or integration fixture.
