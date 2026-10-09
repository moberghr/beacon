using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Data.Enums;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class AiActorActionGuardTests
{
    private static AiActorActionPlan Plan(string type, params (string Key, object? Value)[] parameters)
    {
        var plan = new AiActorActionPlan
        {
            ActionType = type,
            Reasoning = "because"
        };

        foreach (var (key, value) in parameters)
        {
            plan.Parameters[key] = value;
        }

        return plan;
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public void ShouldExecute_FollowsRequiresApproval(bool requiresApproval, bool expected)
    {
        var actor = new Beacon.Core.Data.Entities.AiActor { RequiresApproval = requiresApproval };

        AiActorActionGuard.ShouldExecute(actor).Should().Be(expected);
    }

    [Test]
    public void ToProposed_CreateQuery_MapsNameAndSql()
    {
        var action = AiActorActionGuard.ToProposed(Plan("CREATE_QUERY", ("name", "Failed payments"), ("sql", "SELECT 1")));

        action.ActionType.Should().Be(AiActorActionType.CreateQuery);
        action.QueryName.Should().Be("Failed payments");
        action.SqlQuery.Should().Be("SELECT 1");
        action.Reasoning.Should().Be("because");
        action.Proposed.Should().BeTrue();
        action.Success.Should().BeFalse();
    }

    [Test]
    public void ToProposed_CreateSubscription_MapsQueryAndCron()
    {
        var action = AiActorActionGuard.ToProposed(
            Plan("CREATE_SUBSCRIPTION", ("queryId", 7), ("queryName", "Q"), ("cronExpression", "0 * * * *")));

        action.ActionType.Should().Be(AiActorActionType.CreateSubscription);
        action.SubscriptionQueryId.Should().Be(7);
        action.QueryName.Should().Be("Q");
        action.CronExpression.Should().Be("0 * * * *");
        action.Proposed.Should().BeTrue();
        action.Success.Should().BeFalse();
    }

    [Test]
    public void ToProposed_RefineQuery_MapsTargetAndSql()
    {
        var action = AiActorActionGuard.ToProposed(Plan("REFINE_QUERY", ("queryId", 3), ("newSql", "SELECT 2")));

        action.ActionType.Should().Be(AiActorActionType.RefineQuery);
        action.TargetQueryId.Should().Be(3);
        action.SqlQuery.Should().Be("SELECT 2");
        action.Proposed.Should().BeTrue();
        action.Success.Should().BeFalse();
    }

    [Test]
    public void ToProposed_ArchiveQuery_MapsTarget()
    {
        var action = AiActorActionGuard.ToProposed(Plan("archive_query", ("queryId", "9")));

        action.ActionType.Should().Be(AiActorActionType.ArchiveQuery);
        action.TargetQueryId.Should().Be(9);
        action.Proposed.Should().BeTrue();
        action.Success.Should().BeFalse();
    }

    [Test]
    public void ToProposed_ArchiveSubscription_MapsTarget()
    {
        var action = AiActorActionGuard.ToProposed(Plan("ARCHIVE_SUBSCRIPTION", ("subscriptionId", 11)));

        action.ActionType.Should().Be(AiActorActionType.ArchiveSubscription);
        action.TargetSubscriptionId.Should().Be(11);
        action.Proposed.Should().BeTrue();
        action.Success.Should().BeFalse();
    }

    [Test]
    public void ToProposed_UnknownType_IsNotProposedAndReportsError()
    {
        var action = AiActorActionGuard.ToProposed(Plan("DROP_EVERYTHING"));

        action.Proposed.Should().BeFalse();
        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().Contain("DROP_EVERYTHING");
    }

    // The plan is deserialized LLM output, so "actionType": null reaches the guard despite the non-nullable property.
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ToProposed_MissingActionType_IsAFailedUnknownAction(string? type)
    {
        var action = AiActorActionGuard.ToProposed(Plan(type!, ("queryId", 1)));

        action.Proposed.Should().BeFalse();
        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().Be("Unknown action type");
        action.Reasoning.Should().Be("because");
    }

    [Test]
    public void ToProposed_NullParameters_IsAFailedUnknownAction()
    {
        var plan = Plan("ARCHIVE_QUERY");
        plan.Parameters = null!;

        var action = AiActorActionGuard.ToProposed(plan);

        action.Proposed.Should().BeFalse();
        action.Success.Should().BeFalse();
        action.ErrorMessage.Should().Be("Unknown action type");
        action.TargetQueryId.Should().BeNull();
    }

    [Test]
    public void CheckOwnership_DifferentActor_ReturnsError()
    {
        AiActorActionGuard.CheckOwnership(1, 2, false).Should().NotBeNull();
    }

    [Test]
    public void CheckOwnership_NullOwner_ReturnsError()
    {
        AiActorActionGuard.CheckOwnership(1, null, false).Should().NotBeNull();
    }

    [Test]
    public void CheckOwnership_Locked_ReturnsError()
    {
        AiActorActionGuard.CheckOwnership(1, 1, true).Should().Contain("Locked");
    }

    [Test]
    public void CheckOwnership_OwnedAndUnlocked_ReturnsNull()
    {
        AiActorActionGuard.CheckOwnership(1, 1, false).Should().BeNull();
    }

    [TestCase(9, null)]
    [TestCase(10, "limit")]
    [TestCase(11, "limit")]
    public void CheckQueryCap_EnforcesMaximum(int owned, string? expectedFragment)
    {
        var result = AiActorActionGuard.CheckQueryCap(owned, 10);

        if (expectedFragment == null)
        {
            result.Should().BeNull();
            return;
        }

        result.Should().Contain(expectedFragment);
    }

    [TestCase(2, null)]
    [TestCase(3, "limit")]
    [TestCase(4, "limit")]
    public void CheckSubscriptionCap_EnforcesMaximum(int existing, string? expectedFragment)
    {
        var result = AiActorActionGuard.CheckSubscriptionCap(existing, 3);

        if (expectedFragment == null)
        {
            result.Should().BeNull();
            return;
        }

        result.Should().Contain(expectedFragment);
    }
}
