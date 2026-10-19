using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using IronPaw.AgentCore.Abstractions;
using IronPaw.AgentCore.Tools;
using IronPaw.Workflow.CrashHarness;

namespace IronPaw.AgentCore.Mcp.Tests;

[TestClass]
public sealed class McpHostTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ironpaw-mcp-host", Guid.NewGuid().ToString("N"));
    private static readonly JsonElement Schema = JsonSerializer.Deserialize<JsonElement>("""{"type":"object","properties":{"value":{"type":"integer"}},"required":["value"],"additionalProperties":false}""");
    private static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private McpHostTransport ProcessTransport(string? mode = null)
    {
        Directory.CreateDirectory(_root);
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var fixture = typeof(McpProcessFixture).Assembly.Location;
        return new("stdio", dotnet, Hash(dotnet), [fixture, "--mcp-fixture", _root, mode ?? "normal"], _root,
            ReviewedFiles: new Dictionary<string, string> { [fixture] = Hash(fixture) });
    }
    private static string Document(McpHostTransport transport, string revision = "reviewed-v1", int timeoutSeconds = 3) =>
        JsonSerializer.Serialize(new McpHostConfiguration(1, [new("fixture", revision, transport, [new("record", "Records a local effect", Schema)], TimeoutSeconds: timeoutSeconds)]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static Task<ToolResult> Invoke(McpHost host, CancellationToken ct = default) => host.Tools[0].ExecuteAsync(
        new("call", host.ToolNames[0], JsonSerializer.SerializeToElement(new { value = 2 })),
        new(RunId.New(), new("fixture"), "local", "fixture", CallerIdentity.LocalOwner(), ToolPolicy: host.Grant(new(ToolProfile.Coding))), ct);

    [TestMethod]
    public async Task RealSubprocessIsLazyExplicitlyGrantedAndReapedOnDisposal()
    {
        int pid;
        using (var host = McpHost.Parse(Document(ProcessTransport())))
        {
            Assert.IsFalse(File.Exists(Path.Combine(_root, "process-id")));
            var registry = new InMemoryToolRegistry(host.Tools);
            Assert.IsEmpty(registry.ResolveTools(new(ToolProfile.Full, SenderIsOwner: true)));
            Assert.ContainsSingle(registry.ResolveTools(host.Grant(new(ToolProfile.Coding, SenderIsOwner: true))));
            Assert.AreSequenceEqual(host.ToolNames, host.RequireApprovals([]));
            var result = await Invoke(host);
            Assert.IsFalse(result.IsError);
            Assert.Contains("inherited=False", result.Content);
            Assert.AreEqual(ContentTrust.Untrusted, result.Trust);
            pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "process-id")));
        }
        Assert.IsTrue(Exited(pid));
        Assert.AreEqual(1, File.ReadAllLines(Path.Combine(_root, "effects")).Length);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task OnlyExplicitEnvironmentReferencesReachTheChildAndReviewsNeverContainValues()
    {
        const string privateName = "IRONPAW_TEST_PRIVATE_SECRET";
        const string sourceName = "IRONPAW_TEST_CONNECTOR_SOURCE";
        var previousPrivate = Environment.GetEnvironmentVariable(privateName);
        var previousSource = Environment.GetEnvironmentVariable(sourceName);
        try
        {
            Environment.SetEnvironmentVariable(privateName, "ambient-secret-value");
            Environment.SetEnvironmentVariable(sourceName, "explicit-secret-value");
            using var host = McpHost.Parse(Document(ProcessTransport() with
            {
                EnvironmentVariables = new Dictionary<string, string> { ["FIXTURE_TOKEN"] = sourceName },
                CredentialIdentity = "fixture-only"
            }));
            Assert.DoesNotContain("ambient-secret-value", host.Review);
            Assert.DoesNotContain("explicit-secret-value", host.Review);
            var result = await Invoke(host);
            Assert.Contains("inherited=False; explicit=True", result.Content);
        }
        finally
        {
            Environment.SetEnvironmentVariable(privateName, previousPrivate);
            Environment.SetEnvironmentVariable(sourceName, previousSource);
        }
    }

    [TestMethod]
    public async Task ReviewedLaunchInputsRemainReadOnlyUntilTheConnectionCloses()
    {
        var transport = ProcessTransport();
        var input = Path.Combine(_root, "reviewed-input");
        await File.WriteAllTextAsync(input, "reviewed");
        using (var host = McpHost.Parse(Document(transport with { ReviewedFiles = new Dictionary<string, string> { [input] = Hash(input) } })))
        {
            Assert.IsFalse((await Invoke(host)).IsError);
            Assert.ThrowsExactly<IOException>(() => File.WriteAllText(input, "changed"));
        }
        await File.WriteAllTextAsync(input, "changed");
        Assert.AreEqual("changed", await File.ReadAllTextAsync(input));
    }

    [TestMethod]
    [DataRow("oversized")]
    [DataRow("hang")]
    [DataRow("stderr")]
    public async Task UncertainProcessOutcomeClosesConnectionWithoutRetry(string mode)
    {
        using var host = McpHost.Parse(Document(ProcessTransport(mode) with { MaxFrameBytes = 4096, MaxSessionBytes = 8192 }, timeoutSeconds: mode == "stderr" ? 30 : 3));
        await Assert.ThrowsAsync<Exception>(() => Invoke(host).WaitAsync(TimeSpan.FromSeconds(5)));
        var pid = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "process-id")));
        Assert.IsTrue(Exited(pid));
        await Assert.ThrowsExactlyAsync<IOException>(() => Invoke(host));
        Assert.AreEqual(1, File.ReadAllLines(Path.Combine(_root, "effects")).Length);
    }

    [TestMethod]
    public void EveryTransportAuthorityChangeCreatesNewToolIdentity()
    {
        var transport = ProcessTransport();
        using var original = McpHost.Parse(Document(transport));
        foreach (var changed in new[] { transport with { Arguments = ["different"] }, transport with { MaxFrameBytes = 4096 },
            transport with { CredentialIdentity = "rotation-2" }, transport with { MaxSessionFrames = 200 } })
        {
            using var other = McpHost.Parse(Document(changed));
            Assert.AreNotEqual(original.ToolNames[0], other.ToolNames[0]);
        }
        Assert.IsFalse(File.Exists(Path.Combine(_root, "process-id")));
    }

    [TestMethod]
    public void RejectsMalformedAmbiguousOrExpandedConfiguration()
    {
        foreach (var json in new[] { "{}", "null", "{\"schemaVersion\":1,\"SchemaVersion\":1,\"connectors\":[]}",
            "{\"schemaVersion\":1,\"connectors\":[],\"unknown\":true}", "{\"schemaVersion\":2,\"connectors\":[]}" })
            Assert.Throws<Exception>(() => McpHost.Parse(json));
        Assert.Throws<ArgumentException>(() => McpHost.Parse(Document(ProcessTransport() with { ExecutableSha256 = new string('0', 64) })));
        Assert.Throws<ArgumentException>(() => McpHost.Parse(Document(ProcessTransport() with { Command = "dotnet" })));
        Assert.Throws<ArgumentException>(() => McpHost.Parse(Document(new("http", Endpoint: "http://public.example/mcp"))));
        Assert.Throws<ArgumentException>(() => McpHost.Parse(Document(new("http", Endpoint: "https://user:secret@public.example/mcp"))));
        Assert.Throws<ArgumentException>(() => McpHost.Parse(Document(new("http", Endpoint: "https://public.example/mcp", BearerTokenEnvironmentVariable: "TOKEN"))));
    }

    [TestMethod]
    public void RechecksReviewedLaunchFilesBeforeAnyProcessStarts()
    {
        var transport = ProcessTransport();
        var file = Path.Combine(_root, "reviewed-input");
        File.WriteAllText(file, "original");
        using var host = McpHost.Parse(Document(transport with { ReviewedFiles = new Dictionary<string, string> { [file] = Hash(file) } }));
        File.WriteAllText(file, "modified");
        Assert.Throws<Exception>(() => Invoke(host).GetAwaiter().GetResult());
        Assert.IsFalse(File.Exists(Path.Combine(_root, "process-id")));
    }

    private static bool Exited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited || process.WaitForExit(3000); }
        catch (ArgumentException) { return true; }
    }

    [TestMethod]
    public async Task KillingTheOwningHostAlsoTerminatesItsConnectorJob()
    {
        var transport = ProcessTransport("linger");
        var configuration = Path.Combine(_root, "host.json");
        var ready = Path.Combine(_root, "host-ready");
        await File.WriteAllTextAsync(configuration, Document(transport));
        var start = new ProcessStartInfo(transport.Command!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { typeof(McpProcessFixture).Assembly.Location, "--mcp-parent", configuration, ready }) start.ArgumentList.Add(argument);
        using var parent = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!File.Exists(ready)) { if (parent.HasExited) Assert.Fail("The MCP parent fixture exited before its ready marker."); await Task.Delay(20, deadline.Token); }
            var childId = int.Parse(await File.ReadAllTextAsync(Path.Combine(_root, "process-id")));
            parent.Kill(); // Deliberately kill only the parent, not its tree.
            await parent.WaitForExitAsync(deadline.Token);
            Assert.IsTrue(Exited(childId), "The connector survived its owning host's termination.");
        }
        finally { if (!parent.HasExited) parent.Kill(entireProcessTree: true); }
    }

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
