# One Java name, multiple binding contracts

> **Implementation update:** The supported-API policy, full-corpus audit and
> enforced canonical owner registry are now implemented. See
> `canonical-corpus-results.md`. The variant-specific outbound view/adapter
> proposals below remain research, not a requirement for the normal dependency
> tree or a claim that the final consolidated DLL set has been generated.

## Conclusion

We should not make a different full peer-owning binding assembly for every
observed declaration of the same Java name. Most of the current corpus problem
first needs better supported-API classification. The remaining real differences
need explicit native contracts and, when useful, ordinary managed views over one
shared peer.

The managed catalog can describe multiple supported native SDK choices without
claiming that all of those native libraries can coexist in one Android app.

This is a researched design recommendation. No generator behavior was changed
for this investigation, and the proposed class/interface-variant adapter has not
been implemented or certified by the existing shared-view tests.

## Important correction to the earlier conflict count

I re-examined all 1,873 raw native name conflicts across the four generated
profiles of the earlier **444-record AndroidX/GMS subset**. These measurements do
not include the subsequently expanded 717-record full configuration:

| Observation | Count |
|---|---:|
| Raw duplicate JVM binary names | 1,873 |
| Names whose final simple name starts with `zz` | 1,704 |
| Other names, not thereby proven to be supported API | 169 |
| Class/interface-kind disagreements | 265 |
| Kind disagreements whose simple name starts with `zz` | **265** |

Consequently, the previously quoted 265 cases are **not evidence of 265 supported
public API changes**. Every one matches the obfuscation naming default used by
the legacy generator. Google can reuse an obfuscated name for an unrelated
implementation type between builds.

There are four non-`zz` superclass disagreements in this comparison:
`AdRequest.Builder`, `AdManagerAdRequest.Builder`, `H5AdsWebViewClient`, and
`ads.internal.util.WorkManagerUtil`. This is a raw-name result, not a declaration
that all four are supported public contracts or incompatible changes.

The JSON evidence retains each native definition's provenance and the observed
classes/interfaces, bases, interfaces, methods and fields. Reproduce it with:

```sh
python3 audit-binding-contract-collisions.py
```

The script and `canonical-binding-contract-evidence.json` are in this session's
files, next to this document.

## How today's bindings cope

### They do not expose every JVM-public implementation class

The existing generator normally treats `zz...` type names as obfuscated and
filters them before emission. Its short-name heuristic also handles some other
obfuscated names. Our experimental semantic path intentionally bypasses the
name heuristic unless a declaration explicitly has `obfuscated="true"`.

Relevant current-worktree source:

- `external/Java.Interop/tools/generator/Java.Interop.Tools.Generator.Importers/XmlApiImporter.cs:343-345`
- The legacy name policy in that file at `569-588`.
- The emission filter in `external/Java.Interop/tools/generator/CodeGenerator.cs:235-240`.

The shipped Google binding metadata goes further. At the pinned corpus revision:

- Ads removes Google implementation packages and obfuscated declarations.
- GASS removes internal packages and obfuscated classes, interfaces and members.
- Drive removes several internal packages, with explicit exceptions.
- Ads Lite explicitly opts certain obfuscated interfaces and mediation callbacks
  back in with `obfuscated="false"`.

That last point matters: **blanket deletion of everything starting with `zz`
would also be wrong**. Necessary callback/helper contracts must survive, even
when they should not be advertised as ordinary application API.

This explains a significant part of why the older curated bindings do not have
the same enormous public conflict surface as the full-surface experiment.

### They bind a selected SDK API shape, rather than all historical shapes

Dependency binding assemblies are imported as reference symbols; they are not
intended to become independent copies in every consuming binding. Existing
metadata and handwritten additions repair naming, inaccessible bases, generic
signatures, callbacks and source-level inconsistencies.

