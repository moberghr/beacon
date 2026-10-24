using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.AiActors;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The pending-plan count behind the navigation badge counts the plans waiting for the caller's decision, in the SQL it
/// sends: pending and not expired, of actors whose data source the caller may write; an Approver with the approval
/// workflow on (or an Admin) counts every such plan, anyone else only those of the actors they created.
/// </summary>
[TestFixture]
public class AiActorPendingPlanCountTests
{
    [Test]
    public async Task ACallerWhoIsNoApprover_CountsOnlyThePlansOfTheActorsTheyCreated()
    {
        var capture = await CountAsync(workflowOn: true, permissions: TestPermissions.None);

        CountCommand(capture).Should().Contain("created_by_user_id");
        capture.CommandParameters[^1].Values.Should().Contain("ext-ada");
    }

    [Test]
    public async Task AnApprover_WithTheWorkflowOn_CountsThePlansOfEveryActorTheyMayWrite()
    {
        var capture = await CountAsync(workflowOn: true, permissions: TestPermissions.Holding(BeaconPermissions.Approver));

        CountCommand(capture).Should().NotContain("created_by_user_id");
    }

    [Test]
    public async Task AnApprover_WithTheWorkflowOff_CountsOnlyThePlansOfTheActorsTheyCreated()
    {
        var capture = await CountAsync(workflowOn: false, permissions: TestPermissions.Holding(BeaconPermissions.Approver));

        CountCommand(capture).Should().Contain("created_by_user_id");
    }

    [Test]
    public async Task OnlyPendingPlansProposedWithinTheExpiryWindow_AreCounted()
    {
        var capture = await CountAsync(workflowOn: true, permissions: TestPermissions.None);

        CountCommand(capture).Should().Contain("status = 1");
        CountCommand(capture).Should().Contain("proposed_at > @");
    }

    [Test]
    public async Task GetPendingPlanCount_Translates()
    {
        var postgres = await new ListHandlerHarness(TestProjectAccess.FastPath).SendAsync(new GetPendingPlanCountQuery());
        var sqlServer = await new ListHandlerHarness(TestProjectAccess.FastPath, sqlServer: true).SendAsync(new GetPendingPlanCountQuery());

        postgres.Where(x => x.Contains("ai_actor_plans", StringComparison.Ordinal)).Should().ContainSingle().Which.Should().StartWith("SELECT count(*)::int");
        sqlServer.Where(x => x.Contains("[AiActorPlans]", StringComparison.Ordinal)).Should().ContainSingle().Which.Should().StartWith("SELECT COUNT(*)");
    }

    // The one command that counts plans; the commands before it resolve the caller.
    private static string CountCommand(SqlCapture capture)
    {
        capture.Commands.Where(x => x.Contains("ai_actor_plans", StringComparison.Ordinal)).Should().ContainSingle();

        return capture.Commands[^1];
    }

    // The count for the harness's caller (ext-ada, no Admin), with the given permissions and approval workflow.
    private static async Task<SqlCapture> CountAsync(bool workflowOn, IBeaconPermissionAccessor permissions)
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();

        var result = await new ListHandlerHarness(TestProjectAccess.FastPath).AnswerAsync(
            new GetPendingPlanCountQuery(),
            capture.Factory(),
            services =>
            {
                var configuration = (BeaconConfiguration)services.Last(x => x.ServiceType == typeof(BeaconConfiguration)).ImplementationInstance!;
                configuration.ApprovalWorkflow.Enabled = workflowOn;
                services.AddScoped(_ => permissions);
            });

        result.Should().BeOfType<GetPendingPlanCountResult>().Which.Count.Should().Be(1);

        return capture;
    }
}
