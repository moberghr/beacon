using System.Data.Common;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Services;

/// <summary>
/// Builds the <see cref="DynamicDbContext"/> options a migration job bulk-writes through. PostgreSQL and SQL Server
/// are configured here; any other engine needs an <see cref="IMigrationBulkContextProvider"/> registered by its
/// connector package.
/// </summary>
internal sealed class MigrationBulkContextFactory(IEnumerable<IMigrationBulkContextProvider> providers)
{
    internal const string MySqlMissingMessage =
        "MySQL migration targets need the Moberg.Beacon.Connector.MySql package: reference it and call "
        + "AddMySqlConnector() on the Beacon builder.";

    private readonly IReadOnlyDictionary<DatabaseEngineType, IMigrationBulkContextProvider> _providers = providers
        .GroupBy(x => x.EngineType)
        .ToDictionary(x => x.Key, x => x.Last());

    /// <summary>
    /// Fails before any destination connection is opened when the engine's bulk provider is missing. Without it the
    /// bulk write would throw inside the generic path, which falls back to row-by-row inserts and hides the cause.
    /// </summary>
    public void EnsureAvailable(DatabaseEngineType engineType)
    {
        if (engineType == DatabaseEngineType.MySQL && !_providers.ContainsKey(engineType))
        {
            throw new BeaconException(MySqlMissingMessage);
        }
    }

    public DynamicDbContext Create(DbConnection connection, DbTransaction transaction, DatabaseEngineType engineType, string? tableName, List<string>? primaryKeys)
    {
        var optionsBuilder = new DbContextOptionsBuilder<DynamicDbContext>();

        if (_providers.TryGetValue(engineType, out var provider))
        {
            provider.Configure(optionsBuilder, connection);
        }
        else
        {
            switch (engineType)
            {
                case DatabaseEngineType.PostgreSQL:
                    optionsBuilder.UseNpgsql(connection);
                    break;
                case DatabaseEngineType.MSSQL:
                    optionsBuilder.UseSqlServer(connection);
                    break;
                case DatabaseEngineType.MySQL:
                    throw new BeaconException(MySqlMissingMessage);
                default:
                    throw new NotSupportedException($"Database engine type '{engineType}' is not supported");
            }
        }

        var context = new DynamicDbContext(optionsBuilder.Options, tableName, primaryKeys);
        context.Database.UseTransaction(transaction);
        return context;
    }
}
