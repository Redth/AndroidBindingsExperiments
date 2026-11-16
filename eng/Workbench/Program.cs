using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BindingWorkbench;

public sealed class WorkbenchException(string message) : Exception(message);
public sealed class CommandException(int exitCode, string message) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public sealed class Options
{
    public string Command { get; set; } = "help";
    public string? Source { get; set; }
    public string Dotnet { get; set; } = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    public string? Jdk { get; set; } = Environment.GetEnvironmentVariable("JAVA_HOME");
    public string? AndroidSdk { get; set; } = Environment.GetEnvironmentVariable("ANDROID_HOME")
        ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
    public string? WorkDir { get; set; }
    public List<string> ToolArguments { get; } = [];

    public static Options Parse(string[] arguments)
    {
        var result = new Options();
        bool commandSeen = false;
        for (int i = 0; i < arguments.Length; i++)
        {
            string value = arguments[i];
            string Next() => ++i < arguments.Length ? arguments[i]
                : throw new WorkbenchException($"Missing value for {value}.");
            switch (value)
            {
                case "--source": result.Source = Next(); break;
                case "--dotnet": result.Dotnet = Next(); break;
                case "--jdk": result.Jdk = Next(); break;
                case "--android-sdk": result.AndroidSdk = Next(); break;
                case "--work-dir": result.WorkDir = Next(); break;
                case "--help" or "-h": result.Command = "help"; return result;
                default:
                    if (commandSeen || value.StartsWith('-'))
                        throw new WorkbenchException($"Unexpected argument: {value}");
                    result.Command = value;
                    commandSeen = true;
                    if (value == "tool")
                    {
                        result.ToolArguments.AddRange(arguments.Skip(i + 1));
                        return result;
                    }
                    break;
            }
        }
        return result;
    }
}

public sealed class FileRecord
{
    public string Path { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? WorktreeSha256 { get; set; }
    public long Bytes { get; set; }
    public string Mode { get; set; } = "100644";
}

public sealed class SourceManifest
{
    public int SchemaVersion { get; set; }
    public string Repository { get; set; } = "";
    public string Commit { get; set; } = "";
    public string AdditionsRoot { get; set; } = "";
    public List<FileRecord> Patches { get; set; } = [];
    public List<FileRecord> Additions { get; set; } = [];
    public List<FileRecord> TrackedOutputs { get; set; } = [];
}

public static class Files
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
        ?? throw new WorkbenchException($"Empty JSON document: {path}");
    public static string ParentDirectory(string path) => Path.GetDirectoryName(path) is { Length: > 0 } directory
        ? directory : throw new WorkbenchException($"Path has no parent directory: {path}");
    public static JsonObject RequireObject(JsonNode? node, string context) => node is JsonObject value
        ? value : throw new WorkbenchException($"Expected JSON object for {context}.");
    public static string RequireString(JsonNode? node, string context) =>
        node is JsonValue value && value.TryGetValue<string>(out string? text) && !string.IsNullOrWhiteSpace(text)
            ? text : throw new WorkbenchException($"Expected nonempty JSON string for {context}.");
    public static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(ParentDirectory(path));
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json) + "\n", new UTF8Encoding(false));
    }
    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Relative(string value)
    {
        string[] parts = value.Split('/');
        if (string.IsNullOrEmpty(value) || value.Contains('\\') || value.Contains(':')
            || System.IO.Path.IsPathRooted(value)
            || parts.Any(p => p is "" or "." or ".." or ".git"))
            throw new WorkbenchException($"Unsafe snapshot path: {value}");
        return System.IO.Path.Combine(parts);
    }

    public static string Under(string root, string relative)
    {
        string path = root;
        foreach (string part in Relative(relative).Split(System.IO.Path.DirectorySeparatorChar))
        {
            path = System.IO.Path.Combine(path, part);
            if (new FileInfo(path).LinkTarget is not null
                || ((File.Exists(path) || Directory.Exists(path))
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new WorkbenchException($"Unsupported symlink: {path}");
        }
        return path;
    }

    public static void VerifyFile(string path, FileRecord record)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new WorkbenchException($"Missing file or unsupported symlink: {path}");
        string actual = Hash(path);
        if (actual != record.Sha256 && actual != record.WorktreeSha256)
            throw new WorkbenchException($"Checksum mismatch: {path}");
    }

    public static void SameSet(IEnumerable<string> actual, IEnumerable<string> expected, string message)
    {
        var differences = new HashSet<string>(actual, StringComparer.Ordinal);
        differences.SymmetricExceptWith(expected);
        if (differences.Count != 0)
            throw new WorkbenchException($"{message}: {string.Join(", ", differences.Order())}");
    }

    public static void RequireEmpty(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new WorkbenchException($"Refusing to overwrite symlinked source directory: {path}");
        if (File.Exists(path) || (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()))
            throw new WorkbenchException($"Refusing to overwrite nonempty source directory: {path}");
    }

    public static void CopyTree(string source, string destination)
    {
        foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Under(destination, System.IO.Path.GetRelativePath(source, path).Replace('\\', '/'));
            Directory.CreateDirectory(ParentDirectory(target));
            File.Copy(path, target, true);
        }
    }
}

