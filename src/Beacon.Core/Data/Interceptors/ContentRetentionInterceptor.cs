using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;

namespace Beacon.Core.Data.Interceptors;

/// <summary>
/// The belt behind the braces: whatever a write site forgets, this interceptor strips content-classified string
/// properties off every Added/Modified <c>Mcp*</c> entity whose project holds the content lock. Structural
/// properties are never touched; error text is replaced by its class. It logs identifiers only (§1.11) and never
/// throws — a settings-resolution failure fails closed (treated as locked).
/// </summary>
public sealed class ContentRetentionInterceptor(
    IServiceProvider serviceProvider,
    ILogger<ContentRetentionInterceptor> logger) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context != null)
        {
            await RedactTrackedEntriesAsync(eventData.Context.ChangeTracker, ResolveDecisionAsync, cancellationToken);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // Defensive only: no production path calls the synchronous SaveChanges() today (verified repo-wide).

    // It exists so a future sync caller cannot open a redaction gap; ASP.NET Core captures no SynchronizationContext,

    // so blocking here cannot deadlock.

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context != null)
        {
            RedactTrackedEntriesAsync(eventData.Context.ChangeTracker, ResolveDecisionAsync, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }

        return base.SavingChanges(eventData, result);
    }

    /// <summary>
    /// The whole behaviour, driven by an injected resolver so it can be exercised without a database (SC3).
    /// Entries are grouped by project id so one settings read covers every row of the same project.
    /// </summary>
    internal async Task RedactTrackedEntriesAsync(
        ChangeTracker changeTracker,
        Func<int?, CancellationToken, Task<ContentRetentionDecision>> resolve,
        CancellationToken cancellationToken = default)
    {
        var entries = changeTracker.Entries()
            .Where(x => x.State == EntityState.Added || x.State == EntityState.Modified)
            .Where(x => McpRetentionDenyList.RulesFor(x.Entity.GetType()).Count > 0)
            .ToList();

        if (entries.Count == 0)
        {
            return;
        }

        var decisions = new Dictionary<int, ContentRetentionDecision>();
        var globalDecision = default(ContentRetentionDecision);

        foreach (var entry in entries)
        {
            var entityType = entry.Entity.GetType();
            var projectId = McpRetentionDenyList.ProjectIdSelector(entityType)?.Invoke(entry.Entity);
            ContentRetentionDecision decision;

            if (projectId == null)
            {
                globalDecision ??= await ResolveOrLockAsync(resolve, null, entry, entityType, cancellationToken);
                decision = globalDecision;
            }
            else if (!decisions.TryGetValue(projectId.Value, out var cached))
            {
                decision = await ResolveOrLockAsync(resolve, projectId, entry, entityType, cancellationToken);
                decisions[projectId.Value] = decision;
            }
            else
            {
                decision = cached;
            }

            if (decision.Locked)
            {
                Redact(entry, entityType);
            }
        }
    }

    private async Task<ContentRetentionDecision> ResolveDecisionAsync(int? projectId, CancellationToken cancellationToken)
    {
        var settingsProvider = serviceProvider.GetRequiredService<IMcpSettingsProvider>();
        var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId ?? 0, cancellationToken);

        return ContentRetentionDecision.From(settings);
    }

    private async Task<ContentRetentionDecision> ResolveOrLockAsync(
        Func<int?, CancellationToken, Task<ContentRetentionDecision>> resolve,
        int? projectId,
        EntityEntry entry,
        Type entityType,
        CancellationToken cancellationToken)
    {
        try
        {
            return await resolve(projectId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Content retention settings could not be resolved for {EntityType} {EntityId}; failing closed (content locked).",
                entityType.Name,
                IdOf(entry));

            return new ContentRetentionDecision(false, false);
        }
    }

    private void Redact(EntityEntry entry, Type entityType)
    {
        foreach (var rule in McpRetentionDenyList.RulesFor(entityType))
        {
            if (rule.Kind == RetentionKind.Structural)
            {
                continue;
            }

            var property = entityType.GetProperty(rule.Property, BindingFlags.Public | BindingFlags.Instance);

            if (property == null || !property.CanWrite)
            {
                continue;
            }

            var current = GetValue(entry, property);

            if (string.IsNullOrEmpty(current))
            {
                continue;
            }

            // A brace may rewrite a Content property into a structural shape instead of nulling it
            // (McpAuditLog.Parameters). The belt must not destroy what the brace just wrote.
            if (rule.AlreadyRedacted?.Invoke(current) == true)
            {
                continue;
            }

            var replacement = rule.Kind == RetentionKind.ErrorClass
                ? McpContentRedactor.ErrorClassOf(current)
                : McpRetentionDenyList.IsRequiredString(entityType, rule.Property) ? string.Empty : null;

            if (string.Equals(current, replacement, StringComparison.Ordinal))
            {
                continue;
            }

            SetValue(entry, property, replacement);

            logger.LogWarning(
                "Content retention lock cleared {EntityType}.{PropertyName} on entity {EntityId}.",
                entityType.Name,
                rule.Property,
                IdOf(entry));
        }
    }

    private static string? GetValue(EntityEntry entry, PropertyInfo property) =>
        entry.Metadata.FindProperty(property.Name) != null
            ? entry.Property(property.Name).CurrentValue as string
            : property.GetValue(entry.Entity) as string;

    private static void SetValue(EntityEntry entry, PropertyInfo property, string? value)
    {
        if (entry.Metadata.FindProperty(property.Name) != null)
        {
            entry.Property(property.Name).CurrentValue = value;

            return;
        }

        property.SetValue(entry.Entity, value);
    }

    private static int? IdOf(EntityEntry entry) => (entry.Entity as BaseEntity)?.Id;
}
