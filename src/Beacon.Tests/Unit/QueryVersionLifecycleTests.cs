using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A save used to write two versions — an archived copy of the previous state and a new Active one — and never
/// archived the version that had been Active, so history doubled up and a query collected several Active
/// versions (which made approving throw). Now a save writes one Active version that replaces every older one,
/// the pre-edit copy is written only when history does not already hold it, and approve/restore cope with the
/// several-Active data older installations already have.
/// </summary>
[TestFixture]
public class QueryVersionLifecycleTests
{
    private const int QueryId = 7;

    [Test]
    public async Task CreateVersionAsync_Active_ArchivesEveryPreviouslyActiveVersion()
    {
        var store = new Store();
        store.Versions.AddRange([Version(1, QueryVersionStatus.Active), Version(2, QueryVersionStatus.Active), Version(3, QueryVersionStatus.Archived)]);

        await store.VersionService().CreateVersionAsync(QueryId, "u1", "UserEdit", null, QueryVersionStatus.Active, CancellationToken.None);

        store.Versions.Should().OnlyContain(x => x.Status == QueryVersionStatus.Archived);
        var created = store.Saved.OfType<QueryVersion>().Should().ContainSingle().Subject;
        created.Status.Should().Be(QueryVersionStatus.Active);
        created.VersionNumber.Should().Be(4);
        store.Query.ActiveVersion.Should().BeSameAs(created);
    }

    [Test]
    public async Task EnsureBaselineVersionAsync_NoVersionYet_WritesAnUnattributedArchivedCopy()
    {
        var store = new Store();

        await store.VersionService().EnsureBaselineVersionAsync(QueryId, CancellationToken.None);

        var baseline = store.Saved.OfType<QueryVersion>().Should().ContainSingle().Subject;
        baseline.Status.Should().Be(QueryVersionStatus.Archived);
        baseline.ChangeSource.Should().Be("Baseline");
        baseline.CreatedByUserId.Should().BeNull("the stored SQL was not written by the user making this edit");
        baseline.CreatedByUserName.Should().BeNull();
        baseline.StepsJson.Should().Contain("SELECT 1");
    }

    [Test]
    public async Task EnsureBaselineVersionAsync_ActiveVersionHoldsTheStoredQuery_WritesNothing()
    {
        var store = new Store();
        await store.ActivateCurrentState();

        await store.VersionService().EnsureBaselineVersionAsync(QueryId, CancellationToken.None);

        store.Saved.Should().BeEmpty();
    }

    [Test]
    public async Task EnsureBaselineVersionAsync_QueryChangedOutsideAVersionedSave_WritesTheCopy()
    {
        var store = new Store();
        await store.ActivateCurrentState();
        store.Query.Steps[0].SqlValue = "SELECT 1 AS changed_elsewhere";

        await store.VersionService().EnsureBaselineVersionAsync(QueryId, CancellationToken.None);

        store.Saved.OfType<QueryVersion>().Should().ContainSingle()
            .Which.StepsJson.Should().Contain("changed_elsewhere");
    }

    [Test]
    public async Task ApproveAsync_SeveralActiveVersions_ArchivesThemAndActivatesTheProposal()
    {
        var store = new Store();
        store.Versions.AddRange([Version(1, QueryVersionStatus.Active), Version(2, QueryVersionStatus.Active)]);
        var proposal = Version(3, QueryVersionStatus.PendingApproval);
        proposal.StepsJson = "[]";
        store.Versions.Add(proposal);
        store.Requests.Add(new QueryApprovalRequest { Id = 90, QueryId = QueryId, QueryVersionId = proposal.Id, QueryVersion = proposal });

        await store.ApprovalService().ApproveAsync(90, "rev", "Reviewer", null, CancellationToken.None);

        store.Versions.Where(x => x.VersionNumber < 3).Should().OnlyContain(x => x.Status == QueryVersionStatus.Archived);
        proposal.Status.Should().Be(QueryVersionStatus.Active);
        store.Query.ActiveVersionId.Should().Be(proposal.Id);
    }

