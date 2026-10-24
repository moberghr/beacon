using Beacon.Core;
using Beacon.Core.Data.Enums;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// Which changes the approval workflow covers: nothing while it is off; with it on, migration jobs, Custom SQL rules and
/// recipient destinations by default, and data-source connections and the LLM connection only when their own switches are
/// turned on. A staged LLM connection change waits 7 days by default, held between 1 and 30.
/// </summary>
[TestFixture]
public class ApprovalWorkflowOptionsCoverageTests
{
    [Test]
    public void TheConnectionSwitches_AreOffByDefault_EvenWithTheWorkflowOn()
    {
        var workflow = new ApprovalWorkflowOptions { Enabled = true };

        workflow.CoversDataSourceConnections.Should().BeFalse();
        workflow.CoversLlmConnection.Should().BeFalse();
        workflow.Covers(ChangeApprovalKind.DataSourceConnection).Should().BeFalse();
        workflow.Covers(ChangeApprovalKind.LlmConnection).Should().BeFalse();
        workflow.Covers(ChangeApprovalKind.MigrationJob).Should().BeTrue();
        workflow.Covers(ChangeApprovalKind.CustomSqlRules).Should().BeTrue();
        workflow.Covers(ChangeApprovalKind.RecipientDestination).Should().BeTrue();
    }

    [TestCase(ChangeApprovalKind.DataSourceConnection)]
    [TestCase(ChangeApprovalKind.LlmConnection)]
    public void EachConnectionSwitch_CoversItsOwnKindOnly_AndOnlyWithTheWorkflowOn(ChangeApprovalKind kind)
    {
        var dataSources = kind == ChangeApprovalKind.DataSourceConnection;
        var on = new ApprovalWorkflowOptions
        {
            Enabled = true,
            CoversDataSourceConnections = dataSources,
            CoversLlmConnection = !dataSources
        };
        var off = new ApprovalWorkflowOptions
        {
            Enabled = false,
            CoversDataSourceConnections = true,
            CoversLlmConnection = true
        };

        on.Covers(kind).Should().BeTrue();
        on.Covers(dataSources ? ChangeApprovalKind.LlmConnection : ChangeApprovalKind.DataSourceConnection).Should().BeFalse();
        off.Covers(kind).Should().BeFalse("nothing is covered while the workflow is off");
    }

    [TestCase(null, 7)]
    [TestCase(1, 1)]
    [TestCase(30, 30)]
    [TestCase(0, 1)]
    [TestCase(-5, 1)]
    [TestCase(31, 30)]
    [TestCase(365, 30)]
    public void TheLlmRequestDays_DefaultToSeven_AndAreHeldBetweenOneAndThirty(int? days, int expected)
    {
        var workflow = new ApprovalWorkflowOptions();
        if (days != null)
        {
            workflow.LlmConnectionRequestDays = days.Value;
        }

        workflow.LlmConnectionRequestDays.Should().Be(expected);
    }

    [Test]
    public void TheSwitches_BindFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApprovalWorkflow:Enabled"] = "true",
                ["ApprovalWorkflow:CoversDataSourceConnections"] = "true",
                ["ApprovalWorkflow:CoversLlmConnection"] = "true",
                ["ApprovalWorkflow:LlmConnectionRequestDays"] = "90"
            })
            .Build();

        var workflow = configuration.GetSection("ApprovalWorkflow").Get<ApprovalWorkflowOptions>()!;

        workflow.Covers(ChangeApprovalKind.DataSourceConnection).Should().BeTrue();
        workflow.Covers(ChangeApprovalKind.LlmConnection).Should().BeTrue();
        workflow.LlmConnectionRequestDays.Should().Be(30);
    }
}
