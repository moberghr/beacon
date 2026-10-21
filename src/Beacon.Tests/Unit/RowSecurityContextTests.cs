using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.RowSecurity;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Row-level security's context on every gate request (§1.5): the gate refuses a statement its context denies or filters,
/// whatever the other settings say, and runs it as written otherwise. The lock <c>Beacon:RowSecurity:Enabled</c> is off by
/// default, so no statement is filtered, and a host that turns it on fails at startup while row security cannot be
/// enforced. No database (§4.7).
/// </summary>
[TestFixture]
public class RowSecurityContextTests
{
    private const string Sql = "SELECT id, total FROM orders";

    private static SqlGateRequest Request(RowSecurityContext rowSecurity) =>
        SqlGateRequest.FromSettings(Sql, "PostgreSQL", TestSqlGate.DefaultSettings(), rowSecurity) with { MaxRows = 10 };

    [Test]
    public void WithNoRowFilter_TheStatementRunsAsTheOtherStagesWriteIt()
    {
        var report = TestSqlGate.Create().Evaluate(Request(TestRowSecurity.NotApplicable));

        report.Blocked.Should().BeFalse();
        report.Verdicts.Schema.Status.Should().NotBe(SqlGateStatus.Fail);
        report.Verdicts.RowLimit.Should().Be(SqlGateVerdict.Passed);
        report.FinalSql.Should().Contain("LIMIT 10");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ADeniedCaller_IsRefused_AndNoLaterStageRuns(bool enforceReadOnly)
    {
        var gate = TestSqlGate.Create(deployment: TestSqlGate.Unlocked);

        var report = gate.Evaluate(Request(RowSecurityContext.Denied("test")) with { EnforceReadOnly = enforceReadOnly });

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.NoEntitlement);
        report.BlockReason.Should().Be(report.Verdicts.Schema.Message);
        report.Verdicts.Lint.Code.Should().Be(SqlGateCodes.NotEvaluated);
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.NotEvaluated);
        report.FinalSql.Should().Be(Sql);
    }

    [Test]
    public void AFilteredCaller_IsRefused_WhileTheGateCannotWriteTheFilterIntoTheStatement()
    {
        var report = TestSqlGate.Create().Evaluate(Request(RowSecurityContext.Filtered("test")));

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.RowSecurityUnrewritable);
        report.Verdicts.RowLimit.Code.Should().Be(SqlGateCodes.NotEvaluated);
        report.FinalSql.Should().Be(Sql);
    }

    [Test]
    public void ARequestWithoutAContext_IsRefusedAsDenied()
    {
        var report = TestSqlGate.Create().Evaluate(Request(TestRowSecurity.NotApplicable) with { RowSecurity = null! });

        report.Blocked.Should().BeTrue();
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.NoEntitlement);
    }

    [Test]
    public void AStatementTheReadOnlyStageRefuses_IsRefusedForThatFirst()
    {
        var report = TestSqlGate.Create().Evaluate(SqlGateRequest.FromSettings(
            "DELETE FROM orders",
            "PostgreSQL",
            TestSqlGate.DefaultSettings(),
            RowSecurityContext.Denied("test")));

        report.Blocked.Should().BeTrue();
        report.Verdicts.ReadOnly.Status.Should().Be(SqlGateStatus.Fail);
        report.Verdicts.Schema.Code.Should().Be(SqlGateCodes.NotEvaluated);
    }

    [Test]
    public void TheInMemoryJoin_HasNoRowFilter()
    {
        var request = SqlGateRequest.ForInMemoryJoin("SELECT * FROM step_1", TestSqlGate.DefaultSettings());

        request.RowSecurity.State.Should().Be(RowSecurityState.NotApplicable);
        request.RowSecurity.Reason.Should().Be("in_memory_join");
        TestSqlGate.Create().Evaluate(request).Blocked.Should().BeFalse();
    }

    [Test]
    public async Task WithTheLockOff_TheResolverAppliesNoRowFilter()
    {
        var resolver = new RowSecurityResolver(Options.Create(new RowSecurityOptions()));

        var context = await resolver.ResolveAsync(7, CancellationToken.None);

        context.State.Should().Be(RowSecurityState.NotApplicable);
        context.Reason.Should().Be("row_security_disabled");
    }

    [Test]
    public async Task WithTheLockOn_TheResolverDeniesEveryStatement()
    {
        var resolver = new RowSecurityResolver(Options.Create(new RowSecurityOptions { Enabled = true }));

        var context = await resolver.ResolveAsync(7, CancellationToken.None);

        context.State.Should().Be(RowSecurityState.Denied);
        context.Reason.Should().Be("row_security_unavailable");
    }

    [Test]
    public void TheLock_IsOffByDefault_AndRefusedWhenTurnedOn()
    {
        var validator = new RowSecurityOptionsValidator();

        new RowSecurityOptions().Enabled.Should().BeFalse();
        validator.Validate(null, new RowSecurityOptions()).Succeeded.Should().BeTrue();
        validator.Validate(null, new RowSecurityOptions { Enabled = true }).FailureMessage.Should().Contain("Beacon:RowSecurity:Enabled");
    }

    [Test]
    public async Task BeaconsOwnRegistration_StartsWithTheLockOff_AndAppliesNoRowFilter()
    {
        await using var provider = Build(new Dictionary<string, string?>());
        await using var scope = provider.CreateAsyncScope();

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();
        var context = await scope.ServiceProvider.GetRequiredService<IRowSecurityResolver>().ResolveAsync(1, CancellationToken.None);

        startup.Should().NotThrow();
        context.State.Should().Be(RowSecurityState.NotApplicable);
    }

    [Test]
    public async Task BeaconsOwnRegistration_FailsAtStartup_WithTheLockOn()
    {
        await using var provider = Build(new Dictionary<string, string?> { ["Beacon:RowSecurity:Enabled"] = "true" });

        var startup = () => provider.GetRequiredService<IStartupValidator>().Validate();

        startup.Should().Throw<OptionsValidationException>().WithMessage("*Beacon:RowSecurity:Enabled*");
    }

    // Beacon's own registration, as a host calls it.
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        settings["Beacon:EncryptionKey"] = TestEncryptionKey.Value;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging(x => x.ClearProviders());
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
