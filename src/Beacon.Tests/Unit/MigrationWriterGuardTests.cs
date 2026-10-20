using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Core.DataMigration;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Each writer refuses, before it sends anything, a connection of another driver and an upsert without key columns.
/// Recording ADO.NET doubles, no database (§4.7).
/// </summary>
[TestFixture]
public class MigrationWriterGuardTests
{
    private static readonly MigrationRows OneRow = new(["id"], [new Dictionary<string, object?> { ["id"] = 1 }]);

    private static IEnumerable<TestCaseData> Writers()
    {
        yield return new TestCaseData(new PostgreSqlMigrationBulkWriter(NullLogger<PostgreSqlMigrationBulkWriter>.Instance), "Npgsql")
            .SetName("PostgreSQL");
        yield return new TestCaseData(new SqlServerMigrationBulkWriter(NullLogger<SqlServerMigrationBulkWriter>.Instance), "SqlClient")
            .SetName("SQL Server");
    }

    [TestCaseSource(nameof(Writers))]
    public async Task AConnectionOfAnotherDriver_IsRefused_BeforeAnyCommand(IMigrationBulkWriter writer, string driver)
    {
        var connection = new RecordingDbConnection();
        var target = new MigrationWriteTarget(connection, connection.BeginTransaction(), null, "orders", 30);

        var clear = () => writer.ClearAsync(target, CancellationToken.None);
        var insert = () => writer.InsertAsync(target, OneRow, CancellationToken.None);
        var upsert = () => writer.UpsertAsync(target, OneRow, ["id"], CancellationToken.None);

        await clear.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{driver}*");
        await insert.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{driver}*");
        await upsert.Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{driver}*");
        connection.ExecutedCommands.Should().BeEmpty();
    }

    [TestCaseSource(nameof(Writers))]
    public async Task AnUpsertWithoutKeyColumns_IsRefused_BeforeAnyCommand(IMigrationBulkWriter writer, string driver)
    {
        var connection = new RecordingDbConnection();
        var target = new MigrationWriteTarget(connection, connection.BeginTransaction(), null, "orders", 30);

        var upsert = () => writer.UpsertAsync(target, OneRow, [], CancellationToken.None);

        await upsert.Should().ThrowAsync<InvalidOperationException>().WithMessage("*primary key*");
        connection.ExecutedCommands.Should().BeEmpty();
    }
}
