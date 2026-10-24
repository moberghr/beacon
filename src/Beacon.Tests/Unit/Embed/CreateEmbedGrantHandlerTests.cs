using System.Text.Json;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Embed;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.SavedQueryTools;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// Creating an embed grant: it pins the query's runnable approved version, normalizes the origins, splits every parameter
/// into fixed and locked, stores only the secret's hash and returns the secret once, and saves its event with ids and
/// counts only. It is refused while embedding is off, without a runnable version (strict mode included), for a version
/// reading a source the project does not link, when the grant could not render (it acts for its creator without the
/// Admin bypass), for the version's requester or approver while the approval workflow is on, and for a caller without a
/// stored user.
/// </summary>
[TestFixture]
public class CreateEmbedGrantHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task AGrant_PinsTheRunnableVersion_AndStoresOnlyTheSecretsHash()
    {
        var data = Seed();
        var query = data.Query(5, null, [Step(1, 10, "SELECT * FROM loans WHERE branch = {branch} AND since >= {since}", Parameter("branch", ParameterType.Number), Parameter("since", ParameterType.DateTime))]);
        var audit = new RecordingSecurityAudit(RecordingSecurityAudit.User(3, "ext-ada"));

        var result = await Handler(data, audit: audit).Handle(Command(fixedParameters: new Dictionary<string, string> { ["since"] = "2026-01-01" }, locked: ["branch"]), CancellationToken.None);

        var grant = data.Store.ListFor<EmbedGrant>().Should().ContainSingle().Subject;
        grant.QueryVersionId.Should().Be(query.ActiveVersionId!.Value);
        grant.ProjectId.Should().Be(1);
        grant.QueryId.Should().Be(5);
        grant.PublicId.Should().MatchRegex("^[0-9a-f]{32}$");
        grant.SecretHash.Should().Be(EmbedGrantSecret.Hash(result.Secret));
        JsonSerializer.Serialize(grant).Should().NotContain(result.Secret, "the secret itself is never stored");
        result.Secret.Should().StartWith("bge_");
        grant.AllowedOriginsJson.Should().Be("[\"https://portal.example.com\"]");
        grant.FixedParametersJson.Should().Be("{\"since\":\"2026-01-01\"}");
        grant.LockedParameterNamesJson.Should().Be("[\"branch\"]");
        grant.ExpiresTime.Should().Be(Now.UtcDateTime.AddDays(30));
        grant.CreatedByUserId.Should().Be("ext-ada");
        grant.RevokedTime.Should().BeNull();
        result.PublicId.Should().Be(grant.PublicId);
        result.VersionNumber.Should().Be(1);
        result.AllowedOrigins.Should().Equal("https://portal.example.com");
        result.LockedParameters.Should().Equal("branch");

        var created = audit.Single("portal.example.com", "2026-01-01", "Loan book", result.Secret);
        created.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.EmbedGrantCreated, ResourceType = SecurityAuditResources.EmbedGrant, ActorUserId = (int?)3 },
            x => x.ExcludingMissingMembers());
        created.DetailsJson.Should().Be($"{{\"queryId\":5,\"versionId\":{grant.QueryVersionId},\"originCount\":1,\"lockedParamCount\":1,\"expiresDays\":30}}");
        audit.Ways.Should().Equal("saved");
    }

    [Test]
    public async Task AVersionSupersededBetweenTheLookupAndTheSave_IsRefused_AndNoGrantIsSaved()
    {
        var data = Seed();
        var query = data.Query(5, null, [Step(1, 10, "SELECT 1")]);
        var inner = new RecordingSecurityAudit(new BeaconActor("ext-ada", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 3, IsActiveUser = true });
        var racing = new Mock<ISecurityAuditRecorder>();
        racing
            .Setup(x => x.SaveWithEventsAsync(It.IsAny<BeaconContext>(), It.IsAny<Func<Task<int>>>(), It.IsAny<Func<int, IEnumerable<SecurityAuditEntry>>>(), It.IsAny<CancellationToken>()))
            .Returns((BeaconContext context, Func<Task<int>> change, Func<int, IEnumerable<SecurityAuditEntry>> events, CancellationToken cancellationToken) =>
            {
                var next = data.AddVersion(query, 2, QueryVersionStatus.Active, [Step(1, 10, "SELECT 2")], approval: ApprovalStatus.Approved);
                query.ActiveVersionId = next.Id;
                query.ActiveVersion = next;

                return inner.SaveWithEventsAsync(context, change, events, cancellationToken);
            });

        var act = () => Handler(data, audit: racing.Object).Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed while the grant was created*");
        data.Store.ListFor<EmbedGrant>().Should().BeEmpty();
        inner.Events.Should().BeEmpty();
    }

    [TestCase(ApprovalStatus.Pending)]
    [TestCase(ApprovalStatus.Rejected)]
    [TestCase(null)]
    public async Task AQueryWithoutAnApprovedActiveVersion_IsRefused(ApprovalStatus? approval)
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT 1")], approval: approval);

        var act = () => Handler(data).Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no approved active version*");
        data.Store.ListFor<EmbedGrant>().Should().BeEmpty();
    }

    [Test]
    public async Task InStrictMode_AnApprovalWithoutTheVersionsHash_IsRefused()
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT 1")]);
        var strict = new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, RequireApprovedVersionToRun = true } };

        var act = () => Handler(data, configuration: strict).Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no approved active version*");
    }

    [Test]
    public async Task AVersionReadingASourceTheProjectDoesNotLink_IsRefused()
    {
        var data = Seed();
        data.DataSource(11, "ledger");
        data.Query(5, null, [Step(1, 10, "SELECT 1"), Step(2, 11, "SELECT 2")]);

        var act = () => Handler(data).Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not link every data source*");
    }

    // An Admin is no member of a restricted project: the grant acts without the Admin bypass, so it could render nothing.
    [Test]
    public async Task AnAdminWhoIsNoMemberOfARestrictedProject_IsRefused_AndAMemberIsNot()
    {
        var data = Seed(requiresMembership: true);
        data.Query(5, null, [Step(1, 10, "SELECT 1")]);
        var admin = new BeaconActor("ext-ada", true) { Kind = BeaconPrincipalKind.User, BeaconUserId = 3, IsActiveUser = true, IsLiveAdmin = true, RoleLevel = RoleService.RoleLevels.Admin };

        var act = () => Handler(data, caller: admin).Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*would render nothing*");
        data.Store.ListFor<ProjectMember>().Add(new ProjectMember { ProjectId = 1, UserId = 3, Role = ProjectRole.Viewer });
        await Handler(data, caller: admin).Handle(Command(), CancellationToken.None);
        data.Store.ListFor<EmbedGrant>().Should().ContainSingle();
    }

    [Test]
    public async Task ACallerWithoutAStoredUser_IsRefused()
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT 1")]);
        var key = new BeaconActor("ext-ada", false) { Kind = BeaconPrincipalKind.ApiKey, BeaconUserId = 3, IsActiveUser = true, ApiKeyId = 4 };

        var act = () => Handler(data, caller: key).Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
    }

    [TestCase("ext-ada", null)]
    [TestCase(null, "ext-ada")]
    public async Task TheRequesterOrApproverOfTheVersion_IsRefused_WhileTheWorkflowIsOn(string? requestedBy, string? reviewedBy)
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT 1")]);
        var approval = data.Store.ListFor<QueryApprovalRequest>().Single();
        approval.RequestedByUserId = requestedBy;
        approval.ReviewedByUserId = reviewedBy;

        var act = () => Handler(data).Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        var withoutWorkflow = await Handler(data, configuration: new BeaconConfiguration()).Handle(Command(), CancellationToken.None);
        withoutWorkflow.Secret.Should().NotBeNullOrEmpty("with the workflow off nobody approves a change");
    }

    [Test]
    public async Task WhileEmbeddingIsOff_NoGrantIsCreated()
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT 1")]);

        var act = () => Handler(data, options: new EmbedOptions()).Handle(Command(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not enabled*");
    }

    [TestCase(0)]
    [TestCase(91)]
    public async Task AnExpiryOutsideTheMaximum_IsRefused(int days)
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT 1")]);

        var act = () => Handler(data).Handle(Command() with { ExpiresInDays = days }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1 to 90 days*");
    }

    [Test]
    public async Task AnInvalidOrigin_OrTooManyOrigins_AreRefused()
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT 1")]);
        var options = new EmbedOptions { Enabled = true, MaxOrigins = 1 };

        var wildcard = () => Handler(data).Handle(Command() with { AllowedOrigins = ["https://*.example.com"] }, CancellationToken.None);
        var tooMany = () => Handler(data, options: options).Handle(Command() with { AllowedOrigins = ["https://a.example", "https://b.example"] }, CancellationToken.None);

        await wildcard.Should().ThrowAsync<InvalidOperationException>();
        await tooMany.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at most 1*");
    }

    [TestCase("{}", new string[0], "*not yet: branch*")]
    [TestCase("{\"region\":\"1\"}", new[] { "branch" }, "*no parameter 'region'*")]
    [TestCase("{}", new[] { "branch", "region" }, "*no parameter 'region'*")]
    [TestCase("{\"branch\":\"1\"}", new[] { "branch" }, "*named twice*")]
    [TestCase("{\"branch\":\"twelve\"}", new string[0], "*is not a number*")]
    public async Task EveryParameter_IsFixedOrLockedExactlyOnce_AndAFixedValueHasItsType(string fixedJson, string[] locked, string expected)
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT * FROM loans WHERE branch = {branch}", Parameter("branch", ParameterType.Number))]);
        var fixedParameters = JsonSerializer.Deserialize<Dictionary<string, string>>(fixedJson)!;

        var act = () => Handler(data).Handle(Command(fixedParameters, locked), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(expected);
        data.Store.ListFor<EmbedGrant>().Should().BeEmpty();
    }

    [Test]
    public async Task AnEmptyOrOverlongName_IsRefused()
    {
        var data = Seed();
        data.Query(5, null, [Step(1, 10, "SELECT 1")]);

        var empty = () => Handler(data).Handle(Command() with { Name = "  " }, CancellationToken.None);
        var overlong = () => Handler(data).Handle(Command() with { Name = new string('n', 101) }, CancellationToken.None);

        await empty.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1–100 characters*");
        await overlong.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1–100 characters*");
    }

    private static SavedQueryTestData Seed(bool requiresMembership = false)
    {
        var data = new SavedQueryTestData();
        var project = data.Project(1, "A", data.DataSource(10, "warehouse"));
        project.RequiresMembership = requiresMembership;

        return data;
    }

    private static CreateEmbedGrantCommand Command(IReadOnlyDictionary<string, string>? fixedParameters = null, IReadOnlyList<string>? locked = null) =>
        new()
        {
            ProjectId = 1,
            QueryId = 5,
            Name = " Loan book ",
            AllowedOrigins = ["https://Portal.Example.com:443"],
            ExpiresInDays = 30,
            FixedParameters = fixedParameters,
            LockedParameters = locked
        };

    private static CreateEmbedGrantHandler Handler(
        SavedQueryTestData data,
        BeaconActor? caller = null,
        BeaconConfiguration? configuration = null,
        EmbedOptions? options = null,
        ISecurityAuditRecorder? audit = null)
    {
        var actor = caller ?? new BeaconActor("ext-ada", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 3, IsActiveUser = true, RoleLevel = RoleService.RoleLevels.Editor };
        var factory = data.Store.Factory().Object;

        return new CreateEmbedGrantHandler(
            factory,
            TestProjectAccess.RealEvaluator(actor, factory),
            RecordingSecurityAudit.Accessor(actor),
            configuration ?? new BeaconConfiguration { ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true } },
            Options.Create(options ?? new EmbedOptions { Enabled = true }),
            audit ?? new RecordingSecurityAudit(actor),
            new FakeTimeProvider(Now),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<CreateEmbedGrantHandler>.Instance);
    }
}
