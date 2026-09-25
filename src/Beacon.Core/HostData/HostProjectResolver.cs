using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.HostData;

/// <summary>
/// The one place that turns a host-declared project name (<c>ExposeDbContext</c>, <c>ExposeDocs</c>,
/// <c>AddHostEndpointTools</c>) into a project, so every host subsystem lands on the same row. The host project is
/// found by <see cref="Project.HostManagedKey"/> (<see cref="KeyFor"/>), never by <see cref="Project.Name"/>, which is
/// neither unique nor protected: a Beacon user who creates a project with the same name never gets the host's data,
/// documents or endpoint tools.
/// </summary>
public interface IHostProjectResolver
{
    /// <summary>The host project's id, or null when the host has not synced it yet.</summary>
    Task<int?> FindAsync(string projectName, CancellationToken cancellationToken);

    /// <summary>
    /// The host project's id, creating it (or un-archiving it) when needed. When a project that is not host-managed
    /// already uses <paramref name="projectName"/>, the host project is created as <c>"{projectName} (host)"</c> (or
    /// <c>"(host 2)"</c>, …) and a warning is logged; the existing project is left untouched.
    /// </summary>
    Task<int> EnsureAsync(string projectName, string? description, CancellationToken cancellationToken);
}

internal sealed class HostProjectResolver(
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<HostProjectResolver> logger) : IHostProjectResolver
{
    private const string KeyPrefix = "host:";
    private const int MaxDisambiguationAttempts = 100;

    /// <summary><c>host:{name}</c>, trimmed and lower-cased so the key does not depend on the casing a host uses.</summary>
    public static string KeyFor(string projectName) => KeyPrefix + projectName.Trim().ToLowerInvariant();

    public async Task<int?> FindAsync(string projectName, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await BuildFindQuery(context, KeyFor(projectName))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<int> EnsureAsync(string projectName, string? description, CancellationToken cancellationToken)
    {
        try
        {
            return await EnsureOnceAsync(projectName, description, cancellationToken);
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            // Another replica created the host project between our read and our insert: use its row.
            return await EnsureOnceAsync(projectName, description, cancellationToken);
        }
    }

    internal static IQueryable<int?> BuildFindQuery(BeaconContext context, string key) =>
        context.Projects
            .Where(x => x.HostManagedKey == key)
            .Select(x => (int?)x.Id);

    private async Task<int> EnsureOnceAsync(string projectName, string? description, CancellationToken cancellationToken)
    {
        var name = projectName.Trim();
        var key = KeyFor(name);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Archived rows count: the unique key covers them, and the host still declares the project.
        var existing = await context.Projects
            .IgnoreQueryFilters()
            .Where(x => x.HostManagedKey == key)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing != null)
        {
            if (existing.ArchivedTime == null)
            {
                return existing.Id;
            }

            existing.Unarchive();
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Host project {ProjectId} was archived and has been restored because the host still declares it.", existing.Id);

            return existing.Id;
        }

        var projectNameToUse = await ChooseNameAsync(context, name, cancellationToken);
        if (projectNameToUse != name)
        {
            logger.LogWarning(
                "A Beacon project named {ProjectName} already exists and is not host-managed; the host project is created as {HostProjectName} instead of taking it over.",
                name,
                projectNameToUse);
        }

        var project = new Project
        {
            Name = projectNameToUse,
            Description = description,
            HostManagedKey = key
        };
        context.Projects.Add(project);

        await context.SaveChangesAsync(cancellationToken);

        return project.Id;
    }

    private static async Task<string> ChooseNameAsync(BeaconContext context, string name, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxDisambiguationAttempts; attempt++)
        {
            var candidate = attempt switch
            {
                0 => name,
                1 => $"{name} (host)",
                _ => $"{name} (host {attempt})"
            };

            var taken = await context.Projects
                .IgnoreQueryFilters()
                .Where(x => x.Name == candidate)
                .AnyAsync(cancellationToken);

            if (!taken)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"Could not find a free name for the host project '{name}'.");
    }
}
