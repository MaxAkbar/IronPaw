using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

[TestClass]
public sealed class AbstractionsGuardTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void SessionKey_RejectsWhitespace(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new SessionKey(value));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void RunId_RejectsWhitespace(string value)
    {
        Assert.ThrowsExactly<ArgumentException>(() => new RunId(value));
    }
}
