using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.HostData;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Who sees that a data source's connection change waits for approval: while the workflow covers data-source connections,
/// an Approver sees every source's pending request, the person who submitted one sees their own, and everyone else sees
/// none, in the list and in the by-id read alike, on both providers. Without the workflow nothing is read.
/// </summary>
[TestFixture]
public class DataSourceApprovalVisibilityTests
{
    private const string Requester = "ext-ada";

    [TestCase(false)]
    [TestCase(true)]
    public async Task List_ForAnApprover_ReadsEverySourcesPendingConnectionRequest(bool sqlServer)
    {
        var capture = await ListAsync(sqlServer, TestPermissions.Holding(BeaconPermissions.Approver), TestChangeApprovals.Caller("ext-cid"));

        var sql = ApprovalSql(capture);
        sql.Should().Contain("changeapprovalrequests").And.Contain("approvedtargethash");
        sql.Should().MatchRegex(@"kind\]? = 4", "only data-source connection requests").And.MatchRegex(@"status\]? = 1", "only pending ones");
        sql.Should().Contain("'none'", "a source never approved awaits its first approval").And.Contain("desc", "the latest request");
        sql.Should().NotContain("requestedbyuserid", "an Approver reads every source's pending request");
        capture.CommandParameters.SelectMany(x => x.Values).Should().NotContain("ext-cid");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task List_ForTheRequester_ReadsOnlyTheirOwnPendingRequests(bool sqlServer)
    {
        var capture = await ListAsync(sqlServer, TestPermissions.Holding(BeaconPermissions.DataSourceAdmin), TestChangeApprovals.Caller(Requester));

        ApprovalSql(capture).Should().Contain("requestedbyuserid");
        capture.CommandParameters.SelectMany(x => x.Values).Should().Contain(Requester, "the request is shown only when the caller submitted it");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ById_ForTheRequester_ReadsOnlyTheirOwnPendingRequests_AndForAnApprover_Every(bool sqlServer)
    {
        var requester = await ByIdAsync(sqlServer, TestPermissions.None, TestChangeApprovals.Caller(Requester));
        var approver = await ByIdAsync(sqlServer, TestPermissions.Admin, TestChangeApprovals.Caller("ext-cid"));

        ApprovalSql(requester).Should().Contain("requestedbyuserid");
        requester.CommandParameters.SelectMany(x => x.Values).Should().Contain(Requester);
        ApprovalSql(approver).Should().NotContain("requestedbyuserid");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ForSomeoneWhoIsNeitherApproverNorSignedInUser_NoRequestIsRead(bool sqlServer)
    {
        var list = await ListAsync(sqlServer, TestPermissions.None, TestChangeApprovals.Caller(null));
        var byId = await ByIdAsync(sqlServer, TestPermissions.None, TestChangeApprovals.Caller(null));

        Flat(list.Commands).Should().NotContain("changeapprovalrequests", "a caller without an id is nobody's requester");
        Flat(byId.Commands).Should().NotContain("changeapprovalrequests");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WithoutTheWorkflowCoveringConnections_NoRequestIsRead_EvenForAnApprover(bool sqlServer)
    {
        var list = await ListAsync(sqlServer, TestPermissions.Admin, TestChangeApprovals.Caller(Requester), TestChangeApprovals.On());
        var byId = await ByIdAsync(sqlServer, TestPermissions.Admin, TestChangeApprovals.Caller(Requester), new BeaconConfiguration());

        Flat(list.Commands).Should().NotContain("changeapprovalrequests", "nothing waits for approval while the workflow does not review connections");
        Flat(byId.Commands).Should().NotContain("changeapprovalrequests");
    }

    // A source never approved is held whether or not a request waits (a rejected first approval leaves none): everyone who
    // reads it is told, while the workflow covers connections.
    [TestCase(false)]
    [TestCase(true)]
    public async Task TheHoldOfASourceNeverApproved_IsReadForEveryone_WhileConnectionsAreCovered(bool sqlServer)
    {
        var list = await ListAsync(sqlServer, TestPermissions.None, TestChangeApprovals.Caller(null));
        var byId = await ByIdAsync(sqlServer, TestPermissions.None, TestChangeApprovals.Caller(null));
        var uncovered = await ListAsync(sqlServer, TestPermissions.None, TestChangeApprovals.Caller(null), TestChangeApprovals.On());

        Flat(list.Commands).Should().Contain("approvedtargethash").And.Contain("'none'");
        Flat(byId.Commands).Should().Contain("approvedtargethash").And.Contain("'none'");
        Flat(uncovered.Commands).Should().NotContain("approvedtargethash", "nothing is held while the workflow does not review connections");
    }

    private static async Task<SqlCapture> ListAsync(
        bool sqlServer,
        IBeaconPermissionAccessor permissions,
        IBeaconActorAccessor actors,
        BeaconConfiguration? configuration = null)
    {
        var capture = Capture(sqlServer);
        var factory = capture.Factory();
        var handler = new GetDataSourcesHandler(
            factory,
            TestProjectAccess.Evaluator(TestProjectAccess.FastPath),
            Mock.Of<IDataSourceProviderFactory>(),
            TransportStatus(factory),
            permissions,
            actors,
            configuration ?? TestChangeApprovals.CoveringConnections());

        await handler.Handle(new GetDataSourcesQuery(), CancellationToken.None);

        return capture;
    }

    private static async Task<SqlCapture> ByIdAsync(
        bool sqlServer,
        IBeaconPermissionAccessor permissions,
        IBeaconActorAccessor actors,
        BeaconConfiguration? configuration = null)
    {
        var capture = Capture(sqlServer);
        var factory = capture.Factory();
        var handler = new GetDataSourceHandler(
            factory,
            TestProjectAccess.Evaluator(TestProjectAccess.FastPath),
            Mock.Of<IDataSourceProviderFactory>(),
            TransportStatus(factory),
            permissions,
            actors,
            configuration ?? TestChangeApprovals.CoveringConnections());

        var entry = await handler.Handle(new GetDataSourceQuery(10), CancellationToken.None);

        entry.Should().BeNull("the capture holds no row");

        return capture;
    }

    // The data-source read, which reads the pending request in a subquery of its own.
    private static string ApprovalSql(SqlCapture capture)
    {
        return capture.Commands
            .Select(x => Flat([x]))
            .Should()
            .ContainSingle(x => x.Contains("changeapprovalrequests"), "the pending request is read in the source's own query")
            .Subject;
    }

    // Npgsql names tables and columns in snake case, SQL Server in Pascal case: compare without either.
    private static string Flat(IEnumerable<string> commands)
    {
        return string.Join("\n", commands)
            .Replace("_", string.Empty)
            .ToLowerInvariant();
    }

    private static DataSourceTransportStatus TransportStatus(IDbContextFactory<BeaconContext> factory)
    {
        return new DataSourceTransportStatus(factory, Mock.Of<IDataSourceConnectionResolver>(), NullLogger<DataSourceTransportStatus>.Instance);
    }

    private static SqlCapture Capture(bool sqlServer)
    {
        return (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture()).AnsweringCountsWithOne();
    }
}
