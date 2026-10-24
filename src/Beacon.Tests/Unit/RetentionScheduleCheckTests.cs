using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Core.Configuration;
using Beacon.Core.Services.Retention;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

[TestFixture]
public class RetentionScheduleCheckTests
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public void ExpectedJobs_IncludeAPurgeOnlyWhileItsRetentionIsSet()
    {
        RetentionScheduleCheck.ExpectedJobs(null, null)
            .Select(x => x.Job)
            .Should().BeEquivalentTo(MaintenanceJobNames.SecurityAuditVerify);

        RetentionScheduleCheck.ExpectedJobs(90, 400)
            .Select(x => x.Job)
            .Should().BeEquivalentTo(MaintenanceJobNames.McpAuditRetention, MaintenanceJobNames.SecurityAuditRetention, MaintenanceJobNames.SecurityAuditVerify);
    }

    [Test]
    public void Findings_NameAJobWithNoRunAndAJobPastItsWindow_AndNotAFreshOne()
    {
        var expected = RetentionScheduleCheck.ExpectedJobs(90, 400);
        var lastRuns = new Dictionary<string, DateTime>
        {
            [MaintenanceJobNames.McpAuditRetention] = Now.AddHours(-30),
            [MaintenanceJobNames.SecurityAuditVerify] = Now.AddHours(-7)
        };

        var findings = RetentionScheduleCheck.Findings(expected, lastRuns, Now);

        findings
            .Select(x => x.Job)
            .Should().BeEquivalentTo(MaintenanceJobNames.SecurityAuditRetention, MaintenanceJobNames.SecurityAuditVerify);
        findings.Single(x => x.Job == MaintenanceJobNames.SecurityAuditRetention).LastRunTime.Should().BeNull();
        findings.Single(x => x.Job == MaintenanceJobNames.SecurityAuditVerify).LastRunTime.Should().Be(Now.AddHours(-7));
    }

    [Test]
    public void Findings_AreEmptyWhenEveryExpectedJobRanWithinItsWindow()
    {
        var lastRuns = new Dictionary<string, DateTime>
        {
            [MaintenanceJobNames.McpAuditRetention] = Now.AddHours(-20),
            [MaintenanceJobNames.SecurityAuditRetention] = Now.AddHours(-35),
            [MaintenanceJobNames.SecurityAuditVerify] = Now.AddHours(-1)
        };

        RetentionScheduleCheck.Findings(RetentionScheduleCheck.ExpectedJobs(90, 400), lastRuns, Now).Should().BeEmpty();
    }

    [Test]
    public void TheStartupCheck_WarnsPerFinding_AndNeverStopsTheHostWhenTheLookupFails()
    {
        var logs = new LogRecorder();
        var findings = new List<RetentionScheduleFinding>
        {
            new(MaintenanceJobNames.McpAuditRetention, null, TimeSpan.FromHours(36)),
            new(MaintenanceJobNames.SecurityAuditVerify, Now, TimeSpan.FromHours(6))
        };
        var check = new Mock<IRetentionScheduleCheck>();
        check
            .Setup(x => x.CheckAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(findings);
        new RetentionScheduleStartupCheck(Scopes(check.Object), logs.For<RetentionScheduleStartupCheck>()).Check();

        logs.Entries.Where(x => x.Level == LogLevel.Warning).Should().HaveCount(2);
        logs.Entries.Should().Contain(x => x.Message.Contains(MaintenanceJobNames.McpAuditRetention) && x.Message.Contains("no recorded run"));

        var failing = new LogRecorder();
        var broken = new Mock<IRetentionScheduleCheck>();
        broken
            .Setup(x => x.CheckAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no table"));
        new RetentionScheduleStartupCheck(Scopes(broken.Object), failing.For<RetentionScheduleStartupCheck>()).Check();

        failing.Entries.Should().ContainSingle(x => x.Level == LogLevel.Information);
        failing.Entries.Should().NotContain(x => x.Message.Contains("no table"));
    }

    [TestCase(false, 1)]
    [TestCase(true, 0)]
    public void TheMcpAuditStartupWarning_WarnsOnlyWhileAuditIsNotRequired(bool required, int warnings)
    {
        var logs = new LogRecorder();
        var options = Options.Create(new McpDeploymentOptions { Audit = new McpAuditOptions { Required = required } });

        new McpAuditStartupWarning(options, logs.For<McpAuditStartupWarning>()).Configure(x => { });

        logs.Entries.Count(x => x.Level == LogLevel.Warning && x.Message.Contains("Beacon:Mcp:Audit:Required")).Should().Be(warnings);
    }

    private static IServiceScopeFactory Scopes(IRetentionScheduleCheck check)
    {
        var services = new ServiceCollection();
        services.AddSingleton(check);

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}
