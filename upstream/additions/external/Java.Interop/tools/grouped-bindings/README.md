# Experimental Maven-group implementation assemblies

This opt-in inspection tool assigns compiled semantic binding implementations to
canonical Maven-group assemblies. It does not publish packages or promise binary
compatibility across different resolved native graphs.

```sh
./dotnet-local.sh build external/Java.Interop/tools/grouped-bindings/grouped-bindings.csproj -v:q -nr:false
./dotnet-local.sh --roll-forward Major \
    external/Java.Interop/tools/grouped-bindings/bin/Debug/grouped-bindings.dll \
    Bootstrap.dll native-inputs.json /absolute/path/to/assemblies
./dotnet-local.sh --roll-forward Major \
    external/Java.Interop/tools/grouped-bindings/bin/Debug/grouped-bindings.dll \
    --verify /absolute/path/to/assemblies
```

The bootstrap must be compiled from one coherent native graph with the current
semantic compiler. `native-inputs.json` records its actual JAR/AAR paths and Maven
groups. Native registration identifies canonical peers; generic/value-class
views, interface implementors, and source-document helpers follow those peers.
Nested CLR definitions stay with their enclosing owner. Managed-only glue uses
`Android.Maven.BindingSupport.dll`; `Microsoft.Android.Kotlin` stays external.

The tool retains real method bodies, rewriting signatures, IL operands, generic
specifications, overrides, attributes, and JNI connector assembly-qualified names.
Each group is materialized from a fresh metadata copy of the bootstrap to avoid
invalidating lazy metadata and nested-definition caches. Original primitive
metadata codes are preserved. CLR reference cycles are retained as cycles in the
implementation metadata; they are not expressed as circular C# project builds.
Generated friend relationships preserve existing internal bridge access. Each
implementation has its own content-derived module version identifier.

`ownership.json` records every emitted type and owner. Verification rejects
duplicate or missing owners and resolves every group-owned type/member reference.
The host regression additionally executes a cyclic implementation graph with
nested compiler-style IL. Android consumer checks are documented in
[`tests/BindingProjection/Corpus`](../../../../tests/BindingProjection/Corpus/README.md).

Use the entire output directory from one profile. Never combine alternatives
with matching assembly identities. The bootstrap and native archives are build
inputs, not part of the delivered inspection assembly set. Android applications
still need the corresponding resolved native graph.

## Large graphs: canonical reference contracts

The CLR user-string heap limits a single implementation assembly independently
of available memory. For larger graphs, compile the bootstrap with
`ProduceOnlyReferenceAssembly=true` and an `InternalsVisibleTo` declaration so
internal binding infrastructure remains represented. Then use:

```sh
./dotnet-local.sh --roll-forward Major \
    external/Java.Interop/tools/grouped-bindings/bin/Debug/grouped-bindings.dll \
    --contracts Bootstrap.dll native-inputs.json generated-source-directory contracts-directory
```

Roslyn syntax trees associate complete generated files and their helper types
with native owners. The contract catalog records each group's source files.
Compile those sources with the corresponding assembly name and version against
the other canonical contracts, preserving internal friend access. The corpus
driver automates this with `--source-groups`.

Finally, `--finalize implementation-directory contracts-directory/ownership.json`
rewrites JNI connector identities, gives compiler-generated private data helpers
unique names across owners, checks that registered native contracts survived,
writes the implementation inventory, and runs verification. Reference-only
assemblies are explicitly rejected as deliverables. An executable host regression
compiles and runs this cyclic source-build path.

## Shared public ownership

`--catalog native-inputs.json descriptor-directory output.json [previous-owners.json]`
uses the compiler's accessibility policy to audit every archive and allocate a
single owner for each public Java type. The descriptor directory is produced by
`jvm-api-descriptor --manifest native-inputs.json --output-directory DIRECTORY`.
Unlike normal combined-descriptor generation, archive indexing retains conflicting
definitions separately with their provenance; it never chooses a duplicate winner.

The catalog is an audited plan, not a compilable or runtime-compatible native
graph. Public contract differences and ambiguous owners are explicit blockers for
review. Hidden implementation variants remain separately scoped facts, and their
required signature closure is reported instead of discarded.

Pass `--owners catalog.json` after the arguments to `--contracts` to enforce the
established owner assignments during emission. A native type moving to another
Maven container keeps its recorded assembly identity. Missing public owners,
inconsistent nested owners, and duplicate native definitions fail before successful
ownership reports are written. The corpus driver wires this through
`--canonical-owners` for a single chosen native baseline.