public sealed class ProcessRunner(string root)
{
    public int LastExitCode { get; private set; }
    readonly string temporary = Path.Combine(root, ".artifacts", "tmp");

    static string ResolveExecutable(string executable, string searchPath)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(executable);
        string[] extensions = OperatingSystem.IsWindows()
            ? ["", ".exe", ".com", ".cmd", ".bat"] : [""];
        foreach (string directory in searchPath.Split(Path.PathSeparator))
            foreach (string extension in extensions)
            {
                string path = Path.Combine(directory, executable + extension);
                if (!File.Exists(path)) continue;
                if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path)
                    & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0) continue;
                return Path.GetFullPath(path);
            }
        throw new WorkbenchException($"Required executable not found in child PATH: {executable}");
    }

    public void Prepare()
    {
        Directory.CreateDirectory(temporary);
    }

    public async Task<string> Run(string executable, IEnumerable<string> arguments, string? cwd = null,
        IReadOnlyDictionary<string, string>? environment = null, string? log = null, bool expectFailure = false)
    {
        string[] args = arguments.ToArray();
        Console.WriteLine("+ " + executable + " " + string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)));
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd ?? root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        foreach (string variable in new[] { "TMPDIR", "TMP", "TEMP" }) info.Environment[variable] = temporary;
        if (environment is not null)
            foreach (var pair in environment) info.Environment[pair.Key] = pair.Value;
        info.FileName = ResolveExecutable(executable, info.Environment["PATH"] ?? "");
        using var process = Process.Start(info) ?? throw new WorkbenchException($"Cannot start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        LastExitCode = process.ExitCode;
        string text = await output + await error;
        if (log is null) Console.Write(text);
        else
        {
            Directory.CreateDirectory(Files.ParentDirectory(log));
            await File.WriteAllTextAsync(log, text);
        }
        if (expectFailure ? process.ExitCode == 0 : process.ExitCode != 0)
            throw new CommandException(process.ExitCode == 0 ? 1 : process.ExitCode,
                $"{executable} exited {process.ExitCode}; expected {(expectFailure ? "a diagnostic failure" : "success")}."
                + (log is null ? "" : $" See {log}.") + "\n" + string.Join("\n", text.Split('\n').TakeLast(16)));
        return text.Trim();
    }
}

public sealed class Workbench
{
    public const string SourceUrl = "https://github.com/dotnet/android.git";
    public const string SourceCommit = "097fd4f7eb6cab27a798e04254ac838c9e8d0369";
    public const string Package = "Microsoft.Android.BindingGenerator.Semantic";
    public const string Version = "0.1.0-preview.workbench.3";
    public static readonly string[] Tools = ["generator", "class-parse", "grouped-bindings", "binding-compatibility"];
    public string Root { get; }
    public string Source { get; }
    public Options Options { get; }
    public ProcessRunner Runner { get; }
    public SourceManifest Manifest { get; }
    public string Feed => Path.Combine(Root, ".artifacts", "feed");
    public string PackagePath => Path.Combine(Feed, $"{Package}.{Version}.nupkg");
    public string ManifestPath => Path.Combine(Root, "upstream", "manifest.json");
    public string ManifestHash { get; }

