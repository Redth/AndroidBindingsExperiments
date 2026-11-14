# Experimental Maven corpus assemblies

New generation defaults to **one binding assembly per Maven artifact**, not per
Maven group. `--assembly-granularity group` selects the earlier group-wide layout
for comparison. Existing inspection archives are unchanged historical snapshots.
Both layouts retain one canonical CLR owner per Java type.

Each assembly row records `MavenGroup` and `MavenArtifact`. Artifact assembly
names omit Maven versions, for example
`Android.Maven.Org.Maplibre.Gl.Artifacts.Android_sdk_opengl.dll`. Archives with
no projected managed types, metadata-only modules, and BOMs need no empty DLL.
Managed-only glue can still live in `Android.Maven.BindingSupport.dll`; platform
peers and the shared Kotlin runtime keep their own established assemblies.

The first artifact-layout snapshot deliberately needs a new owner registry:
an old group-layout registry cannot silently be used to reassign CLR identities.
Subsequent artifact snapshots reuse `OwnerRegistry`, including reservations for
absent APIs, so moving a Java type to another Maven archive does not move its
managed owner or create a second projection. The emitter also writes a registry
that can be passed back through `--owners`. Native duplicate definitions and
ambiguous first owners remain errors/review items, not new projections.

This opt-in experiment resolves one version of every configured Maven module in a
pinned `dotnet/android-libraries/config.json`, including its transitive support
libraries, before assigning canonical Maven-group binding owners. There is no
AndroidX/GMS or other Maven-group allowlist: Firebase, ML Kit, Kotlin, Square,
TensorFlow and every other configured family are corpus roots too.

The pinned configuration used by the initial experiment contains **717 records,
705 distinct group/artifact coordinates and 161 Maven groups**, including
**59 Firebase records**. Earlier 444-record reports and assembly ZIPs cover only
the old AndroidX/GMS subset, not the complete configuration.

The complete corpus includes mutually exclusive native SDK alternatives.
The strict whole-world preflight rejects duplicate native ownership.
`generate-assemblies.py` covers all original records using a modern primary
profile plus compatible alternatives, and re-resolves roots whose transitive
versions were made incompatible by the combined graph. It never merges
conflicting JVM definitions.

Each profile is compiled with the current semantic/Kotlin/generic pipeline,
then split into implementation DLLs with one canonical native owner per selected
Maven container. The separate `Android.Maven.BindingSupport.dll` contains managed-only
glue. Type references, IL operands, attributes, and JNI callback connector
assembly names are rewritten to the canonical owners. Internal bridge access
is preserved through generated friend relationships. CLR reference cycles
are retained without merging Maven groups.

Use one complete profile at a time; files from different profiles are not
interchangeable overlays. `Microsoft.Android.Kotlin` remains a separate runtime
owner. These are experimental inspection assemblies, not published NuGet
packages or a compatibility guarantee for arbitrary Maven version combinations.

## One consolidated DLL tree

Use `--consolidated` to build one modern compatible baseline instead of emitting
self-contained alternative sets:

```sh
python3 tests/BindingProjection/Corpus/generate-assemblies.py \
    --resolved /absolute/path/to/resolved-evidence \
    --config /absolute/path/to/config.json \
    --output /absolute/path/to/consolidated-output --consolidated
```

For subsequent snapshots, add
`--canonical-owners /absolute/path/to/established/artifact-owners.json`.

The only implementation directory is `tree/assemblies/`. Shared types have one
canonical owner and dependent assemblies reference it. The baseline prefers
modern GMS Ads/Games, ML Kit and modular Play libraries; genuinely overlapping
native products remain explicit alternatives, not extra copies of framework DLLs.
`baseline-coverage.json` retains every original configured record, its selected
version and either its inclusion or concrete native conflict evidence. A record
without a native archive may legitimately need no separate binding assembly.

