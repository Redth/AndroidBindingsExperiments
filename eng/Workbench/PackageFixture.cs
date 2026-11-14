using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace BindingWorkbench;

public sealed class PackageFixture(Workbench workbench)
{
    readonly List<object> results = [];
    string work = "";
    Dictionary<string, string> environment = [];

    static void Require(bool condition, string message)
    {
        if (!condition) throw new WorkbenchException(message);
    }

    async Task<string> Stage(string name, IEnumerable<string> arguments, bool expectFailure = false)
    {
        string[] command = arguments.ToArray();
        var clock = Stopwatch.StartNew();
        try
        {
            string text = await workbench.Runner.Run(command[0], command.Skip(1), work, environment,
                Path.Combine(work, name + ".log"), expectFailure);
            results.Add(new
            {
                name,
                command,
                status = "passed",
                exitCode = workbench.Runner.LastExitCode,
                expectedFailure = expectFailure,
                seconds = clock.Elapsed.TotalSeconds
            });
            Files.WriteJson(Path.Combine(work, "results.json"), new { status = "in-progress", results });
            return text;
        }
        catch (Exception error) when (error is CommandException or IOException)
        {
            results.Add(new { name, command, status = "failed", error = error.Message });
            Files.WriteJson(Path.Combine(work, "results.json"), new { status = "failed", results });
            throw;
        }
    }

