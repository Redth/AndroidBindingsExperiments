using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using BindingWorkbench;

if (args is ["--exit-code", var code]) return int.Parse(code);
string root = BindingWorkbench.Program.FindRoot();
string scratch = Path.Combine(root, ".artifacts", "dotnet-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
int passed = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
void Reject(Action action, string message)
{
    try { action(); }
    catch (WorkbenchException error)
    {
        Require(error.Message.Contains(message, StringComparison.OrdinalIgnoreCase), error.Message);
        return;
    }
    throw new Exception("Expected rejection: " + message);
}
void Test(string name, Action action)
{
    action();
    passed++;
    Console.WriteLine("PASS " + name);
}
string Write(string name, string content)
{
    string path = Path.Combine(scratch, name);
    File.WriteAllText(path, content);
    return path;
}

try
{
    Test("unsafe paths and malformed metadata are rejected", () =>
    {
        foreach (string path in new[] { "", "../outside", "/absolute", "a/.git/config", "C:/outside", "a\\b", "./file" })
            Reject(() => Files.Relative(path), "unsafe");
        string pathRoot = Path.GetPathRoot(scratch) ?? throw new Exception("Test directory has no filesystem root.");
        Reject(() => Files.ParentDirectory(pathRoot), "parent directory");
        Reject(() => Files.RequireObject(JsonNode.Parse("null"), "SDK properties"), "SDK properties");
        Reject(() => Files.RequireObject(JsonNode.Parse("[]"), "SDK properties"), "JSON object");
        Reject(() => Files.RequireString(null, "asset file"), "asset file");
        Reject(() => Files.RequireString(JsonValue.Create(3), "asset file"), "JSON string");
        Reject(() => Files.RequireString(JsonValue.Create(" "), "asset file"), "JSON string");
    });
    Test("changed source hashes fail", () =>
    {
        string path = Write("tamper.cs", "original");
        var record = new FileRecord { Sha256 = Files.Hash(path) };
        File.WriteAllText(path, "modified");
        Reject(() => Files.VerifyFile(path, record), "checksum");
    });
    Test("unexpected source inventory fails", () =>
        Reject(() => Files.SameSet(["kept.cs", "forgotten.cs"], ["kept.cs"], "inventory differs"), "inventory"));
    Test("nonempty source is never overwritten", () =>
    {
        string source = Path.Combine(scratch, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "user-file"), "preserve");
        Reject(() => Files.RequireEmpty(source), "refusing");
        Require(File.ReadAllText(Path.Combine(source, "user-file")) == "preserve", "User file changed.");
    });
    Test("exact canonical and declared CRLF bytes are accepted", () =>
    {
        string path = Write("line-endings.cs", "line\n");
        string canonical = Files.Hash(path);
        File.WriteAllText(path, "line\r\n");
        var record = new FileRecord { Sha256 = canonical, WorktreeSha256 = Files.Hash(path) };
        Files.VerifyFile(path, record);
        File.WriteAllText(path, "line\n");
        Files.VerifyFile(path, record);
        File.WriteAllText(path, "edited\n");
        Reject(() => Files.VerifyFile(path, record), "checksum");
    });
    if (!OperatingSystem.IsWindows()) Test("symlinked source is rejected", () =>
    {
        string original = Write("original.cs", "source");
        string link = Path.Combine(scratch, "link.cs");
        File.CreateSymbolicLink(link, original);
        Reject(() => Files.VerifyFile(link, new FileRecord { Sha256 = Files.Hash(original) }), "symlink");
        Reject(() => Files.Under(scratch, "link.cs"), "symlink");
        string dangling = Path.Combine(scratch, "dangling.cs");
        File.CreateSymbolicLink(dangling, Path.Combine(scratch, "missing.cs"));
        Reject(() => Files.Under(scratch, "dangling.cs"), "symlink");
    });
    Test("source edits cannot claim package provenance", () =>
        Reject(() => Files.SameSet(["original.cs", "unrelated.cs"], ["original.cs"], "Package provenance inventory"), "provenance"));
    Test("restored package bytes must match the feed", () =>
    {
        string package = Write($"{Workbench.Package}.{Workbench.Version}.nupkg", "fresh");
        string cache = Path.Combine(scratch, "cache");
        string restored = Path.Combine(cache, Workbench.Package.ToLowerInvariant(), Workbench.Version, Path.GetFileName(package).ToLowerInvariant());
        Directory.CreateDirectory(Files.ParentDirectory(restored));
        File.WriteAllText(restored, "stale");
        Reject(() => Workbench.VerifyRestoredPackage(package, cache), "restored");
        File.Copy(package, restored, true);
        Workbench.VerifyRestoredPackage(package, cache);
    });
    ArchiveIndex MakeArchive(string name, params string[] paths)
    {
        string archive = Path.Combine(scratch, name);
        var index = new ArchiveIndex();
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (string path in paths)
            {
                byte[] content = "original archived source"u8.ToArray();
                using var stream = zip.CreateEntry(path).Open();
                stream.Write(content);
                index.Files.Add(new FileRecord { Path = path, Bytes = content.Length, Sha256 = Files.Hash(content) });
            }
        }
        index.Sha256 = Files.Hash(archive);
        index.Bytes = new FileInfo(archive).Length;
        return index;
    }
    Test("archive hash and every member are verified", () =>
    {
        var index = MakeArchive("valid.zip", "original/script.py");
        string archive = Path.Combine(scratch, "valid.zip");
        Require(PythonArchive.ReadVerified(archive, index).Count == 1, "Missing archive source.");
        index.Sha256 = new string('0', 64);
        Reject(() => PythonArchive.ReadVerified(archive, index), "checksum");
    });
    Test("archive directory metadata is accepted without becoming payload", () =>
    {
        var index = MakeArchive("directories.zip", "original/script.py");
        string archive = Path.Combine(scratch, "directories.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) zip.CreateEntry("original/");
        index.Sha256 = Files.Hash(archive);
        index.Bytes = new FileInfo(archive).Length;
        Require(PythonArchive.ReadVerified(archive, index).Count == 1, "Directory metadata became a source file.");
    });
    Test("archived path traversal is rejected", () =>
    {
        var index = MakeArchive("traversal.zip", "../outside.py");
        Reject(() => PythonArchive.ReadVerified(Path.Combine(scratch, "traversal.zip"), index), "unsafe");
    });
    Test("unexpected archive members are rejected", () =>
    {
        var index = MakeArchive("extra.zip", "original.py", "unlisted.py");
        index.Files.RemoveAt(1);
        Reject(() => PythonArchive.ReadVerified(Path.Combine(scratch, "extra.zip"), index), "inventory");
    });
    var runner = new ProcessRunner(root, false);
    runner.Prepare();
    try
    {
        await runner.Run(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [Assembly.GetExecutingAssembly().Location, "--exit-code", "23"]);
        throw new Exception("Child failure was swallowed.");
    }
    catch (CommandException error)
    {
        Require(error.ExitCode == 23, "Child exit code changed.");
        passed++;
        Console.WriteLine("PASS child exit code is preserved");
    }
    if (!OperatingSystem.IsWindows())
    {
        var blocker = new ProcessRunner(root, true);
        blocker.Prepare();
        try
        {
            await blocker.Run("python3", ["--version"]);
            throw new Exception("The blocking shim allowed Python.");
        }
        catch (CommandException error)
        {
            Require(error.ExitCode == 127, "The blocking shim did not fail fast.");
            Reject(blocker.VerifyNoPython, "attempted");
            passed++;
            Console.WriteLine("PASS negative control reaches the blocking shim, never a Python interpreter");
        }
    }
    Console.WriteLine($"Passed {passed} .NET workbench tests.");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error);
    return 1;
}
finally
{
    Directory.Delete(scratch, recursive: true);
}
