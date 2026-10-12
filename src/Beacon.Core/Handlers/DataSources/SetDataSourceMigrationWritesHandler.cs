using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class SetDataSourceMigrationWritesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<MigrationOptions> migrationOptions,
    IBeaconActorAccessor actorAccessor,
    IMemoryCache cache,
    ISchemaGraphService schemaGraphService,
    TimeProvider timeProvider,
    ILogger<SetDataSourceMigrationWritesHandler> logger)
    : IRequestHandler<SetDataSourceMigrationWritesCommand>
{
    public async Task Handle(SetDataSourceMigrationWritesCommand request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        // The deployment lock: no data source becomes a write target while migrations are off. Unmarking one stays
        // possible, so a deployment that turns migrations off can return its targets to ordinary read sources.
        if (request.AllowMigrationWrites && !migrationOptions.Value.Enabled)
        {
            throw new BeaconConflictException(MigrationWriteTargets.DisabledMessage);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var dataSource = await context.DataSources
            .Where(x => x.Id == request.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Data source {request.DataSourceId} not found.");

        var before = dataSource.AllowMigrationWrites;
        if (before == request.AllowMigrationWrites)
        {
            return;
        }

        if (request.AllowMigrationWrites)
        {
            var refusal = MigrationWriteTargets.MarkRefusal(dataSource);
            if (refusal != null)
            {
                throw new InvalidOperationException(refusal);
            }

            // Beacon stops reading the source, so the queries, subscriptions and data contracts that read it fail from then
            // on: the Admin confirms that first (Force), told how many there are.
            var queryCount = await context.Queries
                .WhereReading(request.DataSourceId)
                .CountAsync(cancellationToken);
            var subscriptionCount = await context.Subscriptions
                .WhereReading(request.DataSourceId)
                .CountAsync(cancellationToken);
            var contractCount = await context.DataContracts
                .WhereReading(request.DataSourceId)
                .CountAsync(cancellationToken);
            if (queryCount + subscriptionCount + contractCount > 0)
            {
                if (!request.Force)
                {
                    throw new BeaconConflictException(
                        MigrationWriteTargets.InUseMessage(request.DataSourceId, queryCount, subscriptionCount, contractCount));
                }

                logger.LogWarning(
                    ProjectAccessEvents.MigrationWritesChanged,
                    "Data source {DataSourceId} is marked as a migration write target while {QueryCount} queries, {SubscriptionCount} subscriptions and {ContractCount} data contracts read it",
                    request.DataSourceId,
                    queryCount,
                    subscriptionCount,
                    contractCount);
            }

            // Beacon never reads a write target, and its login may change to one that can write: the column values sampled
            // through the old login go with it, so no path that serves stored metadata can show them.
            var sampledColumns = await context.ColumnMetadata
                .Where(x => x.DatabaseMetadata.DataSourceId == request.DataSourceId)
                .Where(x => x.SampleValues != null || x.SampleValuesComplete)
                .ToListAsync(cancellationToken);
            foreach (var column in sampledColumns)
            {
                column.SampleValues = null;
                column.SampleValuesComplete = false;
            }
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        dataSource.AllowMigrationWrites = request.AllowMigrationWrites;
        context.AppSettingHistory.Add(new AppSettingHistory
        {
            SettingKey = SettingKey(request.DataSourceId),
            OldValue = Format(before),
            NewValue = Format(request.AllowMigrationWrites),
            ChangedAt = timeProvider.GetUtcNow().UtcDateTime,
            ChangedByUserId = actor.LogId
        });
        await context.SaveChangesAsync(cancellationToken);

        // A write target has no catalog in Beacon, and an unmarked one is scanned afresh: the cached metadata and the
        // relationship graph describe the source as it was.
        cache.Remove(DatabaseMetadataService.GetCacheKey(request.DataSourceId));
        schemaGraphService.Invalidate(request.DataSourceId);

        logger.LogWarning(
            ProjectAccessEvents.MigrationWritesChanged,
            "Data source {DataSourceId} migration writes changed from {Before} to {After} by {ActorId}",
            request.DataSourceId,
            before,
            request.AllowMigrationWrites,
            actor.LogId ?? "unknown");
    }

    /// <summary>The settings-history key a change is recorded under.</summary>
    public static string SettingKey(int dataSourceId) =>
        string.Create(CultureInfo.InvariantCulture, $"DataSource.{dataSourceId}.{nameof(DataSource.AllowMigrationWrites)}");

    private static string Format(bool value) => value ? "true" : "false";
}

/// <summary>
/// Marks a data source as a migration write target, or returns it to an ordinary read source. Admin only. Marking needs a
/// deployment that runs migrations (409 otherwise) and a PostgreSQL, MySQL or SQL Server data source that is neither
/// host-managed nor marked read-only. A write target is never read through Beacon (queries, previews, subscriptions, data
/// quality, MCP, AI, metadata), so its connection may carry the write grants a migration needs. Every change is recorded
/// in the settings history (<c>DataSource.{id}.AllowMigrationWrites</c>, before and after, by whom) and in the security
/// log. A source that live queries, subscriptions or data contracts read is marked only with <paramref name="Force"/> — they
/// fail once Beacon stops reading it — and is otherwise refused with 409 naming how many read it.
/// </summary>
/// <param name="Force">The Admin confirmed marking a source that queries, subscriptions or data contracts read.</param>
[ProjectScopeExempt("data-source administration reserved to an Admin: a write target may carry a write-capable login", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record SetDataSourceMigrationWritesCommand(int DataSourceId, bool AllowMigrationWrites, bool Force = false) : IRequest;
