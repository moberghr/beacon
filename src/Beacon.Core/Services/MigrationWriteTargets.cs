using System.Globalization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services;

/// <summary>
/// The segregated write path of data migrations. A migration writes only to a data source an Admin marked as a write
/// target (<see cref="DataSource.AllowMigrationWrites"/>), and Beacon never reads through such a source: its connection may
/// carry a login with write grants, which only the migration writer may use. Every other data source stays a read source,
/// which should be connected with a login that can only read. The read refusal is enforced where a database connection is
/// made from a data source's credentials (the connection resolver, the metadata scan), and project-scoped listings for
/// MCP and AI leave write targets (and disabled sources) out (<see cref="WhereReadable"/>).
/// </summary>
public static class MigrationWriteTargets
{
    /// <summary>What a reader is told when it reaches a write target.</summary>
    public const string ReadRefusedMessage =
        "This data source is a migration write target: Beacon does not read from it. Choose another data source.";

    /// <summary>What a caller is told while the deployment does not run migrations.</summary>
    public const string DisabledMessage =
        "Data migration is turned off for this deployment (Beacon:Migrations:Enabled).";

    /// <summary>The engines the migration writer supports, the only ones a write target may have.</summary>
    public static readonly IReadOnlyList<DatabaseEngineType> SupportedEngines =
    [
        DatabaseEngineType.PostgreSQL,
        DatabaseEngineType.MySQL,
        DatabaseEngineType.MSSQL
    ];

    /// <summary>Throws <see cref="InvalidOperationException"/> when <paramref name="dataSource"/> is a write target.</summary>
    public static void EnsureReadable(DataSource dataSource)
    {
        if (dataSource.AllowMigrationWrites)
        {
            throw new InvalidOperationException(ReadRefusedMessage);
        }
    }

    /// <summary>Why <paramref name="dataSource"/> cannot be marked as a write target; null when it can.</summary>
    public static string? MarkRefusal(DataSource dataSource)
    {
        if (dataSource.HostManagedKey != null)
        {
            return "A host data source is read-only and cannot be a migration write target.";
        }

        if (dataSource.IsReadOnly)
        {
            return "The data source is marked read-only and cannot be a migration write target.";
        }

        if (dataSource.DataSourceType != DataSourceType.Database
            || dataSource.DatabaseEngineType is not { } engine
            || !SupportedEngines.Contains(engine))
        {
            return "Only a PostgreSQL, MySQL or SQL Server database can be a migration write target.";
        }

        return null;
    }

    /// <summary>Why a migration may not write to <paramref name="dataSource"/>; null when it may.</summary>
    public static string? DestinationRefusal(DataSource dataSource)
    {
        if (!dataSource.AllowMigrationWrites)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Data source {dataSource.Id} is not a migration write target: an Admin must mark it before a migration can write to it.");
        }

        return MarkRefusal(dataSource);
    }

    /// <summary>
    /// The project links whose data source Beacon reads: every link but a write target's or a disabled source's. A link read
    /// without its data source (an in-memory projection) has nothing marking it and counts as readable; in SQL the data
    /// source is joined.
    /// </summary>
    public static IQueryable<ProjectDataSource> WhereReadable(this IQueryable<ProjectDataSource> links)
    {
        return links
            .Where(x => x.DataSource == null || !x.DataSource.AllowMigrationWrites)
            .Where(x => x.DataSource == null || x.DataSource.DisabledTime == null);
    }

    /// <summary>
    /// Why <paramref name="dataSourceId"/> is not marked without the Admin's confirmation: the live queries, subscriptions and
    /// data contracts that read it, which fail once Beacon no longer reads it.
    /// </summary>
    public static string InUseMessage(int dataSourceId, int queryCount, int subscriptionCount, int contractCount)
    {
        var uses = new List<string>();
        if (queryCount > 0)
        {
            uses.Add(Counted(queryCount, "query", "queries"));
        }

        if (subscriptionCount > 0)
        {
            uses.Add(Counted(subscriptionCount, "subscription", "subscriptions"));
        }

        if (contractCount > 0)
        {
            uses.Add(Counted(contractCount, "data contract", "data contracts"));
        }

        var listed = uses.Count > 1 ? $"{string.Join(", ", uses.Take(uses.Count - 1))} and {uses[^1]}" : uses.Single();

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Data source {dataSourceId} is read by {listed}. Beacon stops reading a migration write target, so they will fail. Confirm to mark it anyway.");
    }

    /// <summary>The live queries with a step on <paramref name="dataSourceId"/>.</summary>
    internal static IQueryable<Query> WhereReading(this IQueryable<Query> queries, int dataSourceId)
    {
        return queries.Where(x => x.Steps.Any(y => y.DataSourceId == dataSourceId));
    }

    /// <summary>The live subscriptions whose query has a step on <paramref name="dataSourceId"/>.</summary>
    internal static IQueryable<Subscription> WhereReading(this IQueryable<Subscription> subscriptions, int dataSourceId)
    {
        return subscriptions.Where(x => x.Query.Steps.Any(y => y.DataSourceId == dataSourceId));
    }

    /// <summary>The live data contracts on <paramref name="dataSourceId"/>.</summary>
    internal static IQueryable<DataContract> WhereReading(this IQueryable<DataContract> contracts, int dataSourceId)
    {
        return contracts.Where(x => x.DataSourceId == dataSourceId);
    }

    private static string Counted(int count, string one, string many) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {(count == 1 ? one : many)}");
}
