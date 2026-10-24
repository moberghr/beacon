using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Beacon.MCP.Tools;

namespace Beacon.Tests.Unit;

[TestFixture]
public class ToolHelperByteBudgetTests
{
    [Test]
    public void TextWithinTheBudget_IsReturnedAsIs()
    {
        ToolHelper.WithinByteBudget("short text", 1000).Should().Be("short text");
    }

    [Test]
    public void TextOverTheBudget_IsCutWithinTheBudget_AndSaysSo()
    {
        var text = new string('a', 5000);

        var result = ToolHelper.WithinByteBudget(text, 1000);

        Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(1000);
        result.Should().StartWith("aaaa").And.Contain("Truncated");
    }

    [Test]
    public void MultiByteText_IsNeverCutInsideACharacter()
    {
        var text = new string('\u00e9', 5000);

        var result = ToolHelper.WithinByteBudget(text, 1001);

        Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(1001);
        result.Should().NotContain("\uFFFD");
    }
}
