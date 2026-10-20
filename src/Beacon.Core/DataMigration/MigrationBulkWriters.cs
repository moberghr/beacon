using Beacon.Core.Data.Enums;
using Beacon.Core.Models;

namespace Beacon.Core.DataMigration;

/// <summary>
/// The registered <see cref="IMigrationBulkWriter"/>s by engine. A connector package adds its engine's writer
/// (<c>AddPostgreSqlConnector()</c>, <c>AddSqlServerConnector()</c>); a run into an engine without one fails before the
/// destination's credentials are decrypted.
/// </summary>
internal sealed class MigrationBulkWriters(IEnumerable<IMigrationBulkWriter> writers)
{
    private readonly IReadOnlyDictionary<DatabaseEngineType, IMigrationBulkWriter> _writers = writers
        .GroupBy(x => x.Engine)
        .ToDictionary(x => x.Key, x => x.Last());

    /// <summary>The writer of <paramref name="engine"/>; throws, naming the connector package to add, when none is registered.</summary>
    public IMigrationBulkWriter Require(DatabaseEngineType engine)
    {
        if (_writers.TryGetValue(engine, out var writer))
        {
            return writer;
        }

        throw new BeaconException(MissingMessage(engine));
    }

    internal static string MissingMessage(DatabaseEngineType engine)
    {
        return engine switch
        {
            DatabaseEngineType.PostgreSQL => Missing("PostgreSQL", "PostgreSql"),
            DatabaseEngineType.MSSQL => Missing("SQL Server", "SqlServer"),
            DatabaseEngineType.MySQL => Missing("MySQL", "MySql"),
            _ => "Only a PostgreSQL, MySQL or SQL Server database can be a migration write target."
        };
    }

    private static string Missing(string engineName, string connector)
    {
        return $"{engineName} migration targets need the Moberg.Beacon.Connector.{connector} package: reference it and call "
            + $"Add{connector}Connector() on the Beacon builder.";
    }
}
