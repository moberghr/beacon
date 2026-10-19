using System.Collections.Concurrent;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Providers;

public static class ConnectorRegistry
{
    private const string UncalledMemberObsoleteMessage = "Not called by Beacon; removed in 5.0.";

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

    [Obsolete(UncalledMemberObsoleteMessage)]
    public static IReadOnlyDictionary<DatabaseEngineType, string> GetRegisteredDatabaseEngines() =>
        _registeredEngines;

    [Obsolete(UncalledMemberObsoleteMessage)]
    public static IReadOnlyDictionary<DataSourceType, string> GetRegisteredDataSourceTypes() =>
        _registeredDataSourceTypes;

    [Obsolete(UncalledMemberObsoleteMessage)]
    public static bool IsEngineRegistered(DatabaseEngineType engineType) =>
        _registeredEngines.ContainsKey(engineType);

    [Obsolete(UncalledMemberObsoleteMessage)]
    public static bool IsDataSourceTypeRegistered(DataSourceType dataSourceType) =>
        _registeredDataSourceTypes.ContainsKey(dataSourceType);

    public static string GetDisplayName(DataSourceType dataSourceType) =>
        _registeredDataSourceTypes.GetValueOrDefault(dataSourceType, dataSourceType.ToString());

    public static string GetDisplayName(DatabaseEngineType engineType) =>
        _registeredEngines.GetValueOrDefault(engineType, engineType.ToString());
}
