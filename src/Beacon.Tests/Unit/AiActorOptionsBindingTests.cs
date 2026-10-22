using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// <c>Beacon:AiActors:PlanExpiryDays</c> as <see cref="ServiceConfiguration.AddBeaconServices"/> binds and validates it.
/// </summary>
[TestFixture]
public class AiActorOptionsBindingTests
{
    [Test]
    public void PlanExpiryDays_IsBoundFromConfiguration()
    {
        using var provider = Services("3");

        provider.GetRequiredService<IOptionsMonitor<AiActorOptions>>().CurrentValue.PlanExpiryDays.Should().Be(3);
    }

    [Test]
    public void PlanExpiryDays_DefaultsToSevenDays()
    {
        using var provider = Services(null);

        provider.GetRequiredService<IOptionsMonitor<AiActorOptions>>().CurrentValue.PlanExpiryDays.Should().Be(7);
    }

    [TestCase("0")]
    [TestCase("366")]
    public void PlanExpiryDays_OutOfRange_IsRefused(string value)
    {
        using var provider = Services(value);

        var act = () => provider.GetRequiredService<IOptionsMonitor<AiActorOptions>>().CurrentValue;

        act.Should().Throw<OptionsValidationException>().WithMessage("*PlanExpiryDays*");
    }

    private static ServiceProvider Services(string? planExpiryDays)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var values = new Dictionary<string, string?>
        {
            ["Beacon:EncryptionKey"] = TestEncryptionKey.Value
        };
        if (planExpiryDays != null)
        {
            values[$"{AiActorOptions.SectionName}:{nameof(AiActorOptions.PlanExpiryDays)}"] = planExpiryDays;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

        return services.BuildServiceProvider();
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
