# Third-party SDK binding probes

These credential-free C# probes consume actual generated SDK bindings. They do
not use published Android managed binding NuGets or per-library transforms.
Each probe is compiled separately with its own resolved native dependency graph:
do not combine SDK validation snapshots with differing shared dependencies.

| Probe | Pinned inputs | Required checks |
| --- | --- | --- |
| MapLibre | `org.maplibre.gl:android-sdk-opengl:13.7.1` | 6 |
| Sentry | Android, Compose Android and Kotlin extensions `8.62.0` | 6 |
| Koin | core, Android, AndroidX Compose and Android Java helpers `4.2.2` | 6 |

`OfflineProbeActivity` checks exact counts and unique check names, applies a
bounded timeout, and reports failures rather than treating installation as a pass.
The probes run with full managed trimming/R8 and with NativeAOT.

MapLibre renders an embedded, source-free background style into a real native
snapshot, verifies its pixel color and coordinate projection, and returns the
PNG as test evidence. It uses the OpenGL artifact for the software-rendered
emulator. The same-version default Vulkan artifact binds successfully but needs
a Vulkan-compatible GPU; its native GPU failure is not a generator failure.
There are no tile, glyph or style downloads and no account token.

Sentry uses the public custom-transport API with a recording transport and drops
the event in a managed before-send callback. Android automatic initialization is
disabled in the probe manifest. The test verifies the callback executes through
JNI, keeps peer identity, and makes no send attempt. It does not upload events or
test full profiling/session replay.

Koin installs a singleton definition using an explicit Kotlin class token and a
managed `Function2` factory. Generic views validate typed resolution, caching,
missing/null results and property conversion. Kotlin inline/reified-only DSL
operations are not interchangeable with the callable explicit-`KClass` overloads.
The Compose integration's generated surface compiles; Compose UI rendering is
not part of these probes.

Artifact-layout validation references the actual per-Maven-artifact DLL tree,
not the monolithic generation input. The tree and its ownership report must be
verified before the probe runs. A complete selected native graph, and the
source-built Kotlin JNI bridge, are supplied separately to the Android app.
