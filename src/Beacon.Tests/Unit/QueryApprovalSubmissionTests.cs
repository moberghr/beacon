using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
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
/// With the approval workflow on, saving a SQL edit files a PendingApproval version, and approving that version
/// applies its steps to the live query. The version used to snapshot the stored query, so the submitted SQL was
/// lost and approval put the old SQL back. These pin that the version carries the edit (checked like a direct
/// save), names its requester, and leaves the live query alone until approved. The contexts serve their sets
/// from memory and record added entities instead of saving (§4.7: no in-memory DB).
/// </summary>
[TestFixture]
public class QueryApprovalSubmissionTests
{
    private const int QueryId = 7;
    private const int DataSourceId = 3;

    [Test]
    public async Task UpdateQuery_ApprovalWorkflow_SubmitsTheEditedSqlAndLeavesTheLiveQueryUntouched()
    {
        var stored = StoredQuery();
        var saved = new List<object>();
        QueryData? proposed = null;
        var versions = new Mock<IQueryVersionService>();
        versions
            .Setup(x => x.CreateProposedVersionAsync(QueryId, It.IsAny<QueryData>(), "u1", "UserEdit", "Submitted for approval", It.IsAny<CancellationToken>()))
            .Callback<int, QueryData, string?, string?, string?, CancellationToken>((_, data, _, _, _, _) => proposed = data)
            .ReturnsAsync(new QueryVersion { Id = 50, QueryId = QueryId, Name = "Orders" });

        var response = await BuildService(stored, saved, versions.Object).UpdateQuery(Edit("SELECT 2"), CancellationToken.None);

        response.Message.Should().Be("Changes submitted for approval");
        proposed!.Steps.Should().ContainSingle().Which.SqlValue.Should().Be("SELECT 2");
        stored.Steps[0].SqlValue.Should().Be("SELECT 1", "the edit only goes live once approved");
        versions.Verify(
            x => x.CreateVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<QueryVersionStatus>(), It.IsAny<CancellationToken>()),
            Times.Never);
        saved.OfType<QueryApprovalRequest>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            QueryId,
            QueryVersionId = 50,
            Status = ApprovalStatus.Pending,
            RequestedByUserId = "u1",
            RequestedByUserName = "Ana Admin",
        });
    }

    [Test]
    public async Task UpdateQuery_ApprovalWorkflow_BlockedKeyword_SubmitsNothing()
    {
        var saved = new List<object>();
        var versions = new Mock<IQueryVersionService>();

        var act = () => BuildService(StoredQuery(), saved, versions.Object).UpdateQuery(Edit("DELETE FROM orders"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconException>().WithMessage("*blocked SQL keywords*");
        versions.Verify(
            x => x.CreateProposedVersionAsync(It.IsAny<int>(), It.IsAny<QueryData>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        saved.Should().BeEmpty();
    }

    [Test]
    public async Task CreateProposedVersionAsync_SnapshotsTheProposedStepsWithTheirAuthor()
    {
        var saved = new List<object>();
        var service = new QueryVersionService(Factory(saved), UserContext(), NullLogger<QueryVersionService>.Instance);

        await service.CreateProposedVersionAsync(QueryId, Edit("SELECT 2"), "u1", "UserEdit", "Submitted for approval", CancellationToken.None);

        var version = saved.OfType<QueryVersion>().Should().ContainSingle().Subject;
        version.Status.Should().Be(QueryVersionStatus.PendingApproval);
        version.VersionNumber.Should().Be(1);
        version.CreatedByUserName.Should().Be("Ana Admin");
        var steps = JsonSerializer.Deserialize<List<QueryStepSnapshot>>(version.StepsJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        steps.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            SqlValue = "SELECT 2",
            DataSourceId,
            DataSourceName = "warehouse",
        });
    }

    [Test]
    public async Task CreateProposedVersionAsync_UnknownDataSource_Throws()
    {
        var saved = new List<object>();
        var service = new QueryVersionService(Factory(saved), UserContext(), NullLogger<QueryVersionService>.Instance);
        var edit = Edit("SELECT 2");
        edit.Steps[0].DataSourceId = 42;

        var act = () => service.CreateProposedVersionAsync(QueryId, edit, "u1", "UserEdit", null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Data source 42 not found.");
        saved.Should().BeEmpty();
    }

    private static Query StoredQuery() =>
        new()
        {
            Id = QueryId,
            Name = "Orders",
            Steps =
            [
                new QueryStep
                {
                    Id = 11,
                    QueryId = QueryId,
                    DataSourceId = DataSourceId,
                    StepOrder = 1,
                    SqlValue = "SELECT 1",
                },
            ],
        };

    private static QueryData Edit(string sql) =>
        new()
        {
            QueryId = QueryId,
            Name = "Orders",
            Steps =
            [
                new QueryStepData
                {
                    StepId = 11,
                    StepOrder = 1,
                    Name = "Step 1",
                    SqlValue = sql,
                    DataSourceId = DataSourceId,
                },
            ],
        };

    private static QueryService BuildService(Query stored, List<object> saved, IQueryVersionService versions) =>
        new(
            Factory(saved, stored),
            Mock.Of<Beacon.Core.HostData.IDataSourceConnectionResolver>(),
            Mock.Of<IManualQueryExecutionLogger>(),
            NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            versions,
            new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } },
            UserContext(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            Mock.Of<Beacon.Core.HostData.IHostDataSourceGuard>());

    private static IBeaconUserContext UserContext() =>
        Mock.Of<IBeaconUserContext>(x => x.UserId == "u1" && x.DisplayName == "Ana Admin");

    private static IDbContextFactory<BeaconContext> Factory(List<object> saved, Query? stored = null)
    {
        var warehouse = new DataSource
        {
            Id = DataSourceId,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "unused",
        };
        var sets = new Dictionary<Type, object>
        {
            [typeof(Query)] = RecordingBeaconContext.MemorySet(stored == null ? new List<Query>() : [stored], saved),
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet<DataSource>([warehouse], saved),
            [typeof(QueryVersion)] = RecordingBeaconContext.MemorySet(new List<QueryVersion>(), saved),
        };

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));
        return factory.Object;
    }
}
