using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.RowSecurity;
using Beacon.Core.SavedQueries;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// Rendering an embed grant runs its pinned version through the saved-query executor alone: the real execution gate, the
/// read-only provider path with the grant's fixed and the token's locked values as database parameters, the row security
/// the resolver states, PII always masked and the smaller of the project's and the embed row cap. A caller other than the
/// grant's token, a superseded or revoked grant, a source the project no longer links, parameters that do not match and
/// every failed run are the one generic refusal, and each render is recorded with ids only.
/// </summary>
[TestFixture]
public class RenderEmbedQueryHandlerTests
{
    private EmbedTestKit _kit = null!;
    private Mock<IDataSourceProvider> _provider = null!;
    private List<(string Sql, Dictionary<string, object?> Parameters)> _executed = null!;
    private List<Dictionary<string, object?>> _rows = null!;
    private RecordingSqlGate _gate = null!;
    private RecordingSecurityAudit _audit = null!;

    [SetUp]
    public void SetUp()
    {
        _kit = new EmbedTestKit();
        _executed = [];
        _rows = [Row(1, "ana@example.com"), Row(2, "bo@example.com")];
        _provider = new Mock<IDataSourceProvider>();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DataSource _, string sql, Dictionary<string, object?> parameters, CancellationToken _) =>
            {
                _executed.Add((sql, parameters));

                return new ProviderQueryResult { Success = true, Rows = _rows, TotalRows = _rows.Count };
            });
    }

    [Test]
    public async Task ARender_RunsThroughTheGate_WithTheFixedAndLockedValuesAsParameters()
    {
        var (grant, _) = _kit.Grant();

        var result = await Handler(grant).Handle(Command(grant, branch: "7"), CancellationToken.None);

        var (sql, parameters) = _executed.Should().ContainSingle().Subject;
        sql.Should().Contain("@p0").And.Contain("@p1").And.NotContain("EU").And.NotContain("{branch}");
        parameters.Should().BeEquivalentTo(new Dictionary<string, object?> { ["p0"] = 7L, ["p1"] = "EU" });
        _gate.Evaluations.Should().ContainSingle().Which.Request.EnforceReadOnly.Should().BeTrue();
        result.Title.Should().Be("Loan book");
        result.Columns.Should().Equal("id", "email");
        result.Rows.Should().HaveCount(2);
    }

    [Test]
    public async Task Rows_AreAlwaysPiiMasked_WithTheProjectsDetectionOff()
    {
        var (grant, _) = _kit.Grant();

        var result = await Handler(grant, new McpSettingsData { EnablePiiDetection = false }).Handle(Command(grant), CancellationToken.None);

        result.Rows.Select(x => x["email"]).Should().NotContain(["ana@example.com", "bo@example.com"]);
        result.Rows.Select(x => x["id"]).Should().Equal(1, 2);
    }

    [TestCase(50, 3, 3)]
    [TestCase(2, 1000, 2)]
    public async Task TheRowCap_IsTheSmallerOfTheProjectsAndTheEmbedLimit(int projectLimit, int embedLimit, int expected)
    {
        var (grant, _) = _kit.Grant();
        _kit.Options.MaxRows = embedLimit;
        _rows = Enumerable.Range(1, 5).Select(x => Row(x, $"u{x}@example.com")).ToList();

        var result = await Handler(grant, new McpSettingsData { MaxRowLimit = projectLimit }).Handle(Command(grant), CancellationToken.None);

        result.MaxRows.Should().Be(expected);
        result.Rows.Should().HaveCount(expected);
        result.Truncated.Should().BeTrue();
        _gate.Evaluations.Single().Request.MaxRows.Should().Be(expected + 1);
    }

    [TestCase(RowSecurityState.Denied)]
    [TestCase(RowSecurityState.Filtered)]
    public async Task ARowSecurityContextTheGateCannotServe_RefusesTheRender_AndNoSqlRuns(RowSecurityState state)
    {
        var (grant, _) = _kit.Grant();
        var resolver = new Mock<IRowSecurityResolver>();
        resolver
            .Setup(x => x.ResolveAsync(EmbedTestKit.DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(state == RowSecurityState.Denied ? RowSecurityContext.Denied("no_entitlement") : RowSecurityContext.Filtered("tenant"));

        var act = () => Handler(grant, rowSecurity: resolver.Object).Handle(Command(grant), CancellationToken.None);

        (await act.Should().ThrowAsync<EmbedUnavailableException>()).Which.Message.Should().Be(EmbedUnavailableException.GenericMessage);
        _executed.Should().BeEmpty();
        _audit.Of(SecurityAuditActions.EmbedQueryRendered).Should().ContainSingle().Which.Outcome.Should().Be(SecurityAuditOutcomes.Failure);
    }

    [Test]
    public async Task AFailedRun_IsTheGenericRefusal_WithoutTheDatabasesError()
    {
        var (grant, _) = _kit.Grant();
        _provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProviderQueryResult { Success = false, ErrorMessage = "relation \"loans\" does not exist" });

        var act = () => Handler(grant).Handle(Command(grant), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<EmbedUnavailableException>();
        thrown.Which.Message.Should().Be(EmbedUnavailableException.GenericMessage).And.NotContain("loans");
        thrown.Which.Reason.Should().Be(RenderEmbedQueryHandler.RenderFailed);
    }

    [Test]
    public async Task ASupersededVersion_IsRefused_AndNoSqlRuns()
    {
        var (grant, _) = _kit.Grant();
        _kit.Supersede();

        var act = () => Handler(grant).Handle(Command(grant), CancellationToken.None);

        (await act.Should().ThrowAsync<EmbedUnavailableException>()).Which.Reason.Should().Be(EmbedRefusalCodes.Superseded);
        _executed.Should().BeEmpty();
    }

    [Test]
    public async Task ARevokedGrant_IsRefused_AndNoSqlRuns()
    {
        var (grant, _) = _kit.Grant();
        grant.RevokedTime = EmbedTestKit.Now.UtcDateTime;

        var act = () => Handler(grant).Handle(Command(grant), CancellationToken.None);

        (await act.Should().ThrowAsync<EmbedUnavailableException>()).Which.Reason.Should().Be(EmbedRefusalCodes.Revoked);
        _executed.Should().BeEmpty();
    }

    [Test]
    public async Task ASourceUnlinkedAfterTheGrantWasCreated_IsRefused_AndNoSqlRuns()
    {
        var (grant, _) = _kit.Grant();
        _kit.Data.Store.ListFor<ProjectDataSource>().RemoveAll(x => x.DataSourceId == EmbedTestKit.DataSourceId);

        var act = () => Handler(grant).Handle(Command(grant), CancellationToken.None);

        (await act.Should().ThrowAsync<EmbedUnavailableException>()).Which.Reason.Should().Be(RenderEmbedQueryHandler.RenderFailed);
        _executed.Should().BeEmpty();
    }

    [Test]
    public async Task ACreatorWhoLostTheirProjectAccess_RendersNothing()
    {
        var (grant, _) = _kit.Grant();
        _kit.Data.Store.ListFor<Project>().Single().RequiresMembership = true;

        var act = () => Handler(grant).Handle(Command(grant), CancellationToken.None);

        await act.Should().ThrowAsync<EmbedUnavailableException>();
        _executed.Should().BeEmpty("the executor re-checks that the grant's creator may still run the project's queries");
    }

    [Test]
    public async Task ADisabledDataSource_IsRefused_AndNoSqlRuns()
    {
        var (grant, _) = _kit.Grant();
        _kit.Data.Store.ListFor<DataSource>().Single().DisabledTime = EmbedTestKit.Now.UtcDateTime;

        var act = () => Handler(grant).Handle(Command(grant), CancellationToken.None);

        await act.Should().ThrowAsync<EmbedUnavailableException>();
        _executed.Should().BeEmpty();
    }

    [TestCase("{}")]
    [TestCase("{\"branch\":\"7\",\"extra\":\"1\"}")]
    [TestCase("{\"branch\":\"7\",\"region\":\"US\"}")]
    [TestCase("{\"branch\":\"seven\"}")]
    public async Task LockedValuesThatDoNotMatchTheGrant_AreRefused_AndNoSqlRuns(string lockedJson)
    {
        var (grant, _) = _kit.Grant();
        var locked = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(lockedJson)!;

        var act = () => Handler(grant).Handle(new RenderEmbedQueryCommand(EmbedTestKit.ProjectId, grant.Id, locked), CancellationToken.None);

        (await act.Should().ThrowAsync<EmbedUnavailableException>()).Which.Reason.Should().Be(RenderEmbedQueryHandler.ParametersRefused);
        _executed.Should().BeEmpty();
    }

    [Test]
    public async Task ACallerOtherThanTheGrantsToken_IsRefused()
    {
        var (grant, _) = _kit.Grant();
        var (other, _) = _kit.Grant();
        var user = new BeaconActor(EmbedTestKit.CreatorExternalId, true) { Kind = BeaconPrincipalKind.User, BeaconUserId = EmbedTestKit.CreatorId, IsActiveUser = true };

        var asUser = () => Handler(grant, caller: user).Handle(Command(grant), CancellationToken.None);
        var asSecret = () => Handler(grant, caller: _kit.Actor(grant, EmbedCredential.GrantSecret)).Handle(Command(grant), CancellationToken.None);
        var asOtherGrant = () => Handler(grant, caller: _kit.Actor(other, EmbedCredential.Token)).Handle(Command(grant), CancellationToken.None);

        await asUser.Should().ThrowAsync<EmbedUnavailableException>();
        await asSecret.Should().ThrowAsync<EmbedUnavailableException>();
        await asOtherGrant.Should().ThrowAsync<EmbedUnavailableException>();
        _executed.Should().BeEmpty();
    }

    [Test]
    public async Task ARender_IsRecorded_WithIdsAndCountsOnly()
    {
        var (grant, _) = _kit.Grant();

        await Handler(grant).Handle(Command(grant, branch: "42"), CancellationToken.None);

        var rendered = _audit.Single("ana@example.com", "EU", "42", "Loan book", "loans");
        rendered.Action.Should().Be(SecurityAuditActions.EmbedQueryRendered);
        rendered.ResourceType.Should().Be(SecurityAuditResources.EmbedGrant);
        rendered.ProjectId.Should().Be(EmbedTestKit.ProjectId);
        rendered.DetailsJson.Should().Be($"{{\"queryId\":{EmbedTestKit.QueryId},\"versionId\":{grant.QueryVersionId},\"rowCount\":2,\"truncated\":false}}");
    }

    [Test]
    public async Task WhileEveryRenderSlotIsTaken_ARenderIsRefusedAtOnce_AndRecordedAsBusy()
    {
        var (grant, _) = _kit.Grant();
        var (other, _) = _kit.Grant();
        _kit.Options.MaxConcurrentRenders = 1;
        var limiter = new EmbedRenderLimiter(_kit.EmbedOptions());
        using var held = limiter.TryAcquire(other.Id);

        var act = () => Handler(grant, limiter: limiter).Handle(Command(grant), CancellationToken.None);

        await act.Should().ThrowAsync<EmbedBusyException>();
        _executed.Should().BeEmpty();
        var busy = _audit.Of(SecurityAuditActions.EmbedQueryRendered).Should().ContainSingle().Subject;
        busy.Outcome.Should().Be(SecurityAuditOutcomes.Failure);
        busy.DetailsJson.Should().Contain("\"reason\":\"busy\"");
    }

    [Test]
    public async Task BusyRenders_AreAudited_OnlyWithinTheEmbedAuditCap()
    {
        var (grant, _) = _kit.Grant();
        _kit.Options.MaxConcurrentRenders = 1;
        var limiter = new EmbedRenderLimiter(_kit.EmbedOptions());
        using var held = limiter.TryAcquire(grant.Id);
        var handler = Handler(grant, limiter: limiter, auditLimiter: new EmbedAuditLimiter(1));

        for (var i = 0; i < 3; i++)
        {
            var act = () => handler.Handle(Command(grant), CancellationToken.None);
            await act.Should().ThrowAsync<EmbedBusyException>();
        }

        _audit.Of(SecurityAuditActions.EmbedQueryRendered).Should().ContainSingle("past the cap a busy render is logged, not recorded");
    }

    [Test]
    public async Task AGrantAtItsOwnConcurrencyCap_IsBusy_WhileAnotherGrantStillRenders()
    {
        var (grant, _) = _kit.Grant();
        var (other, _) = _kit.Grant();
        _kit.Options.MaxConcurrentRenders = 8;
        var limiter = new EmbedRenderLimiter(_kit.EmbedOptions());
        using var first = limiter.TryAcquire(grant.Id);
        using var second = limiter.TryAcquire(grant.Id);

        var busy = () => Handler(grant, limiter: limiter).Handle(Command(grant), CancellationToken.None);

        await busy.Should().ThrowAsync<EmbedBusyException>("a quarter of the slots is one grant's share");
        (await Handler(other, limiter: limiter).Handle(Command(other), CancellationToken.None)).Rows.Should().HaveCount(2);
    }

    [Test]
    public async Task ARenderWhoseExecutorThrows_IsRecordedAsFailed_AndRethrown()
    {
        var (grant, _) = _kit.Grant();
        var limiter = new EmbedRenderLimiter(_kit.EmbedOptions());

        var act = () => Handler(grant, limiter: limiter, runner: new ThrowingRunner()).Handle(Command(grant), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        var failed = _audit.Of(SecurityAuditActions.EmbedQueryRendered).Should().ContainSingle().Subject;
        failed.Outcome.Should().Be(SecurityAuditOutcomes.Failure);
        failed.DetailsJson.Should().Contain("\"reason\":\"render_failed\"").And.NotContain("driver failure");
        limiter.InFlight(grant.Id).Should().Be(0, "the slot is released");
    }

    private RenderEmbedQueryCommand Command(EmbedGrant grant, string branch = "7") =>
        new(EmbedTestKit.ProjectId, grant.Id, new Dictionary<string, string> { ["branch"] = branch });

    private RenderEmbedQueryHandler Handler(
        EmbedGrant grant,
        McpSettingsData? settings = null,
        IRowSecurityResolver? rowSecurity = null,
        BeaconActor? caller = null,
        EmbedRenderLimiter? limiter = null,
        ISavedQueryRunner? runner = null,
        EmbedAuditLimiter? auditLimiter = null)
    {
        var actor = caller ?? _kit.Actor(grant, EmbedCredential.Token);
        var factory = _kit.Data.Store.Factory().Object;
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(DataSourceType.Database))
            .Returns(_provider.Object);
        _gate = new RecordingSqlGate(TestSqlGate.Create());
        _audit = new RecordingSecurityAudit(actor);

        var executor = new SavedQueryToolExecutor(
            factory,
            providers.Object,
            _gate,
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            SettingsProviderMock.Unshared(SettingsProviderMock.Create(settings ?? new McpSettingsData()).Object),
            TestProjectAccess.RealEvaluator(actor, factory),
            rowSecurity ?? TestRowSecurity.Resolver,
            TestQueryCostLimit.Passing,
            Options.Create(new QueryExecutionOptions()),
            NullLoggerFactory.Instance);

        return new RenderEmbedQueryHandler(
            RecordingSecurityAudit.Accessor(actor),
            _kit.Resolver(),
            runner ?? executor,
            limiter ?? new EmbedRenderLimiter(_kit.EmbedOptions()),
            auditLimiter ?? new EmbedAuditLimiter(),
            _kit.EmbedOptions(),
            _audit,
            NullLogger<RenderEmbedQueryHandler>.Instance);
    }

    private static Dictionary<string, object?> Row(int id, string email) =>
        new() { ["id"] = id, ["email"] = email };

    private sealed class ThrowingRunner : ISavedQueryRunner
    {
        public Task<SavedQueryToolExecution> ExecuteAsync(
            SavedQueryToolDefinition tool,
            int projectId,
            IReadOnlyDictionary<string, object?> arguments,
            SavedQueryRunOptions options,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("driver failure");
        }
    }
}
