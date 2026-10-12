using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;

namespace Beacon.Api;

/// <summary>
/// Warns once at startup about what keeps Beacon's SQL read-only in this host: a deployment that lets an Admin turn MCP
/// read-only enforcement or PII detection off at runtime (<c>Beacon:Mcp:ForceReadOnly</c> / <c>ForcePiiDetection</c> set to
/// false), and, after the host has started, off the startup thread and best effort, the stored MCP settings a deployment
/// lock overrides (<see cref="McpLockPolicy.Overridden"/>) and the data sources whose engine does not refuse writes in
/// Beacon's read-only session (<see cref="DataSourceReadOnlyEnforcement"/>) — not the migration write targets, which Beacon
/// never reads — and, under the row-data lock, the projects whose stored documentation holds sections that can list values
/// discovered in the data. A host that runs data migrations (<c>Beacon:Migrations:Enabled</c>) is noted too, and so are the
/// row-data settings in effect while no global MCP settings row is stored. Ids and configuration names only (§1.11); a
/// database that cannot be read is noted, never a failure.
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
                    await LogRowDataDefaultsAsync(builder.ApplicationServices, lifetime.ApplicationStopping);
                    await WarnAboutDataSourcesAsync(builder.ApplicationServices, lifetime.ApplicationStopping);
                    await WarnAboutStoredDocumentationAsync(builder.ApplicationServices, lifetime.ApplicationStopping);
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

    // Reads the stored values of every locked field, the row-data fields included. Internal for unit tests.
    internal async Task WarnAboutOverriddenSettingsAsync(IServiceProvider services, CancellationToken cancellationToken)
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
                        x.RetainQueryContent,
                        x.EnableSampleValueCollection,
                        x.EnableValueGrounding,
                        x.EnableEvalJudge
                    })
                .ToListAsync(cancellationToken);
            var projectRows = await context.McpProjectSettings
                .Select(x =>
                    new
                    {
                        x.ProjectId,
                        x.EnforceReadOnly,
                        x.EnablePiiDetection,
                        x.RetainQueryContent,
                        x.EnableSampleValueCollection,
                        x.EnableValueGrounding,
                        x.EnableEvalJudge
                    })
                .ToListAsync(cancellationToken);

            var overrides = McpLockPolicy.Overridden(
                deploymentOptions.Value,
                globalRows.Select(x => (x.Id, new McpSettingsData
                {
                    EnforceReadOnly = x.EnforceReadOnly,
                    EnablePiiDetection = x.EnablePiiDetection,
                    RetainQueryContent = x.RetainQueryContent,
                    EnableSampleValueCollection = x.EnableSampleValueCollection,
                    EnableValueGrounding = x.EnableValueGrounding,
                    EnableEvalJudge = x.EnableEvalJudge
                })),
                projectRows.Select(x => (x.ProjectId, new McpProjectSettingsData
                {
                    EnforceReadOnly = x.EnforceReadOnly,
                    EnablePiiDetection = x.EnablePiiDetection,
                    RetainQueryContent = x.RetainQueryContent,
                    EnableSampleValueCollection = x.EnableSampleValueCollection,
                    EnableValueGrounding = x.EnableValueGrounding,
                    EnableEvalJudge = x.EnableEvalJudge
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
    /// While no global MCP settings row is stored, one Information record of the settings in effect that send values read
    /// from the data to the LLM provider — sample-value collection, value grounding and the eval judge, off by default and
    /// pinned off by <c>Beacon:Mcp:ForbidRowDataToLlm</c> — so an operator learns they are off and where to turn them on.
    /// Nothing once a global row is stored. Internal for unit tests.
    /// </summary>
    internal async Task LogRowDataDefaultsAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        if (contextFactory == null)
        {
            return;
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (await context.McpSettings.AnyAsync(cancellationToken))
            {
                return;
            }

            var effective = McpSettingsProvider.Resolve(new McpSettingsData(), null, deploymentOptions.Value).Effective;
            if (deploymentOptions.Value.ForbidRowDataToLlm)
            {
                logger.LogInformation(
                    "No global MCP settings are stored, and {Section}:{Lock} pins sample-value collection, value grounding and the eval judge off: no value read from the data is sent to the LLM provider",
                    McpDeploymentOptions.SectionName,
                    nameof(McpDeploymentOptions.ForbidRowDataToLlm));

                return;
            }

            logger.LogInformation(
                "No global MCP settings are stored, so the defaults apply: sample-value collection {SampleValueCollection}, value grounding {ValueGrounding}, eval judge {EvalJudge}. Each sends values read from the data to the LLM provider while on; turn it on for every project in the global MCP settings, or for one project in its overrides",
                OnOff(effective.EnableSampleValueCollection),
                OnOff(effective.EnableValueGrounding),
                OnOff(effective.EnableEvalJudge));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "The stored global MCP settings could not be read ({Failure})",
                FailureCodes.Describe(ex));
        }
    }

    /// <summary>
    /// Under the row-data lock (<c>Beacon:Mcp:ForbidRowDataToLlm</c>), one Warning naming (by id) the projects whose stored
    /// documentation holds sections that can list values discovered in the data (<see cref="ProjectDocSectionTypes.ListingValues"/>):
    /// those sections are no longer sent to the LLM provider, but the values discovered before the lock stay stored until the
    /// documentation is regenerated. Nothing without the lock or such sections. Internal for unit tests.
    /// </summary>
    internal async Task WarnAboutStoredDocumentationAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var contextFactory = services.GetService<IDbContextFactory<BeaconContext>>();
        if (contextFactory == null || !deploymentOptions.Value.ForbidRowDataToLlm)
        {
            return;
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var listingValues = ProjectDocSectionTypes.ListingValues.ToList();
            var projectIds = await context.ProjectDocumentationSections
                .Where(x => listingValues.Contains(x.SectionType))
                .Select(x => x.Documentation.ProjectId)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync(cancellationToken);

            if (projectIds.Count > 0)
            {
                logger.LogWarning(
                    "{Section}:{Lock} is on: the stored documentation of projects {ProjectIds} has business domain, data model or glossary sections, which can list values discovered in the data. They are no longer sent to the LLM provider; regenerate the documentation so values discovered before the lock are no longer stored",
                    McpDeploymentOptions.SectionName,
                    nameof(McpDeploymentOptions.ForbidRowDataToLlm),
                    projectIds);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(
                "The projects whose stored documentation can list discovered values could not be listed ({Failure})",
                FailureCodes.Describe(ex));
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

    private static string OnOff(bool value)
    {
        return value ? "on" : "off";
    }
}
