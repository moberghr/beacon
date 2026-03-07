using System.Collections.Concurrent;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Services.Providers;

public static class ConnectorRegistry
{
    private static readonly ConcurrentDictionary<DatabaseEngineType, string> _registeredEngines = new();
    private static readonly ConcurrentDictionary<DataSourceType, string> _registeredDataSourceTypes = new();

    public static void RegisterDatabaseEngine(DatabaseEngineType engineType, string displayName)
    {
        _registeredEngines[engineType] = displayName;
    }

    public static void RegisterDataSourceType(DataSourceType dataSourceType, string displayName)
    {
        _registeredDataSourceTypes[dataSourceType] = displayName;
    }

    public static IReadOnlyDictionary<DatabaseEngineType, string> GetRegisteredDatabaseEngines() =>
        _registeredEngines;

    public static IReadOnlyDictionary<DataSourceType, string> GetRegisteredDataSourceTypes() =>
        _registeredDataSourceTypes;

    public static bool IsEngineRegistered(DatabaseEngineType engineType) =>
        _registeredEngines.ContainsKey(engineType);

    public static bool IsDataSourceTypeRegistered(DataSourceType dataSourceType) =>
        _registeredDataSourceTypes.ContainsKey(dataSourceType);
}
