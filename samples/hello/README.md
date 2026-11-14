# Hello native binding

This deliberately small sample has three pieces:

1. `Native/Greeter.java` is an authored Java API.
2. `Binding/Hello.Binding.csproj` binds its JAR using the project-private
   semantic compiler package.
3. `Consumer/` is a separate C# project that calls the generated `Greeter.Greet`.

From the workbench root, after bootstrap:

```sh
python3 eng/workbench.py pack
python3 eng/workbench.py generate
```

The runner compiles the Java source and builds a disposable copy under
`.artifacts/hello/`. Inspect `Binding/obj/Debug/net11.0-android/semantic-bindings/`
there for unadjusted facts, the official descriptor, generated C# and reports.
The resulting binding and consumer DLLs are under their normal `bin/Debug`
directories in that disposable copy.

An installed .NET 11 Android workload, Android SDK and JDK 21 are required.
`JAVA_HOME` and `ANDROID_HOME` may be supplied explicitly through the runner's
`generate --jdk ... --android-sdk ...` options. This sample compiles but does
not install or run an Android app. It has no Kotlin runtime dependency.
