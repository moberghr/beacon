using Beacon.Core.Data.Entities;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// QueryExecutionHistory is the highest-volume table (one row per execution). The Home dashboard,
/// uptime rail, and trend handlers all filter it by CreatedTime / NotificationStatus / SubscriptionId,
/// so these indexes must exist to keep those reads off full table scans as the table grows.
/// </summary>
[TestFixture]
public class QueryExecutionHistoryIndexTests
{
    private static IReadOnlyList<IReadOnlyList<string>> GetIndexes()
    {
        using var context = NpgsqlTestContext.Create();
        var entity = context.Model.FindEntityType(typeof(QueryExecutionHistory))!;

        return entity.GetIndexes()
            .Select(x => (IReadOnlyList<string>)x.Properties.Select(p => p.Name).ToList())
            .ToList();
    }

    [Test]
    public void CreatedTime_Index_Exists()
    {
        GetIndexes()
            .Should()
            .ContainEquivalentOf(new[] { nameof(QueryExecutionHistory.CreatedTime) });
    }

    [Test]
    public void NotificationStatus_CreatedTime_CompositeIndex_Exists()
    {
        GetIndexes()
            .Should()
            .ContainEquivalentOf(new[]
            {
                nameof(QueryExecutionHistory.NotificationStatus),
                nameof(QueryExecutionHistory.CreatedTime),
            });
    }

    [Test]
    public void SubscriptionId_CreatedTime_CompositeIndex_Exists()
    {
        GetIndexes()
            .Should()
            .ContainEquivalentOf(new[]
            {
                nameof(QueryExecutionHistory.SubscriptionId),
                nameof(QueryExecutionHistory.CreatedTime),
            });
    }
}