    [Test]
    public async Task RestoreVersionAsync_SeveralActiveVersions_ArchivesThemAll()
    {
        var store = new Store();
        var old = Version(1, QueryVersionStatus.Archived);
        old.StepsJson = "[]";
        store.Versions.AddRange([old, Version(2, QueryVersionStatus.Active), Version(3, QueryVersionStatus.Active)]);

        await store.VersionService().RestoreVersionAsync(old.Id, "u1", CancellationToken.None);

        store.Versions.Should().OnlyContain(x => x.Status == QueryVersionStatus.Archived);
        store.Saved.OfType<QueryVersion>().Should().ContainSingle().Which.Status.Should().Be(QueryVersionStatus.Active);
    }

    [Test]
    public async Task UpdateQuery_SqlEdit_WritesOneActiveVersionAndNoArchivedCopy()
    {
        var store = new Store();
        var versions = new Mock<IQueryVersionService>();

        await store.QueryService(versions.Object).UpdateQuery(
            new QueryData
            {
                QueryId = QueryId,
                Name = "Orders",
                Steps = [new QueryStepData { StepId = 11, StepOrder = 1, Name = "Step 1", SqlValue = "SELECT 2", DataSourceId = 3 }],
            },
            CancellationToken.None);

        versions.Verify(x => x.EnsureBaselineVersionAsync(QueryId, It.IsAny<CancellationToken>()), Times.Once);
        versions.Verify(
            x => x.CreateVersionAsync(QueryId, "u1", "UserEdit", null, QueryVersionStatus.Active, It.IsAny<CancellationToken>()),
            Times.Once);
        versions.Verify(
            x => x.CreateVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), QueryVersionStatus.Archived, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private static QueryVersion Version(int number, QueryVersionStatus status) =>
        new()
        {
            Id = 100 + number,
            QueryId = QueryId,
            VersionNumber = number,
            Status = status,
            Name = "Orders",
            StepsJson = "[]",
        };

    /// <summary>One stored query with one step, its versions and approval requests, behind recording contexts.</summary>
    private sealed class Store
    {
        public List<object> Saved { get; } = [];

        public List<QueryVersion> Versions { get; } = [];

        public List<QueryApprovalRequest> Requests { get; } = [];

        public Query Query { get; } = new()
        {
            Id = QueryId,
            Name = "Orders",
            Steps =
            [
                new QueryStep
                {
                    Id = 11,
                    QueryId = QueryId,
                    DataSourceId = 3,
                    DataSource = new DataSource
                    {
                        Id = 3,
                        Name = "warehouse",
                        DataSourceType = DataSourceType.Database,
                        EncryptedConnectionData = "unused",
                    },
                    StepOrder = 1,
                    SqlValue = "SELECT 1",
                },
            ],
        };

        public QueryVersionService VersionService() =>
            new(Factory(), UserContext(), NullLogger<QueryVersionService>.Instance);

        public QueryApprovalService ApprovalService() => new(Factory(), VersionService());

        public QueryService QueryService(IQueryVersionService versions) =>
            new(
                Factory(),
                Mock.Of<Beacon.Core.HostData.IDataSourceConnectionResolver>(),
                Mock.Of<IManualQueryExecutionLogger>(),
                NullLogger<QueryService>.Instance,
                NullLoggerFactory.Instance,
                versions,
                new BeaconConfiguration(),
                UserContext(),
                new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
                Mock.Of<Beacon.Core.HostData.IHostDataSourceGuard>());

        /// <summary>A save as it happens now: the stored state becomes the Active version, which history then holds.</summary>
        public async Task ActivateCurrentState()
        {
            await VersionService().CreateVersionAsync(QueryId, "u1", "UserEdit", null, QueryVersionStatus.Active, CancellationToken.None);
            Versions.AddRange(Saved.OfType<QueryVersion>());
            Saved.Clear();
        }

        private IDbContextFactory<BeaconContext> Factory()
        {
            var sets = new Dictionary<Type, object>
            {
                [typeof(Query)] = RecordingBeaconContext.MemorySet<Query>([Query], Saved),
                [typeof(QueryVersion)] = RecordingBeaconContext.MemorySet(Versions, Saved),
                [typeof(QueryApprovalRequest)] = RecordingBeaconContext.MemorySet(Requests, Saved),
            };

            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new RecordingBeaconContext(sets, Saved));
            return factory.Object;
        }

        private static IBeaconUserContext UserContext() =>
            Mock.Of<IBeaconUserContext>(x => x.UserId == "u1" && x.DisplayName == "Ana Admin");
    }
}
