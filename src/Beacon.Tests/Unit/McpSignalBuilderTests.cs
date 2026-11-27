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

    // SC6: the MCP caller's hash reaches the signal; a caller without one leaves it null.
    [TestCase("9f2c1ab0", "9f2c1ab0")]
    [TestCase(null, null)]
    [TestCase("", null)]
    public void Build_WritesTheCallerHash(string? callerHash, string? expected)
    {
        var signal = new McpSignalBuilder()
            .SetTool("query")
            .SetQuestion("q")
            .SetCallerHash(callerHash)
            .Build();

        signal.CallerHash.Should().Be(expected);
    }

    // The requested parent is read from the builder for the signal service to verify; it never sits on the built entity.
    [Test]
    public void Build_KeepsTheRequestedParentOffTheEntity()
    {
        var builder = new McpSignalBuilder()
            .SetTool("query")
            .SetQuestion("q")
            .SetRequestedParent(812);

        builder.RequestedParentId.Should().Be(812);
        var signal = builder.Build();
        signal.ParentSignalId.Should().BeNull("the unverified signal_id is never put on the entity's parent link");
        signal.IsEditedRerun.Should().BeFalse("the builder never decides whether the SQL was edited");
    }

    [Test]
    public void Build_WithoutRequestedParent_CarriesNone()
    {
        var builder = new McpSignalBuilder()
            .SetTool("query")
            .SetQuestion("q");

        builder.RequestedParentId.Should().BeNull();
        builder.Build().ParentSignalId.Should().BeNull();
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
