# Portable commands and advanced paths

The portable, freshly checked entrypoints are `eng/workbench.py` and the authored
hello sample. They derive paths from the repository, use argument arrays rather
than shell-generated command strings, select an installed SDK, and keep source,
temporary files and generated output under `.artifacts/`.

The wrapper does not reference a creator's session, built SDK or private cache.
The original source/curated manifests are unchanged; there are no hidden
integration patches. Standard NuGet and Gradle caches may accelerate restore,
but the source pin and declared dependencies establish the inputs.

## Classification of preserved drivers

| Location | Classification |
| --- | --- |
| `eng/workbench.py` | Current bootstrap/build/test/pack/generate and evaluated host-tool entrypoint |
| `samples/hello/` | Current authored native/binding/downstream compile sample |
| Materialized `tests/BindingProjection/GeneratorPackage/verify-package.py` | Fresh no-device installed-workload fixture; explicit optional runtime/upgrade/device scopes |
| Materialized `tests/BindingProjection/Corpus/test_*.py` | Fresh pure-Python corpus regression tests |
| Materialized corpus generation/smoke drivers | Advanced prepared-product-SDK path, not a minimal-bootstrap promise |
| Materialized `MauiCompatibility/verify-pilot.py` | Advanced prepared-product-SDK/native path; full bounded result is historical |
| Materialized Kotlin/device comparison drivers | Prepared SDK, selected native graph and explicit owned device required; historical results |
| `historical/session-helpers/*.py` | Preserved exploratory logic only; not runnable workbench commands |

The last group includes corpus analysis/collision audits, additional SDK
generation/runtime helpers, closure/incrementality probes, and packaging
helpers. Some contain literal `${ANDROID_SOURCE}` placeholders and historical
output assumptions. Python does not expand those placeholders automatically.
Keep them as evidence; port their inputs/output roots and validate the specific
path before treating one as a supported command.

The original corpus and MAUI drivers intentionally remain source-exact. They
use `dotnet-local.sh`, product output locations and prepared runtime DLLs.
The current `tool` command instead queries MSBuild for each host tool's actual
`TargetPath`, avoiding the old conventional-bin assumption. This does not
silently convert every advanced native driver into a stock-workload build.

## Runtime and full native/MAUI work

The runtime package source is under
`upstream/additions/src/Microsoft.Android.Kotlin/`; use its materialized copy
for builds. Its native bridge joins the app's one selected Gradle graph and
must be built once, not injected as an extra incompatible prebuilt archive.
The historical `0.1.0-preview.1` runtime nupkg remains a separate inspection
asset. The handoff does not claim a fresh runtime rebuild.

A full product build follows the pinned upstream preparation instructions.
The source's `eng/Versions.props` pins SDK
`12.0.100-alpha.1.26523.103`; this is separate from the installed SDK 11.0.100
used for host/package work. `dotnet-local.sh` only works after preparation and
searches Release before Debug. Do not copy someone else's prepared SDK or
silently install/replace global workloads.

Materialize new native paths using the preserved resolver/configuration rather
than treating placeholder paths in `evidence/` as live archives. Keep native
alternatives and owner registries in their proper graph/layout. A 717-record
coverage report is not a successful combined classpath or a fresh regenerated
717-record artifact-layout tree.

Device runners require a deliberately chosen owned serial. No workbench
command picks a default ADB device, starts/stops an emulator, installs an app,
makes a purchase or invokes commercial-service APIs. Device execution is outside the fresh
handoff's validation scope.
