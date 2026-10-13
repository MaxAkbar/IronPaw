using System.Diagnostics;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tools.Tests;

public sealed class WorkspaceBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFile_RejectsSiblingDirectorySharingPrefix(bool trailingSeparator)
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var outside = Path.Combine(root, "workspace-other", "outside.txt");
            var tool = new WriteFileTool(trailingSeparator ? workspace + Path.DirectorySeparatorChar : workspace);
            await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(tool, outside));
            Assert.False(File.Exists(outside));
            await WriteAsync(tool, "inside.txt");
            Assert.Equal("test", await File.ReadAllTextAsync(Path.Combine(workspace, "inside.txt")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ReadFile_RejectsSiblingDirectorySharingPrefix()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            var outside = Path.Combine(root, "workspace-other");
            Directory.CreateDirectory(outside);
            var path = Path.Combine(outside, "outside.txt");
            await File.WriteAllTextAsync(path, "outside data");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ReadFileTool(workspace).ExecuteAsync(
                new ToolCall("read", "read_file", JsonSerializer.SerializeToElement(new { path })), Context(), CancellationToken.None));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task WriteFile_RejectsJunctionOrSymbolicLink()
    {
        var root = CreateTempDirectory();
        var link = Path.Combine(root, "workspace", "link");
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(outside);
            if (OperatingSystem.IsWindows())
            {
                using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
                })!;
                await process.WaitForExitAsync();
                Assert.Equal(0, process.ExitCode);
            }
            else
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => WriteAsync(new WriteFileTool(workspace), "link/outside.txt"));
            Assert.False(File.Exists(Path.Combine(outside, "outside.txt")));
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task<ToolResult> WriteAsync(WriteFileTool tool, string path) => tool.ExecuteAsync(
        new ToolCall("write", "write_file", JsonSerializer.SerializeToElement(new { path, content = "test" })),
        Context(), CancellationToken.None);

    private static ToolExecutionContext Context() => new(RunId.New(), SessionKey.New(), "local", "test", CallerIdentity.LocalOwner());

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "IronPawTests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}