The baseline is checked for duplicate native classes. Its own canonical catalog
can reuse an established same-layout registry, then assigns any previously ambiguous owners
from the single selected native definition. No contract variants are blindly
unioned. Only the baseline is generated; other profiles in `profiles.json` are
accounting information, not additional deliverable assembly directories.

Already referenced platform peers remain authoritative. For example, Maven's
`org.json` archive does not create second `JSONObject` or `JSONArray` definitions:
the bindings use `Mono.Android`'s existing types and API surface. New non-platform
types can still have Maven owners. The generator records these decisions in
`*.referenced-owners.json`; final ownership manifests preserve them, and the
verifier rejects regenerated peers or references targeting the wrong assembly.
This does not make newer JVM-only JSON members available on Android, nor does it
remove duplicate platform classes from the upstream native archive.

## Generate assemblies

After resolving/auditing the inputs below, build the compiler, canonical runtime,
descriptor `installDist`, and `tools/grouped-bindings/grouped-bindings.csproj`:

```sh
python3 tests/BindingProjection/Corpus/generate-assemblies.py \
    --resolved /absolute/path/to/resolved-evidence \
    --config /absolute/path/to/config.json \
    --output /absolute/path/to/grouped-assemblies --source-groups
```

The output includes `profiles.json`, native archive hashes, official descriptors,
generated C# sources, build logs, and per-profile `assemblies/ownership.json` and
`assemblies/verification.json`. Repeat `--profile NAME` to select profiles;
`--split-only` resumes grouping from an already compiled bootstrap. The bootstrap
is a build intermediate and is not part of the delivered assembly set.
SDK analyzers are disabled for this generated whole-graph intermediate to bound
compilation memory; ordinary C# compiler diagnostics remain enabled. The consumer
and runtime probes use their normal analyzer settings.

The smoke app supplies Google's documented sample AdMob application ID because
a complete GMS-group native closure includes the Ads initialization provider,
even when the checks do not request ads. Test the exact native closure required
by the selected complete binding assemblies, not just the individual APIs called
by the probe, and honor that closure's minimum Android SDK requirement.

The default `--source-groups` path avoids the CLR's per-assembly user-string limit.
It first emits metadata-only reference contracts with
canonical group identities, then compiles real per-group source implementations
against those contracts. Cyclic dependencies remain valid without building a
giant implementation intermediate. Only implementation DLLs are delivered.
`--no-source-groups` selects the older compact-bootstrap relocation path for
graphs that fit within the CLR limit.
`--reuse-descriptor` reuses an existing descriptor when only the C# compiler
pipeline changed; native hashes are still audited.

Before planning or generation, `configuration-coverage.json` checks the resolved
selection against every original configuration record, including duplicate
coordinates at different versions, `dependencyOnly`, and no-binding records.
A matching config hash or matching record count alone is insufficient. An old
partial resolution is rejected, even when it used the same `config.json`.

The verifier resolves every group-owned type/member reference and rejects
duplicate managed type owners. `Smoke/GroupedSmoke.csproj` and `verify-smoke.py`
exercise JNI callbacks, generics, Task/Flow, bounds, arrays, and canonical peer
identity through a real grouped implementation set, including Release/R8 and
NativeAOT. The emitter also has an executable host regression with cyclic
assembly references and nested compiler-style IL.

The current 26-check probe includes projection-aware record constraints and
mapped-view conversions. It requires a fixture binding regenerated with the
current compiler and shared runtime; older 19-check delivery archives remain
historical snapshots and do not contain the new projection contracts.

## Canonical supported-API catalog

The profile outputs above are compatibility experiments, not the desired final
package tree. To audit the entire configuration while assigning each public
Java type at most one managed owner, use:

```sh
python3 tests/BindingProjection/Corpus/generate-assemblies.py \
    --resolved /absolute/path/to/resolved-evidence \
    --config /absolute/path/to/config.json \
    --output /absolute/path/to/canonical-catalog --catalog-only
```