For a real incompatible SDK change, the binding package/API generally changes
with it. Google explicitly changed four Location client types from classes to
interfaces in Location 21.0.0. The current binding additions actually declare
`partial interface IFusedLocationProviderClient` and
`partial interface IGeofencingClient`.

That is not a loader mechanism which makes an old CLR class and a new CLR
interface equivalent. Managed consumers may need updates, and native callers
must use a compatible graph.

Likewise, CLR type forwarding can preserve identity when a compatible type moves
between assemblies. It does not convert an incompatible class into an interface
or repair incompatible native bytecode.

## Different problems need different solutions

| Situation | Recommended treatment |
|---|---|
| Same supported Java type moves to another Maven archive | Keep its canonical CLR owner; change native provenance, not type identity. |
| Two projects want different generic, nullable or convenience projections of a compatible native type | Ordinary typed views sharing the canonical peer. |
| A newer library adds callable members | One shared carrier; additional views or members with explicit native availability requirements. |
| An obfuscated implementation name is reused for unrelated declarations | Artifact/build-scoped internal facts, not a global public CLR identity. |
| A supported type changes native class/interface kind | Distinct ABI contracts; common outbound views only where the actual required operations are compatible. |
| An app must implement callbacks or subclass the Java type | Generate or select an adapter for the resolved native ABI; reject unsupported combinations. |
| Two native archives define the same binary name in the same class loader | Resolve the native conflict. Hiding the C# type does not remove a duplicate DEX class. |

## Proposed architecture

### 1. Supported API roots plus necessary implementation closure

Determine the public roots from Kotlin visibility, restriction annotations,
published API/reference evidence where available, and reviewed publisher rules.
Treat bytecode visibility as one fact, not a complete support guarantee.

Compute the closure required for public signatures, erased descriptors,
constructors, generics, virtual methods, callbacks and Kotlin helpers. Preserve
that machinery internally or opaquely where possible. If a hidden type becomes a
managed `Java.Lang.Object`, its **original JNI descriptor must remain unchanged**.

For Google libraries, the existing metadata is useful evidence for a publisher
policy and its exceptions, rather than a reason to copy every old transform or
turn a global naming regex back on without review.

This should produce a conflict report about actual supported contracts, separate
from the raw native duplicate-class audit.

### 2. Separate ownership, projection and ABI

There are three different identities:

- A stable public API lineage, with one canonical CLR owner.
- A selected native definition and its binary requirements.
- A managed projection, which need not be a registered Java peer.

Internals with unstable names need provenance scoped to the module/build,
not an assumption that the name identifies one permanent public API.

At runtime, the actual Java class and class loader matter. In a normal resolved
app graph there is one chosen definition of a binary name in that loader.

### 3. Share the Java object, not every possible CLR hierarchy

Multiple views can hold the same canonical managed peer and call different
verified JNI slots. They should not each own another peer registration or
independently manage the Java object's lifetime.

Our existing Reader/Writer experiment already implements this separation.
`tests/Mono.Android-Tests/SharedPeerViews/Widget.cs` holds a shared
`Java.Lang.Object`; the Reader and Writer projections are ordinary classes,
not `IJavaPeerable` types. The associated tests cover identity, checked Java
assignability, callback identity, virtual dispatch and lifetime behavior.

That establishes a useful implementation pattern, not proof that every SDK
version transition is compatible. Identity must use Java object identity,
not equality of numeric JNI reference values.

### 4. A common outbound view can sometimes span a class/interface change

The JNI specification explicitly allows `GetMethodID` to find an instance
method on a **class or interface**. Ordinary `Call<Type>Method` operations then
invoke that method on the Java object.

Therefore, if two SDK versions provide a compatible instance method with the
same descriptor, an ordinary managed view can potentially call it in both
versions without requiring its own CLR shape to switch from class to interface.

Conceptually, not an already-shipped API:

```csharp
var client = LocationClientView.FromPeer (peer);
var location = await client.GetLastLocationAsync ();
```

