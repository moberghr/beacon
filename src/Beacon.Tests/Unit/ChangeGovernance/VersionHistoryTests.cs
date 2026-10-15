using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The versions a save writes record the content hash of what they hold — an Active version made from the stored query and
/// the Archived baseline of the SQL an edit replaces — and a baseline is written once: not when a version that was live
/// (Active or Archived) already holds the same content.
/// </summary>
[TestFixture]
public class VersionHistoryTests
{
    [Test]
    public async Task CreateVersionAsync_RecordsTheStoredQuerysContentHash()
    {
        var store = new GovernanceStore();

        var version = await TestQueryServices.VersionService(store.Factory())
            .CreateVersionAsync(GovernanceStore.QueryId, "ext-ada", "UserEdit", null, QueryVersionStatus.Active, CancellationToken.None);

        version.ContentHash.Should().Be(QueryContentHash.Of(store.Query).Hash);
        store.Saved.OfType<QueryVersion>().Should().ContainSingle().Which.Should().BeSameAs(version);
    }

    [Test]
    public async Task EnsureBaselineVersionAsync_RecordsTheStoredQuerysContentHash()
    {
        var store = new GovernanceStore();

        await TestQueryServices.VersionService(store.Factory()).EnsureBaselineVersionAsync(GovernanceStore.QueryId, CancellationToken.None);

        store.Saved.OfType<QueryVersion>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Status = QueryVersionStatus.Archived,
            ChangeSource = QueryVersionService.BaselineSource,
            ContentHash = QueryContentHash.Of(store.Query).Hash,
        }, x => x.ExcludingMissingMembers());
    }

    // The stored SQL was changed back to what an earlier version held (outside a versioned save): history holds it already.
    [TestCase(QueryVersionStatus.Archived, false)]
    [TestCase(QueryVersionStatus.Superseded, true)]
    [TestCase(QueryVersionStatus.Rejected, true)]
    public async Task EnsureBaselineVersionAsync_WhenALiveVersionHoldsTheSameContent_WritesNoSecondBaseline(QueryVersionStatus earlier, bool written)
    {
        var store = new GovernanceStore();
        store.AddVersion(1, earlier, "SELECT 1");
        store.Activate(store.AddVersion(2, QueryVersionStatus.Active, "SELECT 2"));

        await TestQueryServices.VersionService(store.Factory()).EnsureBaselineVersionAsync(GovernanceStore.QueryId, CancellationToken.None);

        store.Saved.OfType<QueryVersion>().Should().HaveCount(written ? 1 : 0, "a version nobody approved does not hold the SQL for history");
    }
}