This mode does not partition or regenerate shared owners inside compatibility
profiles. It extracts official Kotlin/JVM descriptors separately for each native
archive, checks archive/descriptor hashes and exact input coverage, then applies
the same accessibility policy as the generator. Package annotations are counted
separately; any other unclassified native type is an error.

The experimental publisher policy treats `zz...` implementation declarations in
the reviewed Google namespaces, and Google Play Services internal packages, as
hidden infrastructure rather than ordinary supported APIs. Explicit
`obfuscated="false"` exceptions are honored without overriding source restrictions.
The native facts and necessary signature/virtual/callback closure remain intact;
this is not permission to remove duplicate classes from Android's native graph.

`canonical-owners.json` contains one `Types` owner record per allocated public
type, a conservative assembly dependency graph, and separate lists for public
contract differences, unresolved ownership, and variant-dependent hidden
requirements. `OwnerRegistry` reserves established identities even when a type
temporarily disappears from a native snapshot; it does not advertise an absent
type in the current `Types` inventory. `Public` means a supported-API candidate under the recorded
visibility/annotation/publisher evidence, not a guarantee of upstream behavioral
compatibility. Different descriptors/hierarchies are never silently unioned.
The report explicitly says `audited-not-generated`.

Initial ownership uses Maven-artifact defaults (or Maven groups in legacy mode),
preferring a matching Java namespace family when multiple groups contain the same name. Ambiguous owners require
review instead of creating duplicates. For subsequent snapshots, pass the old
map with `--canonical-owners /path/to/previous/canonical-owners.json` to retain
established owners when upstream packaging moves. Write to a new output directory;
the established owner input cannot be overwritten in place.

For implementation generation, `--canonical-owners` requires exactly one
`--profile` native baseline and the source-group path. The emitter uses that map,
rejects missing public owners, checks nested ownership, and refuses duplicate
native definitions even within one Maven group. Executable regressions cover
shared dependencies, stable ownership despite new native packaging, circular
references, actual cross-assembly IL calls, and failures on missing owners.
The remaining incompatible SDK choices still need native selection and contract
review before a final comprehensive package tree can be emitted.

Compile the small cross-group Activity/Lifecycle/SavedState consumer against a
complete profile without packaging its native graph:

```sh
./dotnet-local.sh build tests/BindingProjection/Corpus/Consumer/GroupedConsumer.csproj \
    -c Debug -v:q -nr:false \
    -p:_AndroidGroupedAssemblyDirectory=/absolute/path/to/profile/assemblies
```

Add `-p:_AndroidConsolidatedConsumer=true` for Firebase Auth, ML Kit and Health
Connect coverage. The Firebase and ML Kit calls deliberately return the same
canonical Google Tasks CLR type. Runtime probes can pass
`--probe-assembly /path/to/fixture-only.dll` to `verify-smoke.py` to exercise a
precompiled consumer against the actual consolidated dependency DLLs, without
regenerating framework projections inside the fixture library.

## Resolve and audit

From the repository root, with JDK 21 in `JAVA_HOME`:

```sh
bash build-tools/gradle/gradlew --no-daemon --console plain \
    -p tests/BindingProjection/Corpus/Native corpusInventory \
    -PbindingCorpusConfig=/absolute/path/to/config.json \
    -PbindingCorpusOutput=/absolute/path/to/evidence

bash build-tools/gradle/gradlew --no-daemon --console plain \
    -p tests/BindingProjection/Corpus/Native publishedRelocations \
    -PbindingCorpusConfig=/absolute/path/to/config.json \
    -PbindingCorpusOutput=/absolute/path/to/evidence

bash build-tools/gradle/gradlew --no-daemon --console plain \
    -p tests/BindingProjection/Corpus/Native nativeInputs --write-locks \
    -PbindingCorpusConfig=/absolute/path/to/config.json \
    -PbindingCorpusOutput=/absolute/path/to/evidence \
    -PbindingCorpusPublishedRelocations=/absolute/path/to/evidence/published-relocations.json

python3 tests/BindingProjection/Corpus/prepare-inputs.py \
    --native-inputs /absolute/path/to/evidence/native-inputs.json \
    --output /absolute/path/to/evidence/preflight \
    --config /absolute/path/to/config.json \
    --selection /absolute/path/to/evidence/selection.json
```

