using Microsoft.Extensions.Options;
using Beacon.Core.Services.Retention;

namespace Beacon.Api;

/// <summary>
/// At startup, warns about each retention or verification job that should be running (a purge with a retention set, the
/// security audit verification) and has no recorded run within its window: Beacon schedules none of them itself, so a host
/// that never scheduled them, or whose scheduler stopped, would otherwise see nothing. A first start has no record yet, and
/// the warning says so. The look-up is bounded and best effort (the schema may not exist yet, §host migrations): a failure
/// is logged at Information and never stops the host. Job names and times only (§1.11).
/// </summary>
internal sealed class RetentionScheduleStartupCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<RetentionScheduleStartupCheck> logger) : IStartupFilter
{
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(10);

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            Check();
            next(builder);
        };
    }

    internal void Check()
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var check = scope.ServiceProvider.GetService<IRetentionScheduleCheck>();
            if (check == null)
            {
                return;
            }

            using var timeout = new CancellationTokenSource(LookupTimeout);
            var findings = check.CheckAsync(timeout.Token).GetAwaiter().GetResult();
            foreach (var finding in findings)
            {
                if (finding.LastRunTime == null)
                {
                    logger.LogWarning(
                        "Maintenance job {Job} has no recorded run (expected at least every {ExpectedHours} hours). Beacon does not schedule it: the host must (the sample host does with a Warp recurring job). Ignore this on a first start.",
                        finding.Job,
                        (int)finding.ExpectedWithin.TotalHours);
                }
                else
                {
                    logger.LogWarning(
                        "Maintenance job {Job} last ran at {LastRunTime:O}, longer ago than the {ExpectedHours} hours it is expected within: its schedule may be missing or stopped.",
                        finding.Job,
                        finding.LastRunTime,
                        (int)finding.ExpectedWithin.TotalHours);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogInformation("The maintenance job schedule check did not run ({Failure})", Beacon.Core.Helpers.FailureCodes.Describe(ex));
        }
    }
}
