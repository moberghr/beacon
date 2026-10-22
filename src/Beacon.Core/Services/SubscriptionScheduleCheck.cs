using Beacon.Core.Configuration;
using Beacon.Core.Helpers;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Services;

/// <summary>
/// Whether a subscription's cron schedule may be stored: every path that creates a subscription or changes its schedule
/// checks it, an AI actor's included. A subscription stored before keeps its schedule.
/// </summary>
public interface ISubscriptionScheduleCheck
{
    /// <summary>
    /// Refuses (<see cref="InvalidOperationException"/>) a schedule that is not a five-field cron expression or that fires
    /// more often than <c>Beacon:Subscriptions:MinIntervalMinutes</c>.
    /// </summary>
    void EnsureAllowed(string? cronExpression);
}

/// <inheritdoc />
public sealed class SubscriptionScheduleCheck(IOptions<SubscriptionOptions> options, TimeProvider timeProvider) : ISubscriptionScheduleCheck
{
    /// <summary>The check with the default limits, for a caller built without dependency injection.</summary>
    public static SubscriptionScheduleCheck Default { get; } = new(Options.Create(new SubscriptionOptions()), TimeProvider.System);

    public void EnsureAllowed(string? cronExpression)
    {
        SubscriptionSchedule.EnsureAllowed(cronExpression, options.Value.MinIntervalMinutes, timeProvider.GetUtcNow().UtcDateTime);
    }
}
