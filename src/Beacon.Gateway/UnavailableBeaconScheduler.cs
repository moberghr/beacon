using Beacon.Core.Worker;

namespace Beacon.Gateway;

/// <summary>
/// The <see cref="IBeaconScheduler"/> Beacon requires, for a gateway that has nothing to schedule: subscriptions and
/// data-quality checks run over data sources, and a gateway has none. Every call refuses rather than doing nothing, so a
/// change that does schedule something fails where somebody sees it instead of never running.
/// </summary>
internal sealed class UnavailableBeaconScheduler : IBeaconScheduler
{
    public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => throw Unavailable();

    public Task Remove(int subscriptionId, string subscriptionName) => throw Unavailable();

    private static NotSupportedException Unavailable() =>
        new("Beacon as an MCP gateway schedules nothing: it has no data sources, so no subscriptions or data-quality checks.");
}
