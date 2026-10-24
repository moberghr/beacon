using Beacon.Core.Helpers;
using Beacon.Core.SecurityAudit;

namespace Beacon.Api;

/// <summary>
/// Runs the startup checks of the sealed audit tables beside the MCP audit (<see cref="AuditTableSealingStartup"/>: the
/// execution log and the settings history; their seal columns, and each table's sealing-started event) once the host has
/// started, off the startup thread and best effort: a database that cannot be read is logged at Warning and never stops the
/// host. Registered by Beacon.Api, so every host that serves Beacon records when sealing started, with or without MCP.
/// </summary>
internal sealed class AuditTableSealingStartupCheck : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(
                () => _ = Task.Run(() => RunAsync(builder.ApplicationServices, lifetime.ApplicationStopping)));

            next(builder);
        };
    }

    internal static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var startup = scope.ServiceProvider.GetService<AuditTableSealingStartup>();
            if (startup == null)
            {
                return;
            }

            await startup.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            services.GetService<ILoggerFactory>()?
                .CreateLogger<AuditTableSealingStartupCheck>()
                .LogWarning("The audit table seal's startup checks did not run ({Failure})", FailureCodes.Describe(ex));
        }
    }
}