The C# view and its public managed contract can stay stable. Its peer carries
the Java identity, and its JNI operation is validated against the selected
native definition.

This does not make constructors, nonvirtual base calls, inheritance, method
staticness or subclassing automatically compatible. Each operation has its own
requirements. In particular, JNI-call compatibility is not proof that an old
compiled Java caller using `invokevirtual` will work against a new interface.

### 5. Treat callbacks and subclassing as stronger contracts

When C# implements an interface or overrides a Java class, the Java-side adapter
must match the real selected SDK:

- Which interface must be implemented, or which class must be extended?
- What constructor and superclass are valid?
- Which methods and exact descriptors must be present?
- Is the class final, or is a required constructor inaccessible?

The app's resolved native graph should select the appropriate generated
adapter. Ordinary views can still share its original managed callback owner.
Do not silently substitute an adapter when those requirements are unsatisfied.

This is the boundary where a generic handle wrapper alone is not enough.
It also cannot repair another precompiled Java library that expects the wrong
class/interface ABI.

### 6. Validate required capabilities, not the entire catalog unconditionally

Record per-operation native requirements: member name, descriptor, invocation
kind, relevant type/assignability constraints, constructor obligations and
callback requirements.

After Gradle resolution, check the selected graph against the contracts the app
uses or explicitly retains. Resolve inherited methods and compatibility bridges,
not just declarations on one exact class.

A catalog can expose a newer method without requiring every app that references
its assembly to have that native method. Calling or retaining the method creates
the requirement. Dynamic access must declare its requirements explicitly.

Version annotations can help users, but native contract facts are the final
authority. Do not equate equal Maven versions across all Google libraries with
compatibility; Google versions those libraries independently.

## Recommended next implementation slice

First fix public-versus-unstable-infrastructure classification and rerun the
supported-contract audit. The 265 `zz...` kind conflicts should not drive us into
designing hundreds of public versioned binding families.

Then consolidate the stable public owners and add genuine ABI variants only
where the supported surface actually requires them. Use shared-peer views for
outbound differences, and native-graph-specific adapters for callback/subclass
differences.

The decisive new fixtures should include:

1. A type moving between artifacts while precompiled consumers keep working.
2. One binary name implemented as a class in one native fixture and an interface
   in another, with compatible outbound slots and one stable managed view.
3. An unsupported constructor/subclass/callback contract rejected for the wrong
   native variant.
4. An unrelated reused obfuscated name that never becomes a conflicting public
   CLR type, plus a required obfuscated callback exception which remains usable.
5. A changed or removed native slot rejected instead of guessed.

The existing shared-peer fixtures help with identity and lifetime, but do not
replace those variant-specific tests.

## Sources

- [Ads filtering metadata](https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/com.google.android.gms/play-services-ads/Transforms/Metadata.xml#L34-L41).
- [GASS filtering metadata](https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/com.google.android.gms/play-services-gass/Transforms/Metadata.xml#L6-L12).
- [Drive filtering and exceptions](https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/com.google.android.gms/play-services-drive/Transforms/Metadata.xml#L24-L35).
- [Ads Lite metadata, including explicit obfuscation exceptions](https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/com.google.android.gms/play-services-ads-lite/Transforms/Metadata.xml).
- [Current managed Location interface additions](https://github.com/dotnet/android-libraries/blob/2393441675a6d82bb817ab7aa2df698c7ec929ce/source/com.google.android.gms/play-services-location/Additions/TaskExtensions.cs).
- [Google's October 13, 2022 class/interface announcement](https://developers.google.com/android/guides/releases#october_13_2022).
- [JNI `GetMethodID` and instance method invocation](https://docs.oracle.com/en/java/javase/21/docs/specs/jni/functions.html#getmethodid).
- [Google Play services version compatibility](https://developers.google.com/android/guides/versioning).
- [CLR type forwarding](https://learn.microsoft.com/en-us/dotnet/standard/assembly/type-forwarding).