`corpusInventory` records all configured roots without resolving their native
archives, so even an unavailable Maven artifact remains visible in the corpus.
`nativeInputs` depends on it. Explicit `bindingCorpusIncludeRoots` filters are
reserved for focused/isolated resolutions and are recorded in `selection.json`;
they are not accepted as a complete corpus by the assembly driver.

`publishedRelocations` reads each configured coordinate's published POM through
the shared Gradle repositories and records standard Maven relocation declarations
with POM hashes. Passing that report to `nativeInputs` follows only those exact
source-version redirects; the POM contents and hashes are checked again before
substitution. This handles TensorFlow's metadata/support redirects to LiteRT and
Error Prone's annotation redirect without silently dropping the original records
or inventing replacement versions. Obsolete explicit artifact selectors are
cleared for relocated coordinates so a former AAR dependency cannot request a
nonexistent AAR from a metadata-only relocation placeholder.

The audit saves archive hashes, native type ownership, duplicate definitions,
group counts, and proposed aliases from explicitly resolved `*-jvm` modules
to their selected `*-android` siblings. Repeated records of the same archive
are deduplicated; definitions from distinct modules are never silently merged,
even when the class bytes are identical.

Save reviewed aliases separately from the audit's output files, then resolve
again with `-PbindingCorpusModuleAliases=/absolute/path/to/aliases.json`.
Each entry maps `group:module-jvm` to `group:module-android:version`.
Only same-family Android/JVM aliases are accepted; this is not an escape hatch
for replacing unrelated SDKs.

`nativeInputs` preserves every original requested record and its selected
module/version, variant alias, and supersession status. It also writes the
resolved module dependency graph. Lock state is recorded by Gradle.

`nativeDescriptor` depends on `nativeOwnership`, which runs this audit. Any
remaining duplicate JVM name fails before the official Kotlin/ASM descriptor
tool executes. The descriptor tool's dependencies remain on an isolated build
classpath, not in the native graph.

## Why compatible profiles are necessary

Normalizing Android/JVM siblings fixes the Paging, DataStore, Compose runtime
annotation, and Window variant overlaps. It does not fix distinct SDKs shipping
the same native binary names. The pinned corpus requests old Ads Lite/Base/GASS
alongside the newer full Ads SDK, overlapping Analytics/TagManager generations,
and Drive alongside Games V2. The planner partitions selected root closures into
compatible profiles, preferring modern full Ads and Games V2 in `primary`.
Roots whose own transitive closure is broken by globally selected versions are
resolved independently. Every original record belongs to exactly one profile;
multiple requested versions of the same coordinate follow the highest-version
selection policy rather than creating duplicate peers.

Each alternative is a complete, separately verified assembly set, not an overlay
for `primary`. Dropping conflicting classes, merging incompatible definitions,
or excluding original roots silently would not produce a trustworthy native
classpath. Native libraries with no projected JVM types remain recorded in the
input catalog without manufacturing an empty binding DLL.

## Tests

```sh
python3 -m unittest discover -s tests/BindingProjection/Corpus -p 'test_*.py' -v
```

The tests cover AAR/JAR enumeration, archive integrity, repeat-record
deduplication, explicit variant aliases, and rejection of both identical and
different cross-module native definitions. They also cover full-config membership,
Firebase and arbitrary groups, original-record multiplicity/flags, and rejection
of stale partial selections. They require no new Python packages
or local Android SDK.
