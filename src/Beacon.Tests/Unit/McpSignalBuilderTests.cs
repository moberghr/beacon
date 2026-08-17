using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Beacon.MCP.Services;

namespace Beacon.Tests.Unit;

[TestFixture]
public class McpSignalBuilderTests
{
    [Test]
    public void SetColumnsUsed_SerializesIntoSignal()
    {
        var signal = new McpSignalBuilder()
            .SetQuestion("how many orders last week")
            .SetColumnsUsed(["id", "created_time"])
            .Build();

        signal.ColumnsUsed.Should().NotBeNull();
        JsonSerializer.Deserialize<List<string>>(signal.ColumnsUsed!).Should().Equal("id", "created_time");
    }

    [Test]
    public void SetColumnsUsed_EmptyList_StaysNull()
    {
        var signal = new McpSignalBuilder()
            .SetQuestion("q")
            .SetColumnsUsed([])
            .Build();

        signal.ColumnsUsed.Should().BeNull();
    }
}
