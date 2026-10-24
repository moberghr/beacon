using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Api;

/// <summary>
/// Warns once, after the host has started, off the startup thread and best effort, when Beacon's PostgreSQL database lets
/// every role create objects in the <c>public</c> schema. Beacon's connections search <c>public</c> after their own schema
/// (a database that already has pgvector usually keeps it there), so a role that can create objects there can plant
/// functions and operators Beacon's own queries would run. PostgreSQL 15 and later closes that by default; an older
/// cluster, or one a DBA re-opened, is told to revoke it. A database that cannot be read, or another provider, is
/// ignored. Names only, no values (§1.11).
/// </summary>
internal sealed class PublicSchemaStartupWarning(ILogger<PublicSchemaStartupWarning> logger) : IStartupFilter
{
    // The PUBLIC pseudo-role: has_schema_privilege answers for every role that has no grant of its own.
    private const string PublicCanCreateQuery = "SELECT has_schema_privilege('public', 'public', 'CREATE') AS \"Value\"";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(
                () => _ = Task.Run(() => CheckAsync(builder.ApplicationServices, lifetime.ApplicationStopping)));

            next(builder);
        };
    }

    internal async Task CheckAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        if (contextFactory == null)
        {
            return;
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (context.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            var answer = await context.Database
                .SqlQueryRaw<bool>(PublicCanCreateQuery)
                .ToListAsync(cancellationToken);

            Report(answer.FirstOrDefault());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "The create privilege on the public schema of Beacon's database could not be checked ({ExceptionType})",
                ex.GetType().Name);
        }
    }

    internal void Report(bool publicCanCreate)
    {
        if (!publicCanCreate)
        {
            return;
        }

        logger.LogWarning(
            "Every role can create objects in the public schema of Beacon's database, and Beacon's connections search that schema after their own. Revoke it (REVOKE CREATE ON SCHEMA public FROM PUBLIC) unless every role that can connect is trusted with Beacon's session");
    }
}
