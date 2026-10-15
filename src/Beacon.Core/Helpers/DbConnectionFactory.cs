using System.Collections.Concurrent;
using System.Data.Common;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;

namespace Beacon.Core.Helpers;

public static class DbConnectionFactory
{
    private static readonly ConcurrentDictionary<DatabaseEngineType, Func<string, DbConnection>> _factories = new();

    private static readonly ConcurrentDictionary<DatabaseEngineType, Action<DbConnection>> _poolClearers = new();

    /// <summary>
    /// Registers how connections to <paramref name="engineType"/> are made and, when its driver pools connections, how the
    /// pool of one connection string is emptied (<paramref name="clearPool"/>, given an unopened connection).
    /// </summary>
    public static void Register(DatabaseEngineType engineType, Func<string, DbConnection> factory, Action<DbConnection>? clearPool = null)
    {
        _factories[engineType] = factory;
        if (clearPool == null)
        {
            _poolClearers.TryRemove(engineType, out _);

            return;
        }

        _poolClearers[engineType] = clearPool;
    }

    public static DbConnection CreateConnection(DatabaseEngineType dbEngineType, string connectionString)
    {
        if (_factories.TryGetValue(dbEngineType, out var factory))
            return factory(connectionString);

        throw new BeaconException(
            $"No connection factory registered for database engine: {dbEngineType}. " +
            "Make sure to register the appropriate connector (e.g., AddPostgreSqlConnector()).");
    }

    /// <summary>
    /// Empties the driver's pools of <paramref name="connectionString"/>, so no idle connection opened with it is reused
    /// (idle ones are closed; one in use closes when returned): every pool Beacon opens with it, see
    /// <see cref="PooledConnectionStrings"/>. Each pool is cleared even when another fails; the first failure is thrown
    /// after. False when the engine registered no pool clearer: its driver keeps no pool Beacon can empty.
    /// </summary>
    public static bool ClearPool(DatabaseEngineType engineType, string connectionString)
    {
        if (!_poolClearers.TryGetValue(engineType, out var clearPool))
        {
            return false;
        }

        Exception? failure = null;
        foreach (var pooled in PooledConnectionStrings(engineType, connectionString))
        {
            try
            {
                using var connection = CreateConnection(engineType, pooled);
                clearPool(connection);
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        if (failure != null)
        {
            throw failure;
        }

        return true;
    }

    /// <summary>
    /// The connection strings Beacon opens connections with for a data source's <paramref name="connectionString"/>, each
    /// a pool of its own: the connection string itself and, on MySQL, the read-only form without statement batches
    /// (<see cref="WithoutStatementBatches"/>).
    /// </summary>
    public static IReadOnlyList<string> PooledConnectionStrings(DatabaseEngineType engineType, string connectionString)
    {
        return engineType == DatabaseEngineType.MySQL
            ? new[] { connectionString, WithoutStatementBatches(connectionString) }.Distinct(StringComparer.Ordinal).ToList()
            : [connectionString];
    }

    /// <summary>
    /// The MySQL connection string with statement batches off (MySql.Data's <c>AllowBatch=false</c>), the one every
    /// read-only MySQL read connects with. With batches on, MySQL runs every statement of a command, so
    /// <c>COMMIT; &lt;write&gt;</c> would end the read-only transaction and then write. The string differs from the
    /// ordinary one, so its connections are pooled apart and only ever run read-only work.
    /// </summary>
    public static string WithoutStatementBatches(string connectionString)
    {
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        var batchKeys = builder.Keys
            .Cast<string>()
            .Where(x => x.Replace(" ", "").Equals("allowbatch", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in batchKeys)
        {
            builder.Remove(key);
        }

        builder["AllowBatch"] = "false";

        return builder.ConnectionString;
    }
}
