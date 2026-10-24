using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Beacon.Core.Helpers;
using Beacon.Core.SecurityAudit;

namespace Beacon.MCP.Services;

/// <summary>
/// Runs the MCP audit seal's startup checks (<see cref="McpAuditSealingStartup"/>: the interceptor that seals, the seal
/// columns, the sealing-started event, a verification that is not scheduled) once the host has started, off the startup
/// thread and best effort: a database that cannot be read is logged at Warning and never stops the host.
/// </summary>
internal sealed class McpAuditSealingStartupCheck : IStartupFilter
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
            var startup = scope.ServiceProvider.GetService<McpAuditSealingStartup>();
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
                .CreateLogger<McpAuditSealingStartupCheck>()
                .LogWarning("The MCP audit seal's startup checks did not run ({Failure})", FailureCodes.Describe(ex));
        }
    }
}
