# Experimental compiler source

`manifest.json` pins the upstream repository and commit, the original tracked
patch, all 173 local additions and all 91 tracked post-patch outputs. Together
these account for **264 source files**. Every addition is checked in here;
bootstrap has no external source-bundle dependency.

The [snapshot change record](../provenance/snapshot-change.json) identifies the
previous 277-file snapshot and its hashes, plus the 13 research-automation
additions no longer included. Retained compiler/runtime/native fixture and
regression source is byte-for-byte unchanged. No source-integration patch
was needed.
The explicit workbench differences are the portable runner and sample outside
this directory, selection of installed SDK 11.0.100, and the package-version
override `0.1.0-preview.workbench.3`.

Nine tracked files have git-normalized LF hashes and separately recorded CRLF
worktree hashes. Bootstrap accepts only those exact declared representations;
it does not normalize or otherwise rewrite source to conceal a mismatch.

## Browse the additions

Paths below are browseable additions, not an independent build tree:

| Area | Source |
| --- | --- |
| Semantic planner, Kotlin contracts, typed views | [generator semantics](additions/external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.Semantics/) |
| Bytecode/Kotlin lowering | [bytecode additions](additions/external/Java.Interop/src/Xamarin.Android.Tools.Bytecode/) plus tracked patch |
| Official Kotlin metadata and ASM reader | [JVM descriptor](additions/external/Java.Interop/tools/jvm-api-descriptor/) |
| Canonical ownership, implementation splitting, artifact contracts | [grouped-bindings](additions/external/Java.Interop/tools/grouped-bindings/) |
| Compiled full/selected compatibility profiles | [binding-compatibility](additions/external/Java.Interop/tools/binding-compatibility/) |
| Build-only NuGet and MSBuild integration | [Semantic package](additions/src/Microsoft.Android.BindingGenerator.Semantic/) |
| Generic/Kotlin Task/Flow runtime and native bridge source | [Kotlin runtime](additions/src/Microsoft.Android.Kotlin/) |
| Reference, corpus, package and MAUI fixtures | [BindingProjection](additions/tests/BindingProjection/) |
| Cross-assembly peers and runtime tests | [Mono.Android tests](additions/tests/Mono.Android-Tests/) |
| Compiler regressions and authored native probes | [generator tests](additions/external/Java.Interop/tests/generator-Tests/) |

Existing-file changes are in [the tracked patch](patches/android.patch).
Run `dotnet run --project eng/Workbench -- bootstrap` from the repository root to inspect
the combined tree in `.artifacts/source/`. Build or run drivers from that
combined tree, **not** from `upstream/additions/`.
Individual source READMEs retain their original research context; earlier
scripted corpus/device drivers are not part of this current snapshot. Use
[the command guide](../eng/README.md) for supported entrypoints.

`external/Java.Interop` is vendored at this revision. The only upstream
submodule is the optional API documentation; its exact pin is retained but
the minimal host workflow does not download it.

Upstream licensing and third-party notices are retained at the repository root
and in `provenance/`. Full product preparation follows the preserved upstream
instructions, not the lightweight workbench host flow.
