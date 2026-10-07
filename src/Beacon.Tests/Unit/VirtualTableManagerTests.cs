using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Unit;

/// <summary>
/// The query-builder / AI-actor final query (<see cref="VirtualTableManager.ExecuteFinalQueryWithInMemoryDatabase"/>)
/// runs stored or LLM-written SQL against the in-memory SQLite join store, so it passes the read-only AST gate (§1.5)
/// like the MCP join paths — otherwise <c>ATTACH</c> creates files on the host.
/// </summary>
[TestFixture]
public class VirtualTableManagerTests
{
    private static readonly SqlReadOnlyAstValidator Validator = new(NullLogger<SqlReadOnlyAstValidator>.Instance);

    [Test]
    public async Task ExecuteFinalQuery_AttachStatement_IsRejectedBeforeTouchingTheHost()
    {
        var attachPath = Path.Combine(Path.GetTempPath(), $"beacon-attach-{Guid.NewGuid():N}.db");
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1 }], Project("orders"));

        var act = () => manager.ExecuteFinalQueryWithInMemoryDatabase(
            $"ATTACH '{attachPath}' AS x",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(attachPath).Should().BeFalse();
    }

    [Test]
    public async Task ExecuteFinalQuery_ReadOnlyJoin_PassesTheGateAndJoinsVirtualTables()
    {
        using var manager = new VirtualTableManager(NullLogger<VirtualTableManager>.Instance);
        manager.AddVirtualTable("@result1", [new Dictionary<string, object?> { ["id"] = 1L, ["name"] = "Ana" }], Project("customers"));
        manager.AddVirtualTable("@result2", [new Dictionary<string, object?> { ["customer_id"] = 1L, ["total"] = 250L }], Project("orders"));

        var result = await manager.ExecuteFinalQueryWithInMemoryDatabase(
            "SELECT c.name, o.total FROM @result1 c JOIN @result2 o ON o.customer_id = c.id",
            Validator,
            NullLogger<InMemoryDatabaseManager>.Instance,
            CancellationToken.None);

        result.TimedOut.Should().BeFalse();
        result.AllRecords.Should().ContainSingle();
        result.AllRecords[0]["name"].Should().Be("Ana");
        result.AllRecords[0]["total"].Should().Be(250L);
    }

    private static ProjectInfo Project(string name) =>
        new()
        {
            Name = name,
            DatabaseEngine = nameof(DatabaseEngineType.PostgreSQL),
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };
}
