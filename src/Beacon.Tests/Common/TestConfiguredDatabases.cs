using Beacon.Core.Data.Entities;
using Beacon.Core.Services.Validation;

namespace Beacon.Tests.Common;

/// <summary>Fixed answers for the database a data source's connection is configured for.</summary>
internal static class TestConfiguredDatabases
{
    /// <summary>No data source has a configured database: only unqualified and schema-qualified names pass.</summary>
    public static IConfiguredDatabaseResolver None { get; } = new Fixed(null);

    /// <summary>Every data source is configured for <paramref name="database"/>.</summary>
    public static IConfiguredDatabaseResolver Of(string? database) => new Fixed(database);

    private sealed class Fixed(string? database) : IConfiguredDatabaseResolver
    {
        public string? Resolve(DataSource dataSource) => database;
    }
}