    public Workbench(string root, Options options)
    {
        Root = root;
        Options = options;
        Source = Path.GetFullPath(options.Source ?? Path.Combine(root, ".artifacts", "source"));
        ManifestHash = Files.Hash(ManifestPath);
        var snapshotChange = Files.ReadJson<JsonObject>(Path.Combine(Root, "provenance", "snapshot-change.json"));
        var sourceChange = Files.RequireObject(snapshotChange["source"], "snapshot change source");
        Files.VerifyFile(ManifestPath, new FileRecord
        {
            Sha256 = Files.RequireString(sourceChange["currentManifestSha256"], "current source manifest hash")
        });
        Manifest = Files.ReadJson<SourceManifest>(ManifestPath);
        if (Manifest.SchemaVersion != 2 || Manifest.Repository != SourceUrl
            || Manifest.Commit != SourceCommit || Manifest.AdditionsRoot != "additions")
            throw new WorkbenchException("The original source pin or manifest format changed.");
        Runner = new ProcessRunner(root);
        Runner.Prepare();
    }

    public async Task<byte[]> Preserved(string relative, FileRecord record)
    {
        string path = Files.Under(Root, relative);
        Files.VerifyFile(path, record);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        if (Files.Hash(bytes) != record.Sha256) throw new WorkbenchException($"Preserved file hash differs: {relative}");
        return bytes;
    }