    public async Task Run()
    {
        var options = workbench.Options;
        if (options.Jdk is null || options.AndroidSdk is null || options.WorkDir is null)
            throw new WorkbenchException("verify-package requires --work-dir, --jdk/JAVA_HOME and --android-sdk/ANDROID_HOME.");
        work = Path.GetFullPath(options.WorkDir);
        Require(!File.Exists(work) && !Directory.Exists(work), "Use a new --work-dir for package verification.");
        workbench.Inspect();
        Directory.CreateDirectory(work);
        string fixture = Path.Combine(workbench.Source, "tests", "BindingProjection", "GeneratorPackage");
        foreach (string directory in new[] { "Binding", "Consumer", "Native", "Multi" })
            Files.CopyTree(Path.Combine(fixture, directory), Path.Combine(work, directory));
        File.Copy(Path.Combine(fixture, "PackageTasks.proj"), Path.Combine(work, "PackageTasks.proj"));
        string cache = Path.Combine(workbench.Root, ".artifacts", "fixture-packages", Files.Hash(workbench.PackagePath));
        environment = new() { ["NUGET_PACKAGES"] = cache, ["DOTNET_gcServer"] = "0" };
        string javaBin = Path.Combine(Path.GetFullPath(options.Jdk), "bin");
        string extension = OperatingSystem.IsWindows() ? ".exe" : "";
        string classes = Path.Combine(work, "classes");
        Directory.CreateDirectory(classes);
        await Stage("javac", new[] { Path.Combine(javaBin, "javac" + extension), "-parameters", "-d", classes }
            .Concat(Directory.EnumerateFiles(Path.Combine(work, "Native"), "*.java", SearchOption.AllDirectories).Order()));
        await Stage("jar", [Path.Combine(javaBin, "jar" + extension), "cf", Path.Combine(work, "Binding", "preview.jar"), "-C", classes, "."]);
        await Stage("sdk-version", [options.Dotnet, "--version"]);
        string[] properties = [
            "-p:AndroidSdkDirectory=" + Path.GetFullPath(options.AndroidSdk),
            "-p:JavaSdkDirectory=" + Path.GetFullPath(options.Jdk),
            "-p:GeneratorPackageVersion=" + Workbench.Version,
        ];
        string[] sources = ["--source", workbench.Feed, "--source", "https://api.nuget.org/v3/index.json"];
        Task<string> Build(string name, string project = "Consumer/Preview.Consumer.csproj", bool restore = false, string[]? extra = null) =>
            Stage(name, new[] { options.Dotnet, "build", project, "-c", "Debug", "-nr:false", "-v:normal" }
                .Concat(restore ? sources : ["--no-restore"]).Concat(properties).Concat(extra ?? []).Append("-bl:" + name + ".binlog"));

        await Build("semantic", restore: true);
        Workbench.VerifyRestoredPackage(workbench.PackagePath, cache);
        string query = await Stage("sdk-paths", new[] { options.Dotnet, "msbuild", "Binding/Preview.Binding.csproj",
            "-nologo", "-t:_ResolveMonoAndroidSdks",
            "-getProperty:AndroidNETSdkVersion,MonoAndroidToolsDirectory,_AndroidSemanticBindingTargetsImported" }.Concat(properties));
        int jsonStart = query.IndexOf('{');
        if (jsonStart < 0) throw new WorkbenchException("MSBuild SDK query did not contain JSON metadata.");
        var response = Files.RequireObject(JsonNode.Parse(query[jsonStart..]), "MSBuild SDK response");
        var sdk = Files.RequireObject(response["Properties"], "MSBuild SDK response.Properties");
        Require(string.Equals(Files.RequireString(sdk["_AndroidSemanticBindingTargetsImported"],
            "SDK _AndroidSemanticBindingTargetsImported"), "true", StringComparison.OrdinalIgnoreCase),
            "The package did not import after the Android SDK.");
        string tools = Files.RequireString(sdk["MonoAndroidToolsDirectory"], "SDK MonoAndroidToolsDirectory");
        var workloadHashes = new[] { "Xamarin.Android.Build.Tasks.dll", "Xamarin.Android.Bindings.Core.targets",
            "Xamarin.Android.Bindings.ClassParse.targets", "generator.dll", "class-parse.dll" }
            .Select(name => Path.Combine(tools, name)).ToDictionary(path => path, Files.Hash);
        string rootDirectory = Path.Combine(work, "Binding/obj/Debug/net11.0-android/semantic-bindings");
        string manifest = Directory.EnumerateFiles(rootDirectory, "Preview.Binding.projitems", SearchOption.AllDirectories).Single();
        string root = Files.ParentDirectory(Files.ParentDirectory(Files.ParentDirectory(manifest)));
        string api = Path.Combine(root, "api.xml");
        string descriptor = Path.Combine(root, "api-descriptor.json");
        Require(XDocument.Load(api).Root?.Attribute("api-source")?.Value == "class-parse", "Missing raw parser facts.");
        Require(Directory.EnumerateFiles(root, "*.semantics.json", SearchOption.AllDirectories).Any(), "Missing semantic compilation report.");
        string[] generated = Directory.EnumerateFiles(Path.Combine(root, "generated"), "*.cs", SearchOption.AllDirectories).Order().ToArray();
        Require(generated.Any(p => File.ReadAllText(p).Contains("RenamedEcho")), "Metadata was not applied.");
        Require(generated.Any(p => File.ReadAllText(p).Contains("namespace PackagePreview")), "Namespace replacement was not applied.");
        const string rule = "-keep class net.dot.android.packagetest.Box {";
        Require(File.ReadAllText(Path.Combine(root, "generated", "semantic-binding.pro")).Contains(rule), "Missing scoped JNI retention rules.");
        string aar = Directory.EnumerateFiles(Path.Combine(work, "Binding/bin/Debug/net11.0-android"), "*.aar").Single();
        using (var zip = ZipFile.OpenRead(aar))
        {
            Require(zip.Entries.Where(e => e.FullName.Contains("proguard")).Any(entry =>
            {
                using var reader = new StreamReader(entry.Open());
                return reader.ReadToEnd().Contains(rule);
            }), "Scoped rules did not reach the SDK binding AAR.");
        }
        var times = new[] { api, descriptor, manifest }.Concat(generated)
            .ToDictionary(path => path, File.GetLastWriteTimeUtc);
        string incremental = await Build("incremental");
        Require(!incremental.Contains("Semantic binding tool:"), "An unchanged build reran compiler tools.");
        Require(times.All(pair => File.GetLastWriteTimeUtc(pair.Key) == pair.Value), "An unchanged build rewrote output.");
        File.Delete(generated[0]);
        await Build("missing-source");
        Require(File.Exists(generated[0]), "A missing generated source did not regenerate.");
        DateTime descriptorTime = File.GetLastWriteTimeUtc(descriptor);
        await Task.Delay(1200);
        File.SetLastWriteTimeUtc(Path.Combine(work, "Binding", "Metadata.xml"), DateTime.UtcNow);
        Require((await Build("metadata-change")).Contains("Semantic binding tool:"), "A metadata change did not regenerate.");
        Require(File.GetLastWriteTimeUtc(descriptor) == descriptorTime, "A metadata-only change reran the native descriptor.");
        await Task.Delay(1200);
        File.SetLastWriteTimeUtc(Path.Combine(work, "Binding", "preview.jar"), DateTime.UtcNow);
        string nativeChange = await Build("native-change");
        Require(nativeChange.Contains("descriptor.ApiDescriptorKt") || nativeChange.Contains("-classpath"), "A native change did not regenerate its descriptor.");
        Require(!(await Build("legacy", extra: ["-p:AndroidBindingGenerator=Legacy"])).Contains("Semantic binding tool:"),
            "Legacy selection invoked the semantic compiler.");
        await Build("semantic-again");
        string invalid = await Stage("invalid-mode", new[] { options.Dotnet, "build", "Binding/Preview.Binding.csproj",
            "--no-restore", "-nr:false", "-p:AndroidBindingGenerator=Invalid" }.Concat(properties), expectFailure: true);
        Require(invalid.Contains("XA4350"), "Invalid selection did not produce its explicit diagnostic.");
        await Build("multitarget", "Multi/Preview.Multi.csproj", restore: true, extra: ["-m:2"]);
        Require(File.Exists(Path.Combine(work, "Multi/bin/Debug/net10.0/Preview.Multi.dll")), "Non-Android inner build was lost.");
        Require(File.Exists(Path.Combine(work, "Multi/bin/Debug/net11.0-android/Preview.Multi.dll")), "Android inner build was lost.");
        await Stage("design-time", new[] { options.Dotnet, "msbuild", "Binding/Preview.Binding.csproj", "-t:Compile",
            "-p:DesignTimeBuild=true", "-p:BuildingInsideVisualStudio=true", "-p:SkipCompilerExecution=true", "-nr:false" }.Concat(properties));
        string task = Path.Combine(cache, Workbench.Package.ToLowerInvariant(), Workbench.Version, "tools", "tasks", Workbench.Package + ".dll");
        await Stage("strict-package-tasks", [options.Dotnet, "msbuild", "PackageTasks.proj", "-t:Check", "-m:2", "-mt",
            "-nr:false", "-warnaserror:MSB4286,MSB4287,MSB4288", "-p:PackageTaskAssembly=" + task, "-p:DotnetHost=" + options.Dotnet,
            "-p:BindingManifest=" + manifest, "-p:BindingOwnershipReport=" + Path.Combine(root, "generated", "ownership.json")]);
        var consumerAssets = Files.RequireObject(
            JsonNode.Parse(File.ReadAllText(Path.Combine(work, "Consumer", "obj", "project.assets.json"))),
            "consumer project.assets.json");
        var libraries = Files.RequireObject(consumerAssets["libraries"], "consumer project.assets.json.libraries");
        Require(!libraries.ContainsKey(Workbench.Package + "/" + Workbench.Version),
            "The project-private compiler package leaked into the downstream consumer.");
        Require(workloadHashes.All(pair => Files.Hash(pair.Key) == pair.Value), "Installed workload files changed.");
        Workbench.VerifyRestoredPackage(workbench.PackagePath, cache);
        Files.WriteJson(Path.Combine(work, "results.json"), new
        {
            status = "passed",
            checks = results.Count,
            runner = ".NET/BCL",
            package = workbench.PackagePath,
            sha256 = Files.Hash(workbench.PackagePath),
            sdk,
            unmodifiedWorkloadHashes = workloadHashes,
            results,
            deviceResults = Array.Empty<object>(),
        });
        Console.WriteLine($"Passed {results.Count} no-device package stages; results: {Path.Combine(work, "results.json")}");
    }
}
