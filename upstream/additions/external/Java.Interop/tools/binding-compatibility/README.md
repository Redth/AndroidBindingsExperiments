# Experimental compiled binding compatibility profiles

This pilot extracts AndroidX/Material contracts from compiled MAUI assemblies,
not source API name lists. It is not a general APICompat replacement or a
certificate that every native version/library can replace every old binding.

```sh
./dotnet-local.sh build external/Java.Interop/tools/binding-compatibility/binding-compatibility.csproj
./dotnet-local.sh external/Java.Interop/tools/binding-compatibility/bin/Debug/binding-compatibility.dll \
  extract full-profile.json /resolved/reference-directory Microsoft.Maui.dll Microsoft.Maui.Controls.dll
./dotnet-local.sh external/Java.Interop/tools/binding-compatibility/bin/Debug/binding-compatibility.dll \
  select full-profile.json selected-profile.json /resolved/reference-directory \
  Microsoft.Maui.dll Microsoft.Maui.Controls.dll Frozen.Consumer.dll
```

`extract OUTPUT REFERENCES ROOT...` walks public/protected signatures, generic
arguments/constraints, bases/interfaces and the public/protected surface of
reachable binding types. It records exact assembly identities, input hashes,
managed signatures, flags, and JNI registrations. The current native-owner
scope is `Xamarin.AndroidX.*` and `Xamarin.Google.Android.Material`, not all
possible third-party MAUI dependencies.

`select FULL OUTPUT REFERENCES CONSUMER...` creates a usage-selected profile
and an `OUTPUT.usage.json` evidence report. Identity-only types do not
automatically demand their old constructors, overloads, or subclass surface.
Observed calls/construction, subclassing, overrides and interface implementation
are recorded separately. The selector also retains implicit legacy interop
dispatch properties for constructed/subclassed peers: `XAPeerMembers` uses
`ThresholdClass`, `ThresholdType` and `JniPeerMembers` without a direct consumer
member reference.

**Selection is a declared, closed set of compiled consumers**, not proof that
arbitrary future plugins or reflection never use an omitted member. Public
signature-only exposure is evidence of identity use, not proof that callers
cannot subclass. Add the relevant consumer binaries, retain the full contract,
or explicitly expand the compatibility contract where that guarantee is needed.

Pass either profile to the semantic compiler:

```text
--experimental-semantic-model
--experimental-compatibility-profile=selected-profile.json
```

The compiler restores protected managed names and native slots, original
virtual/accessibility choices and required legacy dispatch infrastructure.
The bounded pilot also supports legacy int-to-enum forwarding overloads when
selected. It does not remove modern native API additions.

**Validate the compiled result before delivering it:**

```sh
./dotnet-local.sh external/Java.Interop/tools/binding-compatibility/bin/Debug/binding-compatibility.dll \
  verify selected-profile.json Xamarin.AndroidX.SwipeRefreshLayout.dll
```

Concrete additions are allowed; missing selected contracts, owner changes, JNI
slot changes, changed virtual flags/constraints and new abstract obligations
are rejected. Framework `System.Runtime` and Java.Interop reference versions
are normalized for this framework-upgrade pilot; external binding owner
identities are not.

This is deliberately fail-closed for unsupported emitted shapes. It does not
implement arbitrary historical generic class/property/event reconstruction,
reflection/serialization compatibility, native ABI reconciliation, or
automatic project-wide owner redistribution. The source compiler switch and
standalone verification tool are not yet a new public MSBuild property in the
previously packaged semantic generator preview.
