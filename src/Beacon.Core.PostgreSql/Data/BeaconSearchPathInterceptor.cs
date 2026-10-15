using System.Data.Common;
using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Beacon.Core.PostgreSql.Data;

/// <summary>
/// Gives each connection Beacon opens from a host-owned data source Beacon's search path, which the connection-string
/// overload otherwise bakes into the data source it builds itself. Beacon's raw SQL and its pgvector migration name
/// tables and the vector type unqualified (<c>mcp_embeddings</c>, <c>vector(384)</c>), so without this they would
/// resolve through the host's search path instead. Npgsql's reset on pool return (DISCARD ALL) drops the setting
/// before the host's next use of the physical connection.
/// </summary>
internal sealed class BeaconSearchPathInterceptor : DbConnectionInterceptor
{
    private readonly string _statement;

    public BeaconSearchPathInterceptor(string schema)
    {
        // The identifier cannot be a parameter in SET; it is validated before being quoted into the statement.
        _statement = $"SET search_path TO \"{BeaconSchema.ValidateIdentifier(schema)}\", public";
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _statement;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = _statement;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