    public async Task VerifySnapshot()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        foreach (var item in Manifest.Patches)
        {
            Files.Relative(item.Path);
            if (!expected.Add(item.Path)) throw new WorkbenchException("Duplicate patch path.");
            await Preserved("upstream/" + item.Path, item);
        }
        foreach (var item in Manifest.Additions)
        {
            string path = "additions/" + item.Path;
            Files.Relative(path);
            if (!expected.Add(path)) throw new WorkbenchException("Duplicate addition path.");
            await Preserved("upstream/" + path, item);
        }
        var actual = Directory.EnumerateFiles(Path.Combine(Root, "upstream"), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Path.Combine(Root, "upstream"), p).Replace('\\', '/'))
            .Where(p => p != "README.md");
        Files.SameSet(actual, expected, "Source payload inventory differs");
        Console.WriteLine($"Verified local source payload, including {Manifest.Additions.Count} additions.");
    }

    public void VerifyMaterialized()
    {
        foreach (var item in Manifest.TrackedOutputs.Concat(Manifest.Additions))
            Files.VerifyFile(Files.Under(Source, item.Path), item);
        Console.WriteLine($"Verified {Manifest.TrackedOutputs.Count} patched files and {Manifest.Additions.Count} additions.");
    }

    public async Task VerifyPreservation()
    {
        await VerifySnapshot();
        string curatedPath = Path.Combine(Root, "provenance", "curated-files.json");
        var snapshotChange = Files.ReadJson<JsonObject>(Path.Combine(Root, "provenance", "snapshot-change.json"));
        var curatedChange = Files.RequireObject(snapshotChange["curated"], "snapshot change curated files");
        Files.VerifyFile(curatedPath, new FileRecord
        {
            Sha256 = Files.RequireString(curatedChange["currentManifestSha256"], "current curated manifest hash")
        });
        var curated = Files.ReadJson<List<FileRecord>>(curatedPath);
        foreach (var item in curated) await Preserved(item.Path, item);
        var assets = Files.ReadJson<JsonArray>(Path.Combine(Root, "release-assets", "assets.json"));
        var expected = new Dictionary<string, string>();
        for (int i = 0; i < assets.Count; i++)
        {
            var asset = Files.RequireObject(assets[i], $"historical asset[{i}]");
            string name = Files.RequireString(asset["file"], $"historical asset[{i}].file");
            string hash = Files.RequireString(asset["sha256"], $"historical asset[{i}].sha256");
            if (!expected.TryAdd(name, hash))
                throw new WorkbenchException($"Duplicate historical asset: {name}");
        }
        var actual = new Dictionary<string, string>();
        foreach (string line in File.ReadLines(Path.Combine(Root, "release-assets", "SHA256SUMS")))
        {
            var match = Regex.Match(line, "^([0-9a-f]{64})  (.+)$");
            if (!match.Success || !actual.TryAdd(match.Groups[2].Value, match.Groups[1].Value))
                throw new WorkbenchException($"Invalid historical checksum record: {line}");
        }
        if (actual.Count != expected.Count || actual.Any(p => !expected.TryGetValue(p.Key, out string? hash) || hash != p.Value))
            throw new WorkbenchException("Historical asset manifest and checksums disagree.");
        Console.WriteLine($"Verified {curated.Count} curated files.");
        Console.WriteLine($"Historical external asset index: {assets.Count} records; asset bytes are separate.");
    }

    string Marker => Path.Combine(Source, ".git", "binding-workbench-local.json");
    public async Task RequireSource()
    {
        await VerifySnapshot();
        if (!File.Exists(Marker) || File.ReadAllText(Marker).Trim() != ManifestHash)
            throw new WorkbenchException("Run bootstrap in a new source directory for this snapshot.");
        string head = await Runner.Run("git", ["-C", Source, "rev-parse", "HEAD"]);
        if (head != SourceCommit) throw new WorkbenchException("Source HEAD differs from its exact pin.");
    }

    public async Task Bootstrap()
    {
        bool recognized = File.Exists(Marker);
        if (!recognized) Files.RequireEmpty(Source);
        await VerifySnapshot();
        if (recognized)
        {
            if (File.ReadAllText(Marker).Trim() != ManifestHash)
                throw new WorkbenchException("This source directory belongs to another snapshot; choose a new empty --source directory.");
            string head = await Runner.Run("git", ["-C", Source, "rev-parse", "HEAD"]);
            if (head != SourceCommit) throw new WorkbenchException("Source HEAD differs from its exact pin.");
            VerifyMaterialized();
        }
        else
        {
            Directory.CreateDirectory(Source);
            await Runner.Run("git", ["init", "--quiet", Source]);
            await Runner.Run("git", ["-C", Source, "remote", "add", "origin", SourceUrl]);
            await Runner.Run("git", ["-C", Source, "fetch", "--depth=1", "origin", SourceCommit]);
            await Runner.Run("git", ["-C", Source, "checkout", "--quiet", "--detach", "FETCH_HEAD"]);
            foreach (var patch in Manifest.Patches)
            {
                string path = Files.Under(Path.Combine(Root, "upstream"), patch.Path);
                await Runner.Run("git", ["-C", Source, "apply", "--check", path]);
                await Runner.Run("git", ["-C", Source, "apply", path]);
            }
            foreach (var item in Manifest.Additions)
            {
                string target = Files.Under(Source, item.Path);
                if (File.Exists(target)) throw new WorkbenchException($"Addition would overwrite upstream: {item.Path}");
                Directory.CreateDirectory(Files.ParentDirectory(target));
                await File.WriteAllBytesAsync(target, await Preserved("upstream/additions/" + item.Path, item));
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(target, (UnixFileMode)(Convert.ToInt32(item.Mode, 8) & 0x1FF));
            }
            VerifyMaterialized();
        }
        await File.WriteAllTextAsync(Marker, ManifestHash + "\n");
        Console.WriteLine("Pinned source materialized from local patches and additions. No workloads or submodules installed.");
    }

    public Task<string> Managed(string verb, string project, params string[] extra) =>
        Runner.Run(Options.Dotnet, new[] { verb, project, "-c", "Debug", "-nr:false", "-v:minimal" }.Concat(extra));
    public string ToolProject(string name) => Path.Combine(Source, "external", "Java.Interop", "tools", name, name + ".csproj");

    public async Task Descriptor()
    {
        string wrapper = Path.Combine(Source, "build-tools", "gradle", OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew");
        var arguments = new[] { wrapper, "--no-daemon", "--console", "plain", "-p",
            Path.Combine(Source, "external", "Java.Interop", "tools", "jvm-api-descriptor"), "test", "installDist" };
        await Runner.Run(OperatingSystem.IsWindows() ? wrapper : "bash",
            OperatingSystem.IsWindows() ? arguments.Skip(1) : arguments, Source);
    }

    public async Task Build()
    {
        foreach (string tool in Tools) await Managed("build", ToolProject(tool));
        await Descriptor();
    }

    public async Task Test()
    {
        await Managed("test", Path.Combine(Source, "external/Java.Interop/tests/generator-Tests/generator-Tests.csproj"));
        await Managed("test", Path.Combine(Source, "tests/Microsoft.Android.BindingGenerator-Tests/Microsoft.Android.BindingGenerator-Tests.csproj"));
        await Runner.Run(Options.Dotnet, ["run", "--project", Path.Combine(Root, "eng", "Workbench.Tests"), "--no-launch-profile"]);
    }

    public void Inspect()
    {
        using var archive = ZipFile.OpenRead(PackagePath);
        var names = archive.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (string name in new[] { $"build/{Package}.targets", $"build/{Package}.Bindings.targets",
            $"tools/tasks/{Package}.dll", "tools/generator/generator.dll", "tools/parser/class-parse.dll",
            "tools/descriptor/jvm-api-descriptor.jar", "THIRD-PARTY-NOTICES.TXT" })
            if (!names.Contains(name)) throw new WorkbenchException($"Missing packaged file: {name}");
        if (names.Any(n => n.StartsWith("lib/") || n.StartsWith("ref/") || n.StartsWith("buildTransitive/")
            || n.StartsWith("tools/tasks/Microsoft.Build.") || (n.EndsWith(".jar") && !n.StartsWith("tools/"))))
            throw new WorkbenchException("Compiler package contains non-private or host MSBuild assets.");
        File.WriteAllLines(Path.Combine(Root, ".artifacts", "package-contents.txt"), names.Order());
        Console.WriteLine($"Inspected {names.Count} package entries; SHA256 {Files.Hash(PackagePath)}");
    }

    public async Task Pack()
    {
        VerifyMaterialized();
        foreach (var (arguments, expected) in new[] {
            (new[] { "diff", "--no-ext-diff", "--name-only", "-z" }, Manifest.TrackedOutputs.Select(f => f.Path)),
            (new[] { "ls-files", "--others", "--exclude-standard", "-z" }, Manifest.Additions.Select(f => f.Path)),
        })
        {
            string output = await Runner.Run("git", new[] { "-C", Source }.Concat(arguments));
            Files.SameSet(output.Split('\0', StringSplitOptions.RemoveEmptyEntries), expected,
                "Package provenance requires an unmodified source inventory");
        }
        foreach (string name in new[] { "generator", "class-parse" }) await Managed("build", ToolProject(name));
        await Descriptor();
        Directory.CreateDirectory(Feed);
        await Managed("pack", Path.Combine(Source, "src", Package, Package + ".csproj"), "-o", Feed, $"-p:PackageVersion={Version}");
        Inspect();
        var runnerFiles = Directory.EnumerateFiles(Path.Combine(Root, "eng", "Workbench"), "*", SearchOption.TopDirectoryOnly)
            .Where(p => p.EndsWith(".cs") || p.EndsWith(".csproj"))
            .ToDictionary(p => Path.GetRelativePath(Root, p).Replace('\\', '/'), Files.Hash);
        Files.WriteJson(Path.Combine(Feed, $"{Package}.{Version}.provenance.json"), new
        {
            package = new { file = Path.GetFileName(PackagePath), sha256 = Files.Hash(PackagePath) },
            sdk = await Runner.Run(Options.Dotnet, ["--version"]),
            sourceManifestSha256 = Files.Hash(ManifestPath),
            sourceManifest = JsonNode.Parse(File.ReadAllText(ManifestPath)),
            snapshotChangeSha256 = Files.Hash(Path.Combine(Root, "provenance", "snapshot-change.json")),
            workbenchFiles = runnerFiles,
            sdkSelectionSha256 = Files.Hash(Path.Combine(Root, "global.json")),
            packageVersionOverride = Version,
            sourceIntegrationPatches = Array.Empty<string>(),
        });
    }

    public static void VerifyRestoredPackage(string package, string cache)
    {
        string restored = Path.Combine(cache, Package.ToLowerInvariant(), Version, Path.GetFileName(package).ToLowerInvariant());
        if (!File.Exists(restored) || Files.Hash(restored) != Files.Hash(package))
            throw new WorkbenchException("Restored compiler bytes do not match the freshly packed nupkg.");
    }

    public async Task Generate()
    {
        Inspect();
        string work = Path.Combine(Root, ".artifacts", "hello-dotnet");
        Files.CopyTree(Path.Combine(Root, "samples", "hello"), work);
        string classes = Path.Combine(work, "classes");
        Directory.CreateDirectory(classes);
        string Java(string name) => Options.Jdk is null ? name : Path.Combine(Options.Jdk, "bin", name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        await Runner.Run(Java("javac"), ["--release", "11", "-parameters", "-d", classes, Path.Combine(work, "Native", "Greeter.java")]);
        await Runner.Run(Java("jar"), ["cf", Path.Combine(work, "Binding", "hello.jar"), "-C", classes, "."]);
        string cache = Path.Combine(Root, ".artifacts", "sample-packages", Files.Hash(PackagePath));
        var properties = new List<string>();
        if (Options.Jdk is not null) properties.Add("-p:JavaSdkDirectory=" + Path.GetFullPath(Options.Jdk));
        if (Options.AndroidSdk is not null) properties.Add("-p:AndroidSdkDirectory=" + Path.GetFullPath(Options.AndroidSdk));
        await Runner.Run(Options.Dotnet, new[] { "build", Path.Combine(work, "Consumer", "Hello.Consumer.csproj"),
            "-c", "Debug", "-nr:false", "-v:minimal", "--source", Feed, "--source", "https://api.nuget.org/v3/index.json" }.Concat(properties),
            work, new Dictionary<string, string> { ["NUGET_PACKAGES"] = cache });
        VerifyRestoredPackage(PackagePath, cache);
        if (!Directory.EnumerateFiles(Path.Combine(work, "Binding", "obj"), "*Greeter*.cs", SearchOption.AllDirectories)
            .Any(p => File.ReadAllText(p).Contains("Greet (")))
            throw new WorkbenchException("The expected generated Greeter binding is missing.");
        Console.WriteLine("Generated the Java binding and compiled its separate C# consumer; no Android device run.");
    }

    public async Task Tool()
    {
        if (Options.ToolArguments.Count == 0 || !Tools.Contains(Options.ToolArguments[0]))
            throw new WorkbenchException("tool requires generator, class-parse, grouped-bindings or binding-compatibility.");
        string target = await Runner.Run(Options.Dotnet, ["msbuild", ToolProject(Options.ToolArguments[0]),
            "-nologo", "-p:Configuration=Debug", "-getProperty:TargetPath"]);
        if (!File.Exists(target)) throw new WorkbenchException("Run build before invoking this tool.");
        var arguments = Options.ToolArguments.Skip(1);
        if (arguments.FirstOrDefault() == "--") arguments = arguments.Skip(1);
        await Runner.Run(Options.Dotnet, new[] { "--roll-forward", "Major", target }.Concat(arguments));
    }
}

public static class Program
{
    public static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "upstream", "manifest.json"))) return directory.FullName;
        throw new WorkbenchException("Cannot locate the workbench root.");
    }

    public static async Task<int> Main(string[] arguments)
    {
        try
        {
            var options = Options.Parse(arguments);
            if (options.Command == "help")
            {
                Console.WriteLine("dotnet run --project eng/Workbench -- [--source DIR] COMMAND\n"
                    + "Commands: doctor, verify-snapshot, verify-preservation, bootstrap, build, test, pack, inspect,\n"
                    + "          generate, verify-package --work-dir NEW_DIR, tool NAME [-- ARGS]");
                return 0;
            }
            var workbench = new Workbench(FindRoot(), options);
            switch (options.Command)
            {
                case "doctor":
                    Console.WriteLine($".NET runtime: {Environment.Version}");
                    await workbench.Runner.Run("git", ["--version"]);
                    await workbench.Runner.Run(options.Dotnet, ["--list-sdks"]);
                    await workbench.Runner.Run(options.Dotnet, ["--version"]);
                    await workbench.Runner.Run("java", ["-version"]);
                    Console.WriteLine("Requires JDK21, selected SDK11 and a .NET10 runtime for host tests; installs nothing.");
                    break;
                case "verify-snapshot": await workbench.VerifySnapshot(); break;
                case "verify-preservation": await workbench.VerifyPreservation(); break;
                case "bootstrap": await workbench.Bootstrap(); break;
                case "inspect": workbench.Inspect(); break;
                case "generate": await workbench.Generate(); break;
                default:
                    await workbench.RequireSource();
                    switch (options.Command)
                    {
                        case "build": await workbench.Build(); break;
                        case "test": await workbench.Test(); break;
                        case "pack": await workbench.Pack(); break;
                        case "tool": await workbench.Tool(); break;
                        case "verify-package": await new PackageFixture(workbench).Run(); break;
                        default: throw new WorkbenchException($"Unknown command: {options.Command}");
                    }
                    break;
            }
            return 0;
        }
        catch (CommandException error) { Console.Error.WriteLine("error: " + error.Message); return error.ExitCode; }
        catch (Exception error) when (error is WorkbenchException or IOException or UnauthorizedAccessException
            or JsonException or System.ComponentModel.Win32Exception
            or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine("error: " + error.Message);
            return 1;
        }
    }
}
