using IronPaw.AgentCore.Abstractions;
using IronPaw.Cli;

namespace IronPaw.Cli.Tests;

[TestClass]
public sealed class ExtensionConfigurationTests
{
    [TestMethod]
    [DataRow("--inspect-mcp-config")]
    [DataRow("--inspect-browser-config")]
    public void InspectionIsStandaloneAndNeverBecomesAChatPrompt(string command)
    {
        Assert.IsTrue(CliOptions.Parse([command, "reviewed.json"]).TryValidate(out _));
        foreach (var extra in new[] { new[] { "--prompt", "hello" }, new[] { "--list-workflows" }, new[] { "--config", "other.json" }, new[] { "--mcp-config", "other.json" } })
            Assert.IsFalse(CliOptions.Parse([command, "reviewed.json", .. extra]).TryValidate(out _));
    }

    [TestMethod]
    [DataRow("--mcp-config")]
    [DataRow("--browser-config")]
    public void ExplicitExtensionFilesAreAllowedOnlyForOrdinaryExecutionOrItsSavedResume(string command)
    {
        Assert.IsTrue(CliOptions.Parse([command, "reviewed.json", "--prompt", "hello"]).TryValidate(out _));
        Assert.IsTrue(CliOptions.Parse([command, "reviewed.json", "--resume", "saved"]).TryValidate(out _));
        foreach (var extra in new[] { new[] { "--list-workflows" }, new[] { "--list-graphs" }, new[] { "--list-skills" }, new[] { "--validate-config", "settings.json" } })
            Assert.IsFalse(CliOptions.Parse([command, "reviewed.json", .. extra]).TryValidate(out _));
    }

    [TestMethod]
    public void InvalidExtensionConfigurationFailsBeforeApplicationStateIsOpened()
    {
        var root = Path.Combine(Path.GetTempPath(), "ironpaw-extension-bootstrap", Guid.NewGuid().ToString("N"));
        var file = root + ".json";
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{}");
        try
        {
            Assert.Throws<ArgumentException>(() => AppBootstrapper.Build(stateRoot: root, workspaceRoot: Path.GetTempPath(), mcpConfigFile: file));
            Assert.IsFalse(Directory.Exists(root));
            Assert.Throws<ArgumentException>(() => AppBootstrapper.Build(stateRoot: root, workspaceRoot: Path.GetTempPath(), browserConfigFile: file));
            Assert.IsFalse(Directory.Exists(root));
        }
        finally { File.Delete(file); }
    }
}
