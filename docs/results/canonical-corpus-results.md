# Canonical corpus ownership: first implementation

**The generator and corpus tooling now implement the first part of the desired
dependency model: one allocated assembly owner for each public Java type, shared
by all consumers. This is not another set of per-library projections.**

The final consolidated dependency DLL tree has **not** been generated yet.
This slice implements supported-API cleanup, the persistent owner registry,
dependency planning, and enforcement in the actual assembly emitter. It also
identifies the remaining native/API choices instead of hiding them.

## What changed

1. **Implementation names are no longer ordinary public APIs.** The opt-in
   semantic compiler recognizes reviewed Google `zz...` implementation naming
   and Google Play Services internal packages. Those definitions remain hidden
   infrastructure, not blindly deleted classes. Explicit supported exceptions
   are retained without overriding actual source restrictions.
2. **Every allocated public type has one assembly owner.** Multiple libraries
   using Lifecycle point to one Lifecycle owner. The catalog also records
   assembly dependencies and the hidden signatures/implementation closure.
3. **Ownership survives native packaging changes.** Later snapshots reuse the
   existing registry. Even if a type temporarily disappears, its old owner
   remains reserved without falsely advertising that type as currently available.
4. **Emission enforces the map.** The real emitter accepts the established owners,
   refuses missing public owners or inconsistent nested ownership, and rejects
   duplicate native definitions even when they come from the same Maven group.

The existing default/non-semantic generator path is unchanged. The new
classification is part of the experimental semantic path.

## Full-corpus result

The audit includes **all 717 configured records**, including **all 59 Firebase
records**. It uses official Kotlin metadata/ASM descriptors with archive hashes,
descriptor hashes and exact input coverage checks.

After applying the five previously reviewed Android/JVM sibling aliases, the
full selection contains 711 modules and 641 native archives. There are 136,810
physical class definitions across those archives:

- 136,531 type declarations classified.
- 279 `package-info` metadata classes accounted for separately.
- No other native class silently left unclassified.

| Result | Count |
|---|---:|
| Current public type candidates with an allocated canonical owner | 51,910 |
| Assembly owners represented in the dependency plan | 164 |
| Total reserved owner identities, including one currently absent type | 51,911 |
| Public names with differing native contracts requiring selection/review | 122 |
| Public names also requiring an explicit ownership-family decision | 2 |
| Public class/interface-kind disagreements after classification | **1** |

These are **catalog and planning counts, not generated DLL or certified API
counts**. `Public` means a candidate under the recorded visibility, Kotlin,
annotation and publisher evidence. It is not a guarantee of upstream behavioral
compatibility.

The normalized graph reuses every previously allocated matching owner. In
particular, changing the selected Android/JVM containers does not make the
managed owner follow the new archive.

## The important finding

The complete input has 282 raw class/interface-kind disagreements across
competing native definitions. After distinguishing implementation names from
public API candidates, **one public kind disagreement remains**:

`com.google.android.gms.ads.MediaContent`

| Native artifact | Kind |
|---|---|
| `com.google.android.gms:play-services-ads-api:25.5.0` | interface |
| `com.google.android.gms:play-services-ads-lite:24.0.0` | interface |
| `com.google.android.libraries.ads.mobile.sdk:ads-mobile-sdk:1.5.0` | class |

This is a genuine choice between Google Ads SDK families. We do not invent two
public C# MediaContent projections or pretend those native libraries can all
define the same class in one app.

The other 121 differing public names involve member/hierarchy/generic contract
differences, not further public class/interface switches. They are conservative
review items, **not 121 proven impossible combinations**.

The two unresolved owner families are:

- `com.google.android.libraries.barhopper.RecognitionOptions`
- `com.google.android.libraries.vision.visionkit.pipeline.PipelineException`

These occur in competing Firebase/ML Kit native cohorts without an unambiguous
initial Maven-group owner. The catalog leaves the owner unresolved rather than
creating duplicate managed definitions or arbitrarily choosing one.

Hidden implementation variants also remain recorded. They are not promoted to
public types, and necessary signature/virtual/callback facts are not erased to
make the report appear cleaner.

## Native correctness remains a separate gate

The normalized all-roots combination still has **3,004 raw duplicate JVM names**.
The native ownership audit continues to reject that combined classpath.
Classifying a Java implementation type as internal in C# does not remove a
duplicate native class from DEX.

The next step is to choose and verify the coherent native baseline for these
remaining SDK alternatives, bind its supported contracts using this one registry,
then generate the shared assembly tree. Legacy or competing products must be
explicitly mutually exclusive or superseded through verified policy; they must
not bring another copy of Lifecycle or another public projection into the tree.

All 717 original records remain in coverage even where the eventual native
choices cannot be installed together.

## Actual verification

- `./dotnet-local.sh test external/Java.Interop/tests/generator-Tests/generator-Tests.csproj -v:q -nr:false`:
  **762 passed**.
- `python3 -m unittest discover -s tests/BindingProjection/Corpus -p 'test_*.py' -v`:
  **18 passed**.
- `python3 -m unittest discover -s external/Java.Interop/tests/generator-Tests -p 'test_compare_binding_corpus.py' -v`:
  **20 passed**.
- Official Kotlin descriptor `test installDist`: **9 tests passed**.
- Real `play-services-tasks:18.4.1` binding generation and compilation:
  **passed with zero C# errors**.
- The real Tasks implementation was emitted through the full-corpus owner map:
  **2 inspection assemblies, 66 managed types; ownership and references verified**.
  This is a focused implementation probe, not the finished GMS package.
- Executable tests verify circular assembly references, cross-assembly IL calls,
  stable canonical identities and rejection of missing owners/native duplicates.
- A lowering regression checks that hidden implementation returns keep their
  original JNI descriptors and explicitly supported callback interfaces stay usable.
- The full-corpus registry was reused across actual Android/JVM container
  consolidation with **no reassignment of established owners**.
- `git diff --check`: **passed**. No device runtime changes were made in this
  slice, and no new whole-corpus on-device claim is being made.

## Files and reproduction

The reproducible catalog command is:

```sh
python3 tests/BindingProjection/Corpus/generate-assemblies.py \
    --resolved /absolute/path/to/resolved-native-inputs \
    --config /absolute/path/to/config.json \
    --output /absolute/path/to/new-catalog --catalog-only \
    --canonical-owners /absolute/path/to/previous/canonical-owners.json
```

Omit `--canonical-owners` only when allocating the initial registry.
`--reuse-descriptor` may reuse existing verified per-archive descriptors.

The checked-in documentation is
`tests/BindingProjection/Corpus/README.md`.
The machine-readable current result is
`canonical-corpus/normalized/canonical-owners.json` in this session's files.
The accompanying inspection archive includes that complete audited catalog,
compact owner/dependency indexes, remaining public contract differences,
original-record coverage, selected native coordinates and validation evidence.
It contains no Maven binaries or supposedly completed dependency DLL tree.
