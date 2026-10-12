using System.Security.Claims;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// <c>Beacon:Query</c> (the row cap and statement timeout of the statements Beacon runs for its own features) and
/// <c>Beacon:Migrations</c> (the deployment switch and a migration's row budget): the validators accept exactly the
/// documented range, and the values a host configures reach the services that enforce them through Beacon's own
/// registration — never a built-in default standing in for a missing one. No database (§4.7).
/// </summary>
[TestFixture]
public class ExecutionLimitOptionsTests
{
    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(10_000_000, true)]
    [TestCase(10_000_001, false)]
    public void MaxExecutionRows_IsAcceptedFromOneToTenMillion(int rows, bool valid)
    {
        var result = new QueryExecutionOptionsValidator().Validate(null, new QueryExecutionOptions { MaxExecutionRows = rows });

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.FailureMessage.Should().Contain("Beacon:Query:MaxExecutionRows");
        }
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    [TestCase(10_000_000, true)]
    [TestCase(10_000_001, false)]
    public void AMigrationsMaxRows_IsAcceptedFromOneToTenMillion(int rows, bool valid)
    {
        var result = new MigrationOptionsValidator().Validate(null, new MigrationOptions { MaxRows = rows });

        result.Succeeded.Should().Be(valid);
        if (!valid)
        {
            result.FailureMessage.Should().Contain("Beacon:Migrations:MaxRows");
        }
    }

    [Test]
    public void TheDefaults_AreAHundredThousandRows_TwoMinutes_AndMigrationsOff()
    {
        var query = new QueryExecutionOptions();
        var migrations = new MigrationOptions();

        query.MaxExecutionRows.Should().Be(100_000);
        query.StatementTimeoutSeconds.Should().Be(120);
        migrations.Enabled.Should().BeFalse();
        migrations.MaxRows.Should().Be(100_000);
    }

    [Test]
    public async Task TheConfiguredLimits_ReachTheServicesThatEnforceThem()
    {
        await using var provider = Build(new Dictionary<string, string?>
        {
            ["Beacon:Query:MaxExecutionRows"] = "42",
            ["Beacon:Query:StatementTimeoutSeconds"] = "7",
            ["Beacon:Migrations:Enabled"] = "true",
            ["Beacon:Migrations:MaxRows"] = "99"
        });
        await using var scope = provider.CreateAsyncScope();

        var queries = (QueryService)scope.ServiceProvider.GetRequiredService<IQueryService>();
        var runner = scope.ServiceProvider.GetRequiredService<GatedSqlRunner>();
        var migrations = scope.ServiceProvider.GetRequiredService<IOptions<MigrationOptions>>().Value;

        queries.ExecutionRowCeiling.Should().Be(42);
        queries.FinalQueryTimeoutSeconds.Should().Be(7);
        runner.StatementTimeout.Should().Be(TimeSpan.FromSeconds(7));
        migrations.Enabled.Should().BeTrue();
        migrations.MaxRows.Should().Be(99);
    }

    [Test]
    public async Task ALimitOutOfRange_FailsWhenTheServicesAreBuilt()
    {
        await using var provider = Build(new Dictionary<string, string?> { ["Beacon:Query:MaxExecutionRows"] = "0" });
        await using var scope = provider.CreateAsyncScope();

        var act = () => scope.ServiceProvider.GetRequiredService<IQueryService>();

        act.Should().Throw<OptionsValidationException>().WithMessage("*Beacon:Query:MaxExecutionRows*");
    }

    // Beacon's own registration, as a host calls it, over an unused context factory.
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
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada")], "Cookies")) }
        });
        services.AddScoped<IBeaconUserContext, HttpContextUserContext>();
        services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        services.AddScoped(_ => TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted));

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
