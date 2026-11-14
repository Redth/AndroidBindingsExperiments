# Microsoft.Android.Kotlin

Optional shared Kotlin interoperation support for .NET for Android. The managed
assembly is owned by this package, not regenerated into each binding library.
Its public interoperation boundary uses `IJavaPeerable` and does not depend on
the assembly identity of a particular Kotlin/coroutines binding package.

The package contains the managed runtime and the native bridge's **source**.
It does not contain Kotlin/coroutines binaries or a precompiled bridge JAR/AAR.
The native bridge must be included once in the app's existing Gradle dependency
graph, together with the libraries being bound.

## Native Gradle graph

The package exports `$(MicrosoftAndroidKotlinNativeProjectDirectory)` to MSBuild.
Pass that restored directory to the existing native graph/build integration.
Include it as a Gradle project, for example in the native graph's settings:

```kotlin
include(":dotnet-kotlin-runtime")
project(":dotnet-kotlin-runtime").projectDir =
    file(providers.gradleProperty("dotnetKotlinRuntimeSource").get())
```

Declare the Kotlin JVM plugin version in the graph's normal plugin management,
using the same supported Kotlin compiler version as the other modules. The
bridge module deliberately does not choose a different plugin version when
included in an existing build. Its settings file supplies a version only for
standalone development of this package.
Included builds write their outputs under the consuming root project's build
directory, not the restored package directory.

Add a normal project dependency where native dependencies are resolved:

```kotlin
dependencies {
    implementation(project(":dotnet-kotlin-runtime"))
}
```

Consume the bridge's project output and the resolved dependency tree through
the existing Gradle integration. Gradle selects Kotlin stdlib and coroutines
versions across the whole graph. The bridge declares Kotlin stdlib 2.0.0 and
coroutines core 1.9.0 minimum inputs; higher graph-selected versions are not
replaced with a second private copy. Do not separately embed support NuGet AARs
on top of the selected native graph.

The `assembleRelease` task also emits an AAR containing only the authored bridge
classes, for native integrations that consume AAR outputs. Reference archives
under `outputs/references` are compiler inputs, not an additional app dependency
graph or package payload.

## Managed use

The experimental binding compiler adds typed `Task` methods, typed Flow
enumeration facades, and verified Kotlin default-argument adapters. Raw bindings
remain available. Complex contracts retain native peers where a richer managed
projection cannot yet preserve their semantics.

`KotlinCoroutines.RunAsync` invokes the real suspend ABI through a shared native
operation. Cancellation cancels and joins the owned coroutine. An optional
native context supplies a dispatcher and parent Job; a failed child Task does
not cancel the caller's Job. Without a context, execution is unconfined, not
implicitly Android Main. Native Main dispatch requires coroutines-android in
the selected graph.

Flow adapters use one native subscription and a reusable `ValueTask` source per
enumeration. Acknowledgement prevents prefetch; early disposal and failures
cancel and join native collection. The consumer must await each `MoveNextAsync`
once before requesting another value.

`KotlinCoroutines.FromTask` supports implementations of bound suspend contracts.
It receives an opaque continuation, propagates managed failures without losing
their original exception identity, links native cancellation to the managed
implementation, and waits for managed cleanup before native cancellation
finishes.

`KotlinOptional<T>` distinguishes omission from an explicitly supplied value,
including null and zero. Generated adapters invoke the verified JVM `$default`
helper so Kotlin evaluates default expressions; C# does not copy their values.

Value-class `Box`/`Unbox` helpers use verified JVM representation operations.
`Create`/`CreateValue` invokes Kotlin's constructor implementation and preserves
validation/initialization. A raw representation constructor or `Box` is not a
substitute for constructor validation. Returned existing peers are not
indiscriminately disposed.

Typed `List`, `Set`, and `Map` results retain nested element nullability.
`KotlinList<T>`, `KotlinSet<T>`, and `KotlinMap<TKey,TValue>` are live, read-only
views, not snapshots or promises that the underlying Kotlin collection is
immutable. Maps expose typed entries rather than `IReadOnlyDictionary`, because
Java maps may contain null keys. Each view retains its native `Peer`.

### Generic views

The separately opt-in `--experimental-generic-facades` generator option emits
ordinary managed `XView<T>` wrappers over the canonical raw peer. They preserve
class/method type parameters, inherited substitutions, typed fields, generic
Task/Flow results, and read-only collection results. They are not registered
JNI types and do not create a second managed peer owner.

`JvmType<T>` provides checked JNI conversion and optional explicit converters
for nested managed views. Generated peers and views implement the ordinary
managed `IJavaProjection<out TPeer>` contract, exposing the same canonical peer.
Representable native upper bounds become C# constraints on that shared contract,
for both class and method type parameters. For example, a record response can
accept either a canonical record peer or a record view, but not an unrelated
`string` type argument.

When C# constraints are emitted, the compiler also generates a distinctly named
`XMappedView` companion. It retains the unconstrained converter-based API for
custom CLR representations. Both views keep native assignability checks; the
mapped companion is not permission to pass an unrelated Java object. References
to nested generic views select the mapped companion when a C# projection
constraint cannot be established. Neither companion registers another JNI type.

Converted scalar/collection bounds, dependent type-variable bounds, unavailable
peer contracts and parameterized/recursive bounds remain explicit runtime
checks rather than false CLR guarantees. Generated type-parameter documentation
preserves the full native bounds, and the projection report records constrained,
mapped and runtime-only decisions. This is not full static enforcement of Java
F-bounded contracts. Already compiled dependencies without the projection
contract remain usable through the runtime-checked path.
Choose a nullable value argument, such as `int?`, when native null is possible:
unconstrained C# `T?` does not turn an `int` type argument into `Nullable<int>`.
Null-to-nonnullable-value conversion throws rather than silently returning zero.

`JvmArray<T>` represents a live reference array: `JvmArray<int>` contains boxed
`Integer` elements, whereas an explicit native `int[]` remains a primitive array.
Collection reads through lower-bounded or star projections widen safely to
opaque nullable objects. Mutable nominal existential types retain raw peers
with a projection diagnostic rather than acquiring unsafe typed setters.

## Compatibility and experimental scope

The parser recognizes Kotlin metadata 1.1-1.9 and 2.0-2.4. Unknown versions
produce a diagnostic and leave raw JVM bindings available rather than inventing
semantic facts. Real compiler/device fixtures cover Kotlin 2.0.21, 2.1.20, and
2.4.21 with coroutines 1.9.0; recognizing a metadata version is not a claim that
every language feature of that version is projected.

The official Kotlin/ASM descriptor frontend is documented in
[`jvm-api-descriptor`](../../external/Java.Interop/tools/jvm-api-descriptor/README.md).
It uses the supported Kotlin metadata reader for source visibility, scoped
generic types, and other language facts. Its dependencies run only in the build
tool process, not in the app. C# still owns managed projection and JNI lowering;
the existing bytecode frontend remains responsible for some ABI/helper facts.
Strictly unsupported or lenient metadata is reported as unverified, not treated
as a confidently supported Kotlin API.

The generated defaults cover functions and public constructors of concrete
classes, including masked, suspend, interface, static, and extension helpers.
Protected/abstract constructor conveniences and unsupported generic forms
remain raw. Managed suspend implementations override the raw contract and call
`FromTask`; no automatic managed implementation class is generated.

This package and the generator switches are experimental and opt-in. The
package is not injected into every Android app or published by the build.

```sh
./dotnet-local.sh pack src/Microsoft.Android.Kotlin/Microsoft.Android.Kotlin.csproj \
    -o /absolute/path/to/local-packages
```
