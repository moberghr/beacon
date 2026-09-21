using Beacon.Core.Helpers;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class ConnectionFailureDescriberTests
{
    [Test]
    public void Describe_FlattensInnerExceptionChain()
    {
        var cause = new InvalidOperationException("No such host is known.");
        var wrapper = new Exception("A network-related error occurred.", cause);

        var description = ConnectionFailureDescriber.Describe(wrapper);

        description.Should().Be(
            "Exception: A network-related error occurred. -> InvalidOperationException: No such host is known.");
    }

    [Test]
    public void Describe_SingleException_ReturnsTypeAndMessage()
    {
        var description = ConnectionFailureDescriber.Describe(new TimeoutException("Timeout expired."));

        description.Should().Be("TimeoutException: Timeout expired.");
    }

    [Test]
    public void Describe_DropsFramesThatRepeatTheOuterMessage()
    {
        var inner = new InvalidOperationException("Login failed.");
        var outer = new InvalidOperationException("Login failed.", inner);

        var description = ConnectionFailureDescriber.Describe(outer);

        description.Should().Be("InvalidOperationException: Login failed.");
    }

    [Test]
    public void Describe_StopsAfterFiveFrames()
    {
        Exception current = new Exception("depth-0");
        for (var i = 1; i <= 8; i++)
        {
            current = new Exception($"depth-{i}", current);
        }

        var description = ConnectionFailureDescriber.Describe(current);

        description.Split(" -> ").Should().HaveCount(5);
        description.Should().NotContain("depth-0");
    }
}
