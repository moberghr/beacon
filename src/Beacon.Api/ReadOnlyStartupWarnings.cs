using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services.Providers;

namespace Beacon.Api;

/// <summary>
/// Warns once at startup about what keeps Beacon's SQL read-only in this host: a deployment that lets an Admin turn MCP
/// read-only enforcement or PII detection off at runtime (<c>Beacon:Mcp:ForceReadOnly</c> / <c>ForcePiiDetection</c> set to
/// false), and, after the host has started, off the startup thread and best effort, the stored MCP settings a deployment
/// lock overrides (<see cref="McpLockPolicy.Overridden"/>) and the data sources whose engine does not refuse writes in
/// Beacon's read-only session (<see cref="DataSourceReadOnlyEnforcement"/>) — not the migration write targets, which Beacon
/// never reads. A host that runs data migrations (<c>Beacon:Migrations:Enabled</c>) is noted too. Ids and configuration
/// names only (§1.11); a database that cannot be read is noted, never a failure.
/// </summary>
internal sealed class ReadOnlyStartupWarnings(
    IOptions<McpDeploymentOptions> deploymentOptions,
    ILogger<ReadOnlyStartupWarnings> logger,
    IOptions<MigrationOptions> migrationOptions) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var locks = deploymentOptions.Value;
        if (!locks.ForceReadOnly)
        {
            logger.LogWarning(
                "{Section}:{Lock} is off: an Admin can turn MCP read-only enforcement off at runtime, globally or per project",
                McpDeploymentOptions.SectionName,
                nameof(McpDeploymentOptions.ForceReadOnly));
        }

        if (!locks.ForcePiiDetection)
        {
            logger.LogWarning(
                "{Section}:{Lock} is off: an Admin can turn MCP PII detection off at runtime, globally or per project",
                McpDeploymentOptions.SectionName,
                nameof(McpDeploymentOptions.ForcePiiDetection));
        }

        if (migrationOptions.Value.Enabled)
        {
            logger.LogInformation(
                "{Section}:{Option} is on: migrations write to the data sources an Admin marks as migration write targets, which Beacon never reads",
                MigrationOptions.SectionName,
                nameof(MigrationOptions.Enabled));
        }

        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(
                () => _ = Task.Run(async () =>
                {
                    await WarnAboutOverriddenSettingsAsync(builder.ApplicationServices, lifetime.ApplicationStopping);
                    await WarnAboutDataSourcesAsync(builder.ApplicationServices, lifetime.ApplicationStopping);
                }));

            next(builder);
        };
    }

    /// <summary>
    /// One Warning naming the stored MCP settings rows a deployment lock overrides (global rows and projects by id, and
    /// the locks), so an Admin learns why a stored value has no effect; nothing when none does.
    /// </summary>
    internal static void WarnAboutOverriddenSettings(ILogger logger, McpLockOverrides overrides)
    {
        if (!overrides.Any)
        {
            return;
        }

        logger.LogWarning(
            "Stored MCP settings contradict the deployment locks {Locks} ({Section}) and are overridden: global settings rows {GlobalRowIds}, overrides of projects {ProjectIds}. They take effect again only if the host lifts the lock",
            overrides.LockNames,
            McpDeploymentOptions.SectionName,
            overrides.GlobalRowIds,
            overrides.ProjectIds);
    }

    private async Task WarnAboutOverriddenSettingsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        if (contextFactory == null || McpLockPolicy.ActiveRules(deploymentOptions.Value).Count == 0)
        {
            return;
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var globalRows = await context.McpSettings
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.EnforceReadOnly,
                        x.EnablePiiDetection,
                        x.RetainQueryContent
                    })
                .ToListAsync(cancellationToken);
            var projectRows = await context.McpProjectSettings
                .Select(x =>
                    new
                    {
                        x.ProjectId,
                        x.EnforceReadOnly,
                        x.EnablePiiDetection,
                        x.RetainQueryContent
                    })
                .ToListAsync(cancellationToken);

            var overrides = McpLockPolicy.Overridden(
                deploymentOptions.Value,
                globalRows.Select(x => (x.Id, new McpSettingsData
                {
                    EnforceReadOnly = x.EnforceReadOnly,
                    EnablePiiDetection = x.EnablePiiDetection,
                    RetainQueryContent = x.RetainQueryContent
                })),
                projectRows.Select(x => (x.ProjectId, new McpProjectSettingsData
                {
                    EnforceReadOnly = x.EnforceReadOnly,
                    EnablePiiDetection = x.EnablePiiDetection,
                    RetainQueryContent = x.RetainQueryContent
                })));
            WarnAboutOverriddenSettings(logger, overrides);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "The stored MCP settings a deployment lock overrides could not be listed ({ExceptionType})",
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// One Warning naming (by id) the data sources Beacon reads whose engine does not refuse writes in its read-only session;
    /// migration write targets are left out, as Beacon never reads them. Internal for unit tests.
    /// </summary>
    internal async Task WarnAboutDataSourcesAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        var providers = services.GetService<IDataSourceProviderFactory>();
        if (contextFactory == null || providers == null)
        {
            return;
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var dataSources = await context.DataSources
                .Where(x => !x.AllowMigrationWrites)
                .Select(x =>
                    new
                    {
                        x.Id,
                        x.DataSourceType,
                        x.DatabaseEngineType
                    })
                .ToListAsync(cancellationToken);

            var unenforced = dataSources
                .Where(x => DataSourceReadOnlyEnforcement.Of(providers, x.DataSourceType, x.DatabaseEngineType) == false)
                .Select(x => x.Id)
                .Order()
                .ToList();

            if (unenforced.Count > 0)
            {
                logger.LogWarning(
                    "Data sources {DataSourceIds} have no database-level read-only enforcement: {Guidance}",
                    unenforced,
                    DataSourceReadOnlyEnforcement.Guidance);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "The data sources without database-level read-only enforcement could not be listed ({ExceptionType})",
                ex.GetType().Name);
        }
    }
}
