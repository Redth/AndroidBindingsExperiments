# Experimental binding compiler package

A **local preview**, not a released or production-supported compiler. Install
the build-only package in an SDK-style Android binding project:

```xml
<PropertyGroup>
  <TargetFramework>net11.0-android</TargetFramework>
  <IsBindingProject>true</IsBindingProject>
  <AndroidBindingGenerator>Semantic</AndroidBindingGenerator>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="Microsoft.Android.BindingGenerator.Semantic"
                    Version="0.1.0-preview.1" PrivateAssets="all" />
</ItemGroup>
```

Blank or `Legacy` selects the installed workload's original generator.
Selection is project-local, including Android inner builds of multi-targeted
projects. It does not rewrite existing DLLs, change assembly ownership, resolve
Maven dependencies, or change whether the SDK packs native libraries.

The package owns matched class-parse/generator tools and a prebuilt official
Kotlin/ASM descriptor reader. Its targets import after the Android SDK, parse
unadjusted facts, describe bound/reference archives, and feed semantic generated
C# into normal SDK compilation. Tools and descriptor dependencies are never
application runtime assets. No installed SDK files are modified.

Initial end-to-end verification used macOS ARM64, .NET SDK
`11.0.100-rc.2.26504.105`, Android workload `37.2.0-rc.2.84`, and JDK 21. The
preview version-gates .NET 11/Android 37.x; Windows/Linux and every other 37.x
revision still require their own integration run.

`Microsoft.Android.Kotlin` is **separate**. Add a normal, non-private reference
when typed generic views and Kotlin Task/Flow conveniences are needed; the
compiler detects that resolved runtime reference. Include its source-built
native bridge once in the app's existing Gradle graph as described in that
package's README. The compiler does not inject package references during build.
The matching local runtime package is `Microsoft.Android.Kotlin` version
`0.1.0-preview.1`.

Existing metadata/enum mappings, namespace replacements, Javadocs, and
handwritten additions remain inputs. Review the generated public API when
migrating: compiler selection is not a promise of binary-equivalent API.
Generation uses configuration-specific directories under
`obj/.../semantic-bindings`, rather than reusing legacy intermediates or writing
into the restored package. Native/reference/transform changes and tool upgrades
invalidate the selected output.

The preview exports per-binding R8 consumer rules through the SDK's ordinary
AAR/project-reference path. Views and Kotlin adapters invoke JNI slots without
necessarily calling the corresponding raw managed member, so the stock
trimmer cannot always infer those native requirements. Until precise
projection-aware retention is available, the preview conservatively preserves
public/protected members of **locally emitted peer classes**, not every class
in the native dependency graph. This is an explicit size tradeoff, not a claim
of minimal R8 retention.

## Building the local preview

From a prepared repository, with JDK 21:

```sh
bash build-tools/gradle/gradlew --no-daemon --console plain \
  -p external/Java.Interop/tools/jvm-api-descriptor test installDist
./dotnet-local.sh pack \
  src/Microsoft.Android.BindingGenerator.Semantic/Microsoft.Android.BindingGenerator.Semantic.csproj \
  -c Debug -o /path/to/local-feed -nr:false
```

Restore the sample from that local feed in addition to normal NuGet feeds. The
compiler tools target .NET 10 and permit major roll-forward; the preview's task
hosting requires modern MSBuild with TaskEnvironment support. Start with .NET
11 Android workloads; old Xamarin/.NET Framework hosts are not supported.

This is not a whole-corpus build at every application compile. It binds the
project's selected native inputs. Existing dependency DLLs are ordinary
references and are not regenerated. The first supported workload/platform
matrix and exact validation commands are recorded by the package integration
tests; broader claims require those tests on each workload and OS.
See `tests/BindingProjection/GeneratorPackage/README.md` for the stock-workload
fixture and explicit-device verification command.
