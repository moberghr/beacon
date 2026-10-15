using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.DataQuality.SetDataContractOwner;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using ActorEntity = Beacon.Core.Data.Entities.AiActor;
using Beacon.Core.SecurityAudit;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// An Admin makes a user the owner scheduled work runs as: a subscription's owner must be able to run its query (Run),
/// a data contract's owner to run its rules (Run on its source), an AI actor's creator to work ad hoc with its source
/// (Query). An owner who could not would only have every run skipped, so the change is refused (409) and nothing is
/// written. The real handlers, authorizer, evaluator and actor accessor run over in-memory rows (no database): project 1
/// is open (data source 10), project 2 restricted (data source 20); every resource reads data source 20; user 5 is an
/// Editor, user 6 a Viewer, user 7 disabled.
/// </summary>
[TestFixture]
public class ScheduledWorkOwnerTransferTests
{
    private const int RestrictedProject = 2;
    private const int RestrictedSource = 20;
    private const int EditorId = 5;
    private const int ViewerId = 6;
    private const int DisabledId = 7;
    private const int SubscriptionId = 100;
    private const int ContractId = 200;
    private const int ActorId = 300;

    private ProjectAccessWorld _world = null!;
    private List<Subscription> _subscriptions = null!;
    private List<DataContract> _contracts = null!;
    private List<ActorEntity> _actors = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Project(1);
        _world.Project(RestrictedProject, requiresMembership: true);
        _world.Source(10, 1);
        var source = _world.Source(RestrictedSource, RestrictedProject);
        // A stored Admin: owner changes are decided by the roles held now, not the session's claims.
        _world.User(1, "ext-admin", RoleService.RoleLevels.Admin).UserRoles[0].Role.Name = RoleService.RoleNames.Admin;
        _world.User(EditorId, "ext-editor");
        _world.User(ViewerId, "ext-viewer", RoleService.RoleLevels.Viewer);
        _world.User(DisabledId, "ext-gone").IsEnabled = false;
        var query = new Query
        {
            Id = 1000,
            Name = "Late payments",
            Steps = [new QueryStep { QueryId = 1000, StepOrder = 1, DataSourceId = RestrictedSource, DataSource = source, SqlValue = "SELECT 1" }]
        };
        _subscriptions = [new Subscription { Id = SubscriptionId, QueryId = query.Id, Query = query, CronExpression = "0 * * * *" }];
        _contracts =
        [
            new DataContract
            {
                Id = ContractId,
                DataSourceId = RestrictedSource,
                DataSource = source,
                SchemaName = "sales",
                TableName = "orders",
                Name = "Orders are complete",
                CronExpression = "0 * * * *"
            }
        ];
        _actors = [new ActorEntity { Id = ActorId, Name = "Watcher", Instructions = "Watch.", DataSourceId = RestrictedSource, DataSource = source }];
        _world.Serve(_subscriptions);
        _world.Serve(_contracts);
        _world.Serve(_actors);
    }

    [Test]
    public async Task ASubscriptionsNewOwner_WhoMayNotRunItsQuery_IsRefused()
    {
        var act = () => SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, EditorId));

        (await act.Should().ThrowAsync<BeaconConflictException>()).WithMessage(SetSubscriptionOwnerHandler.OwnerMayNotRunMessage);
        _subscriptions[0].OwnerUserId.Should().BeNull();
    }

    [Test]
    public async Task ASubscriptionsNewOwner_WhoMayRunItsQuery_BecomesItsOwner()
    {
        // A Viewer member runs saved SQL in the project: enough to own the subscription's runs.
        _world.Member(RestrictedProject, ViewerId, ProjectRole.Viewer);

        await SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, ViewerId));

        _subscriptions[0].OwnerUserId.Should().Be("ext-viewer");
    }

    [Test]
    public async Task ASubscriptionsOwner_IsAnEnabledUser()
    {
        _world.Member(RestrictedProject, DisabledId, ProjectRole.Editor);

        var act = () => SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, DisabledId));

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*existing, enabled user*");
        _subscriptions[0].OwnerUserId.Should().BeNull();
    }

    [Test]
    public async Task ASubscriptionsOwner_IsNotChangedByAnEditor()
    {
        _world.Member(RestrictedProject, ViewerId, ProjectRole.Viewer);

        var act = () => SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, ViewerId), admin: false);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        _subscriptions[0].OwnerUserId.Should().BeNull();
    }

    [Test]
    public async Task AUserAdmin_ChangesOwners_AndTheNewOwnersAccessIsStillValidated()
    {
        _world.FunctionalUser(20, "ext-useradmin", RoleService.RoleLevels.Viewer, BeaconPermissions.UserAdmin);
        _world.Member(RestrictedProject, ViewerId, ProjectRole.Viewer);

        await SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, ViewerId), externalId: "ext-useradmin");
        var unfit = () => SendAsync(new SetDataContractOwnerCommand(ContractId, EditorId), externalId: "ext-useradmin");

        _subscriptions[0].OwnerUserId.Should().Be("ext-viewer");
        await unfit.Should().ThrowAsync<BeaconConflictException>("the new owner must be able to evaluate the contract");
    }

    [Test]
    public async Task AMissingSubscription_IsNotFound()
    {
        var act = () => SendAsync(new SetSubscriptionOwnerCommand(999, EditorId));

        await act.Should().ThrowAsync<BeaconNotFoundException>().WithMessage("Subscription 999 not found.");
    }

    [Test]
    public async Task AMissingContract_IsNotFound()
    {
        var act = () => SendAsync(new SetDataContractOwnerCommand(999, EditorId));

        await act.Should().ThrowAsync<BeaconNotFoundException>().WithMessage("Data contract 999 not found");
    }

    [Test]
    public async Task AContractsNewOwner_MustBeAbleToRunItsRules()
    {
        var refused = () => SendAsync(new SetDataContractOwnerCommand(ContractId, EditorId));
        (await refused.Should().ThrowAsync<BeaconConflictException>()).WithMessage(SetDataContractOwnerHandler.OwnerMayNotRunMessage);
        _contracts[0].OwnerUserId.Should().BeNull();

        _world.Member(RestrictedProject, EditorId, ProjectRole.Viewer);
        await SendAsync(new SetDataContractOwnerCommand(ContractId, EditorId));

        _contracts[0].OwnerUserId.Should().Be("ext-editor");
    }

    [Test]
    public async Task AnAiActorsNewCreator_MustBeAbleToWorkAdHocWithItsSource()
    {
        // A Viewer member may run saved SQL, not work ad hoc: an AI actor's cycles need Query.
        _world.Member(RestrictedProject, EditorId, ProjectRole.Viewer);
        var refused = () => SendAsync(new SetAiActorOwnerCommand(ActorId, EditorId));
        (await refused.Should().ThrowAsync<BeaconConflictException>()).WithMessage(SetAiActorOwnerHandler.OwnerMayNotQueryMessage);
        _actors[0].CreatedByUserId.Should().BeNull();

        _world.Members.Clear();
        _world.Member(RestrictedProject, EditorId, ProjectRole.Editor);
        await SendAsync(new SetAiActorOwnerCommand(ActorId, EditorId));

        _actors[0].CreatedByUserId.Should().Be("ext-editor");
    }

    [Test]
    public async Task AUserAdmin_MakesNoOneButThemselvesTheOwner_WhileAnAdminMayNameThemselves()
    {
        _world.FunctionalUser(8, "ext-users", RoleService.RoleLevels.Editor, BeaconPermissions.UserAdmin);
        _world.Member(RestrictedProject, 8, ProjectRole.Editor);
        _world.Member(RestrictedProject, EditorId, ProjectRole.Editor);

        var self = () => SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, 8), admin: false, externalId: "ext-users");
        var selfContract = () => SendAsync(new SetDataContractOwnerCommand(ContractId, 8), admin: false, externalId: "ext-users");
        var selfActor = () => SendAsync(new SetAiActorOwnerCommand(ActorId, 8), admin: false, externalId: "ext-users");

        await self.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(OwnerTransfer.SelfMessage);
        await selfContract.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(OwnerTransfer.SelfMessage);
        await selfActor.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(OwnerTransfer.SelfMessage);
        _subscriptions[0].OwnerUserId.Should().BeNull();

        await SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, EditorId), admin: false, externalId: "ext-users");
        _subscriptions[0].OwnerUserId.Should().Be("ext-editor", "a UserAdmin names another user who may run the work");

        await SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, 1));
        _subscriptions[0].OwnerUserId.Should().Be("ext-admin", "an Admin may name themselves");
    }

    [Test]
    public async Task EachOwnerChange_IsRecordedOnce_NamingTheOwnersByUsersId_AndTheSameOwnerAgainRecordsNothing()
    {
        _world.Member(RestrictedProject, EditorId, ProjectRole.Editor);
        _subscriptions[0].OwnerUserId = "ext-viewer";

        await SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, EditorId));
        await SendAsync(new SetDataContractOwnerCommand(ContractId, EditorId));
        await SendAsync(new SetAiActorOwnerCommand(ActorId, EditorId));
        await SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, EditorId));

        var changes = _world.Audit.Events;
        changes.Should().HaveCount(3, "the last change named the owner the subscription already had");
        changes.Should().OnlyContain(x => x.Action == SecurityAuditActions.OwnerChanged && x.ActorUserId == 1);
        changes.Select(x => (x.ResourceType, x.ResourceId, x.DetailsJson)).Should().Equal(
            (SecurityAuditResources.Subscription, SubscriptionId.ToString(), "{\"ownerUserId\":5,\"previousOwnerUserId\":6}"),
            (SecurityAuditResources.DataContract, ContractId.ToString(), "{\"ownerUserId\":5,\"previousOwnerUserId\":null}"),
            (SecurityAuditResources.AiActor, ActorId.ToString(), "{\"ownerUserId\":5,\"previousOwnerUserId\":null}"));
        foreach (var change in changes)
        {
            SecurityAuditTestKit.AssertNoPii(change, "ext-editor", "ext-viewer", "ext-admin");
        }

        _world.Audit.Ways.Should().Equal("saved", "saved", "saved");
        _subscriptions[0].ModifiedByUserId.Should().Be("ext-admin");
        _subscriptions[0].ModifiedTime.Should().NotBeNull();
        _contracts[0].ModifiedByUserId.Should().Be("ext-admin");
    }

    [Test]
    public async Task ARefusedOwnerChange_RecordsNothing()
    {
        var act = () => SendAsync(new SetSubscriptionOwnerCommand(SubscriptionId, EditorId));

        await act.Should().ThrowAsync<BeaconConflictException>();
        _world.Audit.Events.Should().BeEmpty();
    }

    [Test]
    public async Task AnOwnerChange_ByAnAuditor_IsForbidden()
    {
        _world.FunctionalUser(8, "ext-audit", RoleService.RoleLevels.Editor, BeaconPermissions.Auditor);

        var act = () => SendAsync(new SetAiActorOwnerCommand(ActorId, EditorId), admin: false, externalId: "ext-audit");

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("Only an Admin or a UserAdmin*");
    }

    private async Task SendAsync(IRequest request, bool admin = true, string? externalId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, externalId ?? (admin ? "ext-admin" : "ext-editor")) };
        if (admin && externalId == null)
        {
            claims.Add(new Claim(ClaimTypes.Role, RoleService.RoleNames.Admin));
        }

        var services = new ServiceCollection();
        _world.Register(services);
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies")) }
        });
        services.AddScoped<IProjectResourceResolver, ProjectResourceResolver>();
        services.AddScoped<ProjectRequestAuthorizer>();
        services.AddScoped<SetSubscriptionOwnerHandler>();
        services.AddScoped<SetDataContractOwnerHandler>();
        services.AddScoped<SetAiActorOwnerHandler>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var sp = scope.ServiceProvider;

        switch (request)
        {
            case SetSubscriptionOwnerCommand x:
                await sp.GetRequiredService<SetSubscriptionOwnerHandler>().Handle(x, CancellationToken.None);
                break;
            case SetDataContractOwnerCommand x:
                await sp.GetRequiredService<SetDataContractOwnerHandler>().Handle(x, CancellationToken.None);
                break;
            case SetAiActorOwnerCommand x:
                await sp.GetRequiredService<SetAiActorOwnerHandler>().Handle(x, CancellationToken.None);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }
}
