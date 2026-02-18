using IronPaw.AgentCore.Abstractions;

namespace IronPaw.AgentCore.Tests;

public sealed class AbstractionsGuardTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SessionKey_RejectsWhitespace(string value)
    {
        Assert.Throws<ArgumentException>(() => new SessionKey(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RunId_RejectsWhitespace(string value)
    {
        Assert.Throws<ArgumentException>(() => new RunId(value));
    }
}
