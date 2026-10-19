using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools.Tests;

[TestClass]
public sealed class ScopedTaskToolRegistryTests
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IronPawScopedTaskTests", Guid.NewGuid().ToString("n"));

    [TestMethod]
    public async Task ReadWriteScopesAndListingCannotExposeUndeclaredFiles()
    {
        var registry = Registry(new() { ReadPaths = ["allowed.txt", "visible"], WritePaths = ["output"] });
        await File.WriteAllTextAsync(Path.Combine(root, "allowed.txt"), "allowed");
        await File.WriteAllTextAsync(Path.Combine(root, "secret.txt"), "secret");
        await File.WriteAllTextAsync(Path.Combine(root, ".git"), "metadata");
        var listing = await InvokeAsync(registry, "list_files", new { path = "." });
        var files = JsonDocument.Parse(listing.Content).RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.AreSequenceEqual(["allowed.txt"], files);
        Assert.AreEqual("allowed", (await InvokeAsync(registry, "read_file", new { path = "allowed.txt" })).Content);
        Assert.IsTrue((await InvokeAsync(registry, "read_file", new { path = "secret.txt" })).IsError);
        Assert.IsTrue((await InvokeAsync(registry, "write_file", new { path = "allowed.txt", content = "change" })).IsError);
        await InvokeAsync(registry, "write_file", new { path = "output/new.txt", content = "output" });
        Assert.AreEqual("output", await File.ReadAllTextAsync(Path.Combine(root, "output/new.txt")));
        await InvokeAsync(registry, "write_file", new { path = "output\\portable.txt", content = "portable" });
        Assert.AreEqual("portable", await File.ReadAllTextAsync(Path.Combine(root, "output", "portable.txt")));
        Assert.IsTrue((await InvokeAsync(registry, "read_file", new { path = "output/new.txt" })).IsError);
        Assert.AreSequenceEqual(["list_files", "read_file", "write_file"], registry.ResolveTools(new(ToolProfile.Full, SenderIsOwner: true)).Select(item => item.Descriptor.Name).Order());
    }

    [TestMethod]
    [DataRow("../escape.txt")]
    [DataRow("dir/../../escape.txt")]
    [DataRow("/outside.txt")]
    [DataRow("C:\\outside.txt")]
    [DataRow("output:stream")]
    [DataRow(".git/config")]
    [DataRow("nested/.GIT/config")]
    [DataRow("dir//file.txt")]
    [DataRow("NUL.txt")]
    [DataRow("dir./file.txt")]
    public async Task WildcardScopeStillRejectsUnsafePaths(string path)
    {
        var registry = Registry(new() { ReadPaths = ["."], WritePaths = ["."] });
        var denial = await InvokeAsync(registry, "write_file", new { path, content = "must not write" });
        Assert.IsTrue(denial.IsError);
        Assert.IsFalse(denial.Details?.ContainsKey("error") == true);
    }

    [TestMethod]
    public async Task EmptyScopeDeniesAllFileAccessAndPolicyStillRestrictsTools()
    {
        var registry = Registry(new());
        Assert.IsTrue((await InvokeAsync(registry, "read_file", new { path = "file.txt" })).IsError);
        Assert.IsTrue((await InvokeAsync(registry, "list_files", new { path = "." })).IsError);
        Assert.IsEmpty(registry.ResolveTools(new(ToolProfile.Minimal, SenderIsOwner: true)));
        var readOnly = registry.ResolveTools(new(ToolProfile.Coding, SenderIsOwner: false));
        Assert.DoesNotContain(item => item.Descriptor.Name == "write_file", readOnly);
    }

    [TestMethod]
    public void UnsupportedCapabilitiesAndUnsafeScopeDeclarationsAreRejectedBeforeToolsExist()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => Registry(new() { NetworkHosts = ["example.com"] }));
        Assert.ThrowsExactly<NotSupportedException>(() => Registry(new() { AllowProcessExecution = true }));
        Assert.ThrowsExactly<NotSupportedException>(() => Registry(new() { AllowCredentialAccess = true }));
        Assert.ThrowsExactly<NotSupportedException>(() => Registry(new() { AllowExternalMutations = true }));
        Assert.ThrowsExactly<InvalidOperationException>(() => Registry(new() { ReadPaths = ["../outside"] }));
    }

    [TestMethod]
    public async Task JunctionAddedAfterRegistryCreationIsRejectedAndNotListed()
    {
        var registry = Registry(new() { ReadPaths = ["."], WritePaths = ["."] });
        var outside = Path.Combine(Path.GetTempPath(), "IronPawScopedOutside", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "outside");
        var link = Path.Combine(root, "link");
        await GitTaskWorkspaceManagerTests.CreateLinkAsync(link, outside);
        try
        {
            Assert.IsTrue((await InvokeAsync(registry, "read_file", new { path = "link/secret.txt" })).IsError);
            Assert.IsTrue((await InvokeAsync(registry, "write_file", new { path = "link/new.txt", content = "outside" })).IsError);
            Assert.IsFalse(File.Exists(Path.Combine(outside, "new.txt")));
            var aliasRoot = root + "-alias";
            await GitTaskWorkspaceManagerTests.CreateLinkAsync(aliasRoot, root);
            try { Assert.ThrowsExactly<InvalidOperationException>(() => ScopedTaskToolRegistry.Create(Workspace(aliasRoot), new() { ReadPaths = ["."] })); }
            finally { Directory.Delete(aliasRoot); }
        }
        finally { Directory.Delete(link); Directory.Delete(outside, recursive: true); }
    }

    private IToolRegistry Registry(TaskResourceScope scope)
    {
        Directory.CreateDirectory(root);
        return ScopedTaskToolRegistry.Create(Workspace(root), scope);
    }

    private static TaskWorkspace Workspace(string path) => new("graph", "fingerprint", "task", "attempt", "commit", path, Path.GetDirectoryName(path)!);
    private static Task<ToolResult> InvokeAsync(IToolRegistry registry, string name, object arguments)
    {
        var tool = registry.ResolveTools(new(ToolProfile.Full, SenderIsOwner: true)).Single(item => item.Descriptor.Name == name);
        return tool.ExecuteAsync(new("call", name, JsonSerializer.SerializeToElement(arguments)),
            new(RunId.New(), SessionKey.New(), "test", "test", CallerIdentity.LocalOwner()), CancellationToken.None);
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
