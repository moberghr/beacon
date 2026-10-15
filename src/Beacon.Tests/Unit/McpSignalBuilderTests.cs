using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services;
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

    // The digest a promotion's claim compares is written with the SQL it digests: the corrected SQL, else the generated SQL.
    [TestCase("SELECT id FROM orders", null, "SELECT id FROM orders")]
    [TestCase("SELECT id FROM order", "SELECT id FROM orders", "SELECT id FROM orders")]
    public void Build_StoresTheDigestOfTheSqlAPromotionStartsFrom(string generated, string? corrected, string digested)
    {
        var builder = new McpSignalBuilder()
            .SetQuestion("q")
            .SetGeneratedSql(generated);
        if (corrected != null)
        {
            builder.SetRetry(corrected, succeeded: true);
        }

        builder.Build().CandidateSqlHash.Should().Be(GroundingContentHash.OfSql(digested));
    }

    [Test]
    public void Build_WithoutSql_StoresNoDigest()
    {
        new McpSignalBuilder().SetQuestion("q").Build().CandidateSqlHash.Should().BeNull();
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
