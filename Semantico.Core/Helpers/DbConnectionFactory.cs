using System.Collections.Concurrent;
using System.Data.Common;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;

namespace Semantico.Core.Helpers;

public static class DbConnectionFactory
{
    private static readonly ConcurrentDictionary<DatabaseEngineType, Func<string, DbConnection>> _factories = new();

    public static void Register(DatabaseEngineType engineType, Func<string, DbConnection> factory)
    {
        _factories[engineType] = factory;
    }

    public static DbConnection CreateConnection(DatabaseEngineType dbEngineType, string connectionString)
    {
        if (_factories.TryGetValue(dbEngineType, out var factory))
            return factory(connectionString);

        throw new SemanticoException(
            $"No connection factory registered for database engine: {dbEngineType}. " +
            "Make sure to register the appropriate connector (e.g., AddPostgreSqlConnector()).");
    }
}
