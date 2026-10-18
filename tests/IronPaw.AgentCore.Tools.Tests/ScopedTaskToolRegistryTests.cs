using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class ScopedTaskToolRegistryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "IronPawScopedTaskTests", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task ReadWriteScopesAndListingCannotExposeUndeclaredFiles()
    {
        var registry = Registry(new() { ReadPaths = ["allowed.txt", "visible"], WritePaths = ["output"] });
        await File.WriteAllTextAsync(Path.Combine(root, "allowed.txt"), "allowed");
        await File.WriteAllTextAsync(Path.Combine(root, "secret.txt"), "secret");
        await File.WriteAllTextAsync(Path.Combine(root, ".git"), "metadata");
        var listing = await InvokeAsync(registry, "list_files", new { path = "." });
        var files = JsonDocument.Parse(listing.Content).RootElement.GetProperty("files").EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["allowed.txt"], files);
        Assert.Equal("allowed", (await InvokeAsync(registry, "read_file", new { path = "allowed.txt" })).Content);
        Assert.True((await InvokeAsync(registry, "read_file", new { path = "secret.txt" })).IsError);
        Assert.True((await InvokeAsync(registry, "write_file", new { path = "allowed.txt", content = "change" })).IsError);
        await InvokeAsync(registry, "write_file", new { path = "output/new.txt", content = "output" });
        Assert.Equal("output", await File.ReadAllTextAsync(Path.Combine(root, "output/new.txt")));
        await InvokeAsync(registry, "write_file", new { path = "output\\portable.txt", content = "portable" });
        Assert.Equal("portable", await File.ReadAllTextAsync(Path.Combine(root, "output", "portable.txt")));
        Assert.True((await InvokeAsync(registry, "read_file", new { path = "output/new.txt" })).IsError);
        Assert.Equal(["list_files", "read_file", "write_file"], registry.ResolveTools(new(ToolProfile.Full, SenderIsOwner: true)).Select(item => item.Descriptor.Name).Order());
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("dir/../../escape.txt")]
    [InlineData("/outside.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData("output:stream")]
    [InlineData(".git/config")]
    [InlineData("nested/.GIT/config")]
    [InlineData("dir//file.txt")]
    [InlineData("NUL.txt")]
    [InlineData("dir./file.txt")]
    public async Task WildcardScopeStillRejectsUnsafePaths(string path)
    {
        var registry = Registry(new() { ReadPaths = ["."], WritePaths = ["."] });
        var denial = await InvokeAsync(registry, "write_file", new { path, content = "must not write" });
        Assert.True(denial.IsError);
        Assert.False(denial.Details?.ContainsKey("error") == true);
    }

    [Fact]
    public async Task EmptyScopeDeniesAllFileAccessAndPolicyStillRestrictsTools()
    {
        var registry = Registry(new());
        Assert.True((await InvokeAsync(registry, "read_file", new { path = "file.txt" })).IsError);
        Assert.True((await InvokeAsync(registry, "list_files", new { path = "." })).IsError);
        Assert.Empty(registry.ResolveTools(new(ToolProfile.Minimal, SenderIsOwner: true)));
        var readOnly = registry.ResolveTools(new(ToolProfile.Coding, SenderIsOwner: false));
        Assert.DoesNotContain(readOnly, item => item.Descriptor.Name == "write_file");
    }

    [Fact]
    public void UnsupportedCapabilitiesAndUnsafeScopeDeclarationsAreRejectedBeforeToolsExist()
    {
        Assert.Throws<NotSupportedException>(() => Registry(new() { NetworkHosts = ["example.com"] }));
        Assert.Throws<NotSupportedException>(() => Registry(new() { AllowProcessExecution = true }));
        Assert.Throws<NotSupportedException>(() => Registry(new() { AllowCredentialAccess = true }));
        Assert.Throws<NotSupportedException>(() => Registry(new() { AllowExternalMutations = true }));
        Assert.Throws<InvalidOperationException>(() => Registry(new() { ReadPaths = ["../outside"] }));
    }

    [Fact]
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
            Assert.True((await InvokeAsync(registry, "read_file", new { path = "link/secret.txt" })).IsError);
            Assert.True((await InvokeAsync(registry, "write_file", new { path = "link/new.txt", content = "outside" })).IsError);
            Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
            var aliasRoot = root + "-alias";
            await GitTaskWorkspaceManagerTests.CreateLinkAsync(aliasRoot, root);
            try { Assert.Throws<InvalidOperationException>(() => ScopedTaskToolRegistry.Create(Workspace(aliasRoot), new() { ReadPaths = ["."] })); }
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

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
