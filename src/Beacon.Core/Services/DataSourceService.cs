using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Approvals;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Models.Providers;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

/// <summary>
/// Probes the server of a governed data source's stored connection before its connection is approved: a source held for
/// approval connects to nothing, so the engine check its save skipped runs once here; and, once the approval is committed,
/// reads its catalog with the connection now approved. Implemented by the data-source service.
/// </summary>
internal interface IHeldDataSourceProbe
{
    /// <summary>
    /// Checks the stored connection of data source <paramref name="dataSourceId"/> as a new connection (its transport, then
    /// the declared engine against the server's answer). A declared engine the server clearly is not, a server that cannot
    /// be reached or whose engine cannot be read, and a disabled source (enabled first) are refused with a
    /// <see cref="BeaconConflictException"/> (409) that keeps the cause. A source that is gone or host-managed, or whose
    /// stored connection cannot be read, is not probed: the decision's own state check answers those.
    /// </summary>
    Task ProbeAsync(int dataSourceId, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="ProbeAsync(int, CancellationToken)"/> for an approval of the definition <paramref name="definitionHash"/>:
    /// a source whose stored connection is not that definition is refused as stale (409) before anything connects.
    /// </summary>
    Task ProbeAsync(int dataSourceId, string definitionHash, CancellationToken cancellationToken)
    {
        return ProbeAsync(dataSourceId, cancellationToken);
    }

    /// <summary>
    /// After the connection of data source <paramref name="dataSourceId"/> was approved: its cached metadata is evicted and
    /// its catalog read again, as a save that connects does it — a database source's re-scan is enqueued, and an API source
    /// that has no endpoints stored yet (created while held, so its create imported none) imports them from its OpenAPI
    /// document. Best effort: an import or enqueue that fails is logged, never thrown.
    /// </summary>
    Task ConnectionApprovedAsync(int dataSourceId, CancellationToken cancellationToken);
}

/// <summary>
/// Data-source administration. While the change approval workflow covers data-source connections
/// (<see cref="ApprovalWorkflowOptions.CoversDataSourceConnections"/>), a created source is saved held
/// (<see cref="DataSource.ApprovedTargetHash"/> <see cref="DataSourceApprovalDefinition.Held"/>) with a pending request, and
/// connects to nothing until a second person approves it: no engine probe and no API metadata import run until then. An
/// update that changes any setting of the connection but its secrets is saved, submitted, and holds the source until it is
/// approved (no probe, no metadata re-scan meanwhile); a change of the secrets alone is an immediate rotation. With the
/// workflow off, a save that changes a governed source's definition makes it ungoverned again and supersedes its pending
/// requests. Deleting a source supersedes its pending requests in the same save.
/// </summary>
internal sealed class DataSourceService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IDataSourceProviderFactory providerFactory,
    IMemoryCache cache,
    ISchemaGraphService schemaGraphService,
    ISecurityAuditRecorder securityAudit,
    IDataSourceTransportPolicy transportPolicy,
    IDatabaseEngineVerifier engineVerifier,
    ChangeApprovalSubmitter changeApprovals,
    ConnectionDefinitionHasher definitionHasher,
    ILogger<DataSourceService> logger,
    IBeaconScheduler? scheduler = null,
    ICatalogSnapshotCache? catalogSnapshots = null) : IHeldDataSourceProbe
{
    /// <summary>What a caller is told when a change of target is not confirmed (HTTP 409).</summary>
    public const string TargetChangeUnconfirmedMessage =
        "The new connection points at another server, port, database or engine. Confirm the target change to save it: the column values sampled from the old target are removed.";

    /// <summary>What a connection test answers when the server's engine could not be read from its answer.</summary>
    public const string UnverifiedEngineMessage = "Connection successful. The server's engine could not be verified.";

    /// <summary>What a create answers while the workflow covers data-source connections.</summary>
    public const string SubmittedForApprovalMessage =
        "The data source was saved and waits for approval: it connects to nothing until a second person approves its connection.";

    /// <summary>Why the connection of a disabled data source is not approved.</summary>
    public const string DisabledApprovalMessage =
        "This data source is disabled, so its server cannot be checked: enable it, then approve its connection.";

    private const ChangeApprovalKind ConnectionKind = ChangeApprovalKind.DataSourceConnection;

    // The longest change summary and resource name a change request stores.
    private const int MaxChangeSummaryLength = 2000;
    private const int MaxResourceNameLength = 200;

    public async Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataSourceData.ConnectionString))
        {
            throw new InvalidOperationException("A connection string is required.");
        }

        var connectionString = dataSourceData.ConnectionString;
        transportPolicy.EnsureNewConnectionAllowed(dataSourceData.DataSourceType, dataSourceData.DatabaseEngineType, connectionString);
        providerFactory.GetProvider(dataSourceData.DataSourceType).EnsureNewConnectionDataAllowed(connectionString);

        // Under the workflow the source is held until approved: it connects to nothing now, and its engine is checked when
        // its connection is approved.
        var requester = await changeApprovals.RequesterAsync(ConnectionKind, null, cancellationToken);
        if (requester == null)
        {
            await EnsureDeclaredEngineAsync(null, dataSourceData.DataSourceType, dataSourceData.DatabaseEngineType, connectionString, refuseUnreachable: false, cancellationToken);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var projectIds = dataSourceData.ProjectIds
            .Distinct()
            .ToList();

        DataSource AddNew()
        {
            var created = new DataSource
            {
                Name = dataSourceData.Name,
                DataSourceType = dataSourceData.DataSourceType,
                EncryptedConnectionData = encryptionService.Encrypt(connectionString),
                DatabaseEngineType = dataSourceData.DatabaseEngineType,
                MetadataLoadingEnabled = dataSourceData.MetadataLoadingEnabled ?? true,
                MetadataMaxTables = dataSourceData.MetadataMaxTables ?? 0,
                MetadataMaxColumnsPerTable = dataSourceData.MetadataMaxColumnsPerTable ?? 0,
                MetadataLoadTableNamesOnly = dataSourceData.MetadataLoadTableNamesOnly ?? false,
                MetadataExcludeSchemas = SchemaList(dataSourceData.MetadataExcludeSchemas),
                MetadataIncludeSchemas = SchemaList(dataSourceData.MetadataIncludeSchemas),
                // Admin-only until a project links it: only sources that existed before project access stay open unlinked.
                OpenWhenUnlinked = false,
                // Under the workflow nothing is approved yet: it connects to nothing until its first approval.
                ApprovedTargetHash = requester == null ? null : DataSourceApprovalDefinition.Held
            };

            context.DataSources.Add(created);
            foreach (var projectId in projectIds)
            {
                context.ProjectDataSources.Add(new ProjectDataSource { ProjectId = projectId, DataSource = created, LinkedByUserId = dataSourceData.LinkedByUserId });
            }

            return created;
        }

        DataSource dataSource;
        if (requester == null)
        {
            dataSource = AddNew();
            await securityAudit.SaveWithEventsAsync(context, () => [CreatedEntry(dataSource, projectIds)], cancellationToken);
        }
        else
        {
            // The source's save gives it the id its request names; the source, its request and their events commit together.
            DataSource? held = null;
            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                held = AddNew();
                await context.SaveChangesAsync(cancellationToken);

                var submission = await SubmitNewAsync(context, requester, held, connectionString, cancellationToken);
                await securityAudit.SaveWithEventsAsync(
                    context,
                    () => new[] { CreatedEntry(held, projectIds) }.Concat(ChangeApprovalAuditEntries.Submitted(submission)),
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
            dataSource = held!;
        }

        DataSourceReadOnlyEnforcement.WarnIfNotEnforced(logger, providerFactory, dataSource.Id, dataSource.DataSourceType, dataSource.DatabaseEngineType);

        // For API data sources, import endpoint metadata from OpenAPI spec; a held source reads nothing until it is approved,
        // and its approval imports them.
        if (dataSourceData.DataSourceType == DataSourceType.Api && requester == null)
        {
            await ImportApiEndpointsAsync(context, dataSource, cancellationToken);
        }

        return new BaseResponse
        {
            Success = true,
            Message = requester == null ? null : SubmittedForApprovalMessage
        };
    }

    public Task ProbeAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        return ProbeCoreAsync(dataSourceId, null, cancellationToken);
    }

    public Task ProbeAsync(int dataSourceId, string definitionHash, CancellationToken cancellationToken)
    {
        return ProbeCoreAsync(dataSourceId, definitionHash, cancellationToken);
    }

    public async Task ConnectionApprovedAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .AsNoTracking()
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(cancellationToken);
        if (dataSource == null)
        {
            return;
        }

        // The cached metadata described the connection as it was before the approval.
        EvictMetadata(dataSourceId);

        if (dataSource.DataSourceType != DataSourceType.Api)
        {
            await EnqueueMetadataRefreshAsync(dataSource);

            return;
        }

        var hasEndpoints = await context.DatabaseMetadata
            .Where(x => x.DataSourceId == dataSourceId)
            .AnyAsync(cancellationToken);
        if (!hasEndpoints && dataSource.DisabledTime == null)
        {
            await ImportApiEndpointsAsync(context, dataSource, cancellationToken);
        }
    }

    /// <summary>A data source's row, taken first in a unit of work that changes its connection (a no-op update; 0 when it is gone).</summary>
    internal static Task<int> LockAsync(BeaconContext context, int dataSourceId, CancellationToken cancellationToken)
    {
        return context.DataSources
            .Where(x => x.Id == dataSourceId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.ApprovedTargetHash, y => y.ApprovedTargetHash), cancellationToken);
    }

    private static async Task StoreApiEndpointsAsMetadata(
        BeaconContext context,
        int dataSourceId,
        List<ApiEndpointMetadata> endpoints,
        CancellationToken cancellationToken)
    {
        foreach (var endpoint in endpoints)
        {
            var schemaName = endpoint.Tag ?? "default";
            var tableName = $"{endpoint.Method} {endpoint.Path}";

            var metadata = new DatabaseMetadata
            {
                DataSourceId = dataSourceId,
                SchemaName = schemaName,
                TableName = tableName,
                TableDescription = endpoint.Summary ?? endpoint.Description,
                LastRefreshed = DateTime.UtcNow
            };

            // Add response fields as columns
            foreach (var (field, index) in endpoint.ResponseFields.Select((f, i) => (f, i)))
            {
                metadata.Columns.Add(new ColumnMetadata
                {
                    ColumnName = field.Name,
                    DataType = field.Type,
                    IsNullable = true,
                    OrdinalPosition = index,
                    Description = field.Description
                });
            }

            context.DatabaseMetadata.Add(metadata);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDataSource(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // IgnoreQueryFilters so archived queries still materialize on the QueryStep→Query navigation.
        // Query is an ArchivableBaseEntity (global ArchivedTime == null filter); without this, qs.Query
        // is null for any archived query and the in-memory qs.Query.ArchivedTime check below throws NRE —
        // on the exact happy path where every referencing query is already archived.
        var dataSource = await context.DataSources
            .IgnoreQueryFilters()
            .Include(x => x.QuerySteps)
                .ThenInclude(qs => qs.Query)
            .Where(x => x.Id == dataSourceId)
            .SingleAsync(cancellationToken);

        // IgnoreQueryFilters also disables the DataSource's own soft-delete filter, so an
        // already-archived source is returned here rather than throwing. Re-archiving would reset
        // ArchivedTime, so treat an already-archived source as a no-op.
        if (dataSource.ArchivedTime != null)
        {
            return;
        }

        EnsureNotHostManaged(dataSource);

        // Check for non-archived queries using this data source
        var unarchivedQueries = dataSource.QuerySteps
            .Where(qs => qs.Query.ArchivedTime == null)
            .Select(qs => qs.Query)
            .DistinctBy(q => q.Id)
            .ToList();

        if (unarchivedQueries.Count > 0)
        {
            var queryNames = string.Join(", ", unarchivedQueries.Select(q => q.Name));
            throw new BeaconException($"Unable to archive data source. The following queries must be archived first: {queryNames}");
        }

        // Check for non-archived migration jobs using this data source (as source or destination)
        var unarchivedMigrationJobs = await context.MigrationJobs
            .Where(mj => (mj.DataSourceId == dataSourceId || mj.DestinationDataSourceId == dataSourceId)
                         && mj.ArchivedTime == null)
            .ToListAsync(cancellationToken);

        if (unarchivedMigrationJobs.Count > 0)
        {
            var migrationJobNames = string.Join(", ", unarchivedMigrationJobs.Select(mj => mj.Name));
            throw new BeaconException($"Unable to archive data source. The following migration jobs must be archived first: {migrationJobNames}");
        }

        // The delete's event names the change requests it superseded, by id.
        async Task ArchiveAsync(ChangeSubmission? superseded)
        {
            // The column values sampled from the source go with it, archived tables' columns included: an archived source is
            // never read again, so nothing keeps a copy of its rows.
            await DatabaseMetadataService.ClearStoredSampleValuesAsync(context, dataSourceId, cancellationToken);

            dataSource.Archive();
            var deleted = new SecurityAuditEntry(SecurityAuditActions.DataSourceDeleted).On(SecurityAuditResources.DataSource, dataSourceId);
            if (superseded is { Superseded.Count: > 0 })
            {
                deleted = deleted.With(new SecurityAuditDetails().Add("supersededRequestIds", superseded.Superseded.Select(x => x.Id)));
            }

            await securityAudit.StageAsync(context, deleted, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Only a governed source can have connection changes waiting (every submission records an approved hash, and a save
        // that ungoverns a source supersedes them): they can no longer be approved, so they are superseded with the delete,
        // its row taken first as a submission takes it, and their requesters are told after the commit.
        ChangeSubmission? superseded = null;
        if (dataSource.ApprovedTargetHash == null)
        {
            await ArchiveAsync(null);
        }
        else
        {
            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                await LockAsync(context, dataSourceId, cancellationToken);
                superseded = await changeApprovals.SupersedeOfDeletedAsync(context, ConnectionKind, dataSourceId, cancellationToken);
                await ArchiveAsync(superseded);
                await transaction.CommitAsync(cancellationToken);
            });
        }

        await changeApprovals.NotifySupersededAsync(superseded, cancellationToken);
        EvictMetadata(dataSourceId);
    }

    public async Task UpdateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        var dataSourceId = dataSourceData.DataSourceId ?? 0;
        var requester = await changeApprovals.CallerAsync(ConnectionKind, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The change is planned on the source as stored, and its server checked, before the unit of work takes the source's
        // row: the check talks to the server, which no row lock or open transaction waits on. Under the lock it is planned
        // again, and checked again only when the connection to save is not the one checked here.
        var stored = await context.DataSources
            .AsNoTracking()
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Data source {dataSourceData.DataSourceId} not found.");
        var plan = await PlanUpdateAsync(stored, dataSourceData, requester, cancellationToken);
        string? checkedConnection = null;
        if (plan.ChecksServer(stored))
        {
            await EnsureDeclaredEngineAsync(stored.Id, stored.DataSourceType, dataSourceData.DatabaseEngineType, plan.ConnectionString, refuseUnreachable: false, cancellationToken);
            checkedConnection = plan.ConnectionString;
        }

        SavedUpdate? saved = null;
        if (requester == null && stored.ApprovedTargetHash == null)
        {
            saved = await SaveUpdateAsync(context, dataSourceData, null, checkedConnection, cancellationToken);
        }
        else
        {
            // Under the workflow, or for a source it governed: the source's row first (as a decision takes it), then the
            // change, its request and the requests it supersedes, committed together; a refused change commits nothing.
            var strategy = context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

                await LockAsync(context, dataSourceId, cancellationToken);
                saved = await SaveUpdateAsync(context, dataSourceData, requester, checkedConnection, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });
        }

        var update = saved!;
        var dataSource = update.DataSource;
        await changeApprovals.NotifySupersededAsync(update.Submission, cancellationToken);
        DataSourceReadOnlyEnforcement.WarnIfNotEnforced(logger, providerFactory, dataSource.Id, dataSource.DataSourceType, dataSource.DatabaseEngineType);

        // The connections pooled with the old credentials are not reused.
        if (update.ConnectionChanged || update.EngineChanged)
        {
            ClearConnectionPool(dataSource.Id, dataSource.DataSourceType, update.PreviousEngine, update.StoredConnection);
        }

        // The connection or the metadata options may have changed: the cached metadata described the old ones.
        EvictMetadata(dataSource.Id);

        // A source held until its change is approved is not re-scanned (the scan could not connect): its approval re-scans it.
        if (update.TargetChanged && !update.Held)
        {
            await EnqueueMetadataRefreshAsync(dataSource);
        }
    }

    /// <summary>
    /// Disables a data source (no connection is made with it from then on) or enables it again; recorded as who disabled it
    /// and when, with a security event. Setting the state it already has changes nothing and records nothing.
    /// </summary>
    public async Task SetDataSourceEnabled(int dataSourceId, bool enabled, int? actorUserId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Data source {dataSourceId} not found.");

        EnsureNotHostManaged(dataSource);

        if ((dataSource.DisabledTime == null) == enabled)
        {
            return;
        }

        dataSource.DisabledTime = enabled ? null : DateTime.UtcNow;
        dataSource.DisabledByUserId = enabled ? null : actorUserId;
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(enabled ? SecurityAuditActions.DataSourceEnabled : SecurityAuditActions.DataSourceDisabled)
                .On(SecurityAuditResources.DataSource, dataSourceId),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Data source {DataSourceId} was {State} by user {ActorUserId}", dataSourceId, enabled ? "enabled" : "disabled", actorUserId);

        // A disabled source's catalog is not served (the metadata service answers an empty one) and an enabled one's is read
        // again: neither is served from the cache built before the change.
        EvictMetadata(dataSourceId);

        if (!enabled)
        {
            // No idle connection opened before the source was disabled is reused.
            ClearConnectionPool(
                dataSourceId,
                dataSource.DataSourceType,
                dataSource.DatabaseEngineType,
                StoredConnection(dataSource.EncryptedConnectionData, dataSource.Id, dataSource.DatabaseEngineType));
        }
    }

    public async Task<BaseResponse> TestConnection(string connectionString, Data.Enums.DatabaseEngineType databaseEngineType, CancellationToken cancellationToken)
    {
        var refusal = TransportRefusal(DataSourceType.Database, databaseEngineType, connectionString);
        if (refusal != null)
        {
            return refusal;
        }

        try
        {
            await using var connection = DbConnectionFactory.CreateConnection(databaseEngineType, connectionString);
            await connection.OpenAsync(cancellationToken);

            // The declared engine is checked on the connection just opened: a clear mismatch fails the test, a compatible
            // engine passes with a note, an answer that cannot be read passes as before.
            var identification = await engineVerifier.IdentifyAsync(null, databaseEngineType, connection, cancellationToken);
            await connection.CloseAsync();

            if (identification.Verdict == DatabaseEngineIdentityVerdict.Mismatch)
            {
                // No idle connection opened with the refused connection stays in the pool.
                ClearConnectionPool(null, DataSourceType.Database, databaseEngineType, connectionString);

                return new BaseResponse
                {
                    Success = false,
                    Message = $"Connection refused: {identification.Message}"
                };
            }

            return new BaseResponse
            {
                Success = true,
                Message = identification.Verdict switch
                {
                    DatabaseEngineIdentityVerdict.Compatible => $"Connection successful. {identification.Message}",
                    DatabaseEngineIdentityVerdict.Unverified => UnverifiedEngineMessage,
                    _ => "Connection successful"
                }
            };
        }
        catch (Exception ex)
        {
            return new BaseResponse
            {
                Success = false,
                Message = $"Connection failed: {ConnectionFailureDescriber.Describe(ex)}"
            };
        }
    }

    public async Task<BaseResponse> TestConnectionAsync(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        var connectionString = dataSourceData.ConnectionString ?? string.Empty;
        var refusal = TransportRefusal(dataSourceData.DataSourceType, dataSourceData.DatabaseEngineType, connectionString);
        if (refusal != null)
        {
            return refusal;
        }

        try
        {
            // For database types, use the connection factory directly
            if (dataSourceData.DataSourceType == DataSourceType.Database && dataSourceData.DatabaseEngineType.HasValue)
            {
                return await TestConnection(connectionString, dataSourceData.DatabaseEngineType.Value, cancellationToken);
            }

            // For non-database types (CloudWatch, Databricks, BigQuery), use the provider pattern
            var provider = providerFactory.GetProvider(dataSourceData.DataSourceType);
            var tempDataSource = new DataSource
            {
                Name = dataSourceData.Name ?? "connection-test",
                DataSourceType = dataSourceData.DataSourceType,
                DatabaseEngineType = dataSourceData.DatabaseEngineType,
                EncryptedConnectionData = encryptionService.Encrypt(connectionString)
            };

            var result = await provider.TestConnectionAsync(tempDataSource, cancellationToken);

            return new BaseResponse
            {
                Success = result.Success,
                Message = result.Success ? "Connection successful" : $"Connection failed: {result.ErrorMessage}"
            };
        }
        catch (Exception ex)
        {
            return new BaseResponse
            {
                Success = false,
                Message = $"Connection failed: {ConnectionFailureDescriber.Describe(ex)}"
            };
        }
    }

    // The approval's check of a held source's server: refused as stale when definitionHash is given and the stored
    // connection is not that definition, before anything connects.
    private async Task ProbeCoreAsync(int dataSourceId, string? definitionHash, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .AsNoTracking()
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(cancellationToken);

        // A source gone is answered by the decision's own check, a stored connection Beacon cannot read by its state check.
        if (dataSource == null || dataSource.HostManagedKey != null)
        {
            return;
        }

        // The check at its approval is the only one a held source gets: one that is disabled connects to nothing, so it is
        // enabled first.
        if (dataSource.DisabledTime != null)
        {
            throw new BeaconConflictException(DisabledApprovalMessage);
        }

        var connectionString = StoredConnection(dataSource.EncryptedConnectionData, dataSource.Id, dataSource.DatabaseEngineType);
        if (connectionString == null)
        {
            return;
        }

        if (definitionHash != null && !ProposesStoredDefinition(dataSource, connectionString, definitionHash))
        {
            throw new BeaconConflictException(ChangeApprovalService.ChangedSinceSubmittedMessage);
        }

        // Approving it makes the connection live: it is checked as a new one, then against the server, which must answer.
        transportPolicy.EnsureNewConnectionAllowed(dataSource.DataSourceType, dataSource.DatabaseEngineType, connectionString);
        providerFactory.GetProvider(dataSource.DataSourceType).EnsureNewConnectionDataAllowed(connectionString);
        try
        {
            await EnsureDeclaredEngineAsync(dataSource.Id, dataSource.DataSourceType, dataSource.DatabaseEngineType, connectionString, refuseUnreachable: true, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            // The engine verifier's refusal names the declared and the detected engine, or that the server could not be
            // reached, never a value of the connection.
            throw new BeaconConflictException(ex.Message, ex);
        }
    }

    // Whether the stored connection's definition is the one definitionHash names, under the current key or a previous one.
    private bool ProposesStoredDefinition(DataSource dataSource, string connectionString, string definitionHash)
    {
        var canonical = DataSourceApprovalDefinition.Canonical(transportPolicy, dataSource.DataSourceType, dataSource.DatabaseEngineType, connectionString);

        return definitionHasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, definitionHash, canonical);
    }

    // Stores the change in the context's unit of work (under the workflow, also submitted for approval) and saves it. The
    // server was checked before for checkedConnection; any other new connection or engine is checked here.
    private async Task<SavedUpdate> SaveUpdateAsync(
        BeaconContext context,
        DataSourceData dataSourceData,
        ChangeRequester? requester,
        string? checkedConnection,
        CancellationToken cancellationToken)
    {
        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceData.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Data source {dataSourceData.DataSourceId} not found.");

        var plan = await PlanUpdateAsync(dataSource, dataSourceData, requester, cancellationToken);
        var storedConnection = plan.StoredConnection;
        var connectionString = plan.ConnectionString;
        var connectionChanged = plan.ConnectionChanged;
        var engineChanged = plan.EngineChanged;
        var targetChanged = plan.TargetChanged;
        var approval = plan.Approval;
        if (plan.ChecksServer(dataSource) && !string.Equals(checkedConnection, connectionString, StringComparison.Ordinal))
        {
            await EnsureDeclaredEngineAsync(dataSource.Id, dataSource.DataSourceType, dataSourceData.DatabaseEngineType, connectionString, refuseUnreachable: false, cancellationToken);
        }

        // A metadata option left out (null) keeps the stored value.
        var metadataLoadingEnabled = dataSourceData.MetadataLoadingEnabled ?? dataSource.MetadataLoadingEnabled;
        var metadataMaxTables = dataSourceData.MetadataMaxTables ?? dataSource.MetadataMaxTables;
        var metadataMaxColumnsPerTable = dataSourceData.MetadataMaxColumnsPerTable ?? dataSource.MetadataMaxColumnsPerTable;
        var metadataLoadTableNamesOnly = dataSourceData.MetadataLoadTableNamesOnly ?? dataSource.MetadataLoadTableNamesOnly;
        var excludeSchemas = dataSourceData.MetadataExcludeSchemas == null ? dataSource.MetadataExcludeSchemas : SchemaList(dataSourceData.MetadataExcludeSchemas);
        var includeSchemas = dataSourceData.MetadataIncludeSchemas == null ? dataSource.MetadataIncludeSchemas : SchemaList(dataSourceData.MetadataIncludeSchemas);
        var changes = new (string Field, bool Changed)[]
        {
            (nameof(DataSource.Name), dataSource.Name != dataSourceData.Name),
            (nameof(DataSource.DatabaseEngineType), engineChanged),
            (nameof(DataSource.MetadataLoadingEnabled), dataSource.MetadataLoadingEnabled != metadataLoadingEnabled),
            (nameof(DataSource.MetadataMaxTables), dataSource.MetadataMaxTables != metadataMaxTables),
            (nameof(DataSource.MetadataMaxColumnsPerTable), dataSource.MetadataMaxColumnsPerTable != metadataMaxColumnsPerTable),
            (nameof(DataSource.MetadataLoadTableNamesOnly), dataSource.MetadataLoadTableNamesOnly != metadataLoadTableNamesOnly),
            (nameof(DataSource.MetadataExcludeSchemas), dataSource.MetadataExcludeSchemas != excludeSchemas),
            (nameof(DataSource.MetadataIncludeSchemas), dataSource.MetadataIncludeSchemas != includeSchemas)
        };
        var changedFields = changes
            .Where(x => x.Changed)
            .Select(x => x.Field)
            .ToList();

        var previousEngine = dataSource.DatabaseEngineType;
        var previousName = dataSource.Name;
        dataSource.Name = dataSourceData.Name;
        dataSource.EncryptedConnectionData = encryptionService.Encrypt(connectionString);
        dataSource.DatabaseEngineType = dataSourceData.DatabaseEngineType;
        dataSource.MetadataLoadingEnabled = metadataLoadingEnabled;
        dataSource.MetadataMaxTables = metadataMaxTables;
        dataSource.MetadataMaxColumnsPerTable = metadataMaxColumnsPerTable;
        dataSource.MetadataLoadTableNamesOnly = metadataLoadTableNamesOnly;
        dataSource.MetadataExcludeSchemas = excludeSchemas;
        dataSource.MetadataIncludeSchemas = includeSchemas;

        // The column values sampled from the old target are not the new one's: they go, archived tables' columns included,
        // in the same unit of work as the change. The stored tables and columns stay until the re-scan enqueued below
        // replaces them (their curated descriptions are kept by name).
        if (targetChanged)
        {
            await DatabaseMetadataService.ClearStoredSampleValuesAsync(context, dataSource.Id, cancellationToken);
        }

        var submission = await SubmitUpdateAsync(context, requester, dataSource, previousName, approval, targetChanged, engineChanged, cancellationToken);

        // A new connection (its credentials, host or database) is a credential change; any other edit is an update. The
        // event names the fields that changed and whether the target did, never a value.
        if (connectionChanged || changedFields.Count > 0)
        {
            await securityAudit.StageAsync(
                context,
                new SecurityAuditEntry(connectionChanged ? SecurityAuditActions.CredentialsRotated : SecurityAuditActions.DataSourceUpdated)
                    .On(SecurityAuditResources.DataSource, dataSource.Id)
                    .With(new SecurityAuditDetails()
                        .Add("connectionChanged", connectionChanged)
                        .Add("targetChanged", targetChanged)
                        .AddCodes("changedFields", changedFields)),
                cancellationToken);
        }

        // A submitted request's event names it by the id its save gives it.
        if (submission?.Request != null)
        {
            await securityAudit.SaveWithEventsAsync(context, () => ChangeApprovalAuditEntries.Submitted(submission), cancellationToken);
        }
        else
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        return new SavedUpdate(dataSource, previousEngine, storedConnection, connectionChanged, engineChanged, targetChanged, approval.Held, submission);
    }

    // An API source's endpoints, read from its OpenAPI document and stored as its metadata. A failure is logged and can be
    // retried; the source is saved either way.
    private async Task ImportApiEndpointsAsync(BeaconContext context, DataSource dataSource, CancellationToken cancellationToken)
    {
        try
        {
            var provider = providerFactory.GetProvider(DataSourceType.Api);
            var metadata = await provider.GetMetadataAsync(dataSource, cancellationToken);
            if (metadata.Endpoints?.Count > 0)
            {
                await StoreApiEndpointsAsMetadata(context, dataSource.Id, metadata.Endpoints, cancellationToken);
                catalogSnapshots?.Invalidate(dataSource.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to import API metadata for data source {Id}. Can be retried via metadata refresh.", dataSource.Id);
        }
    }

    // Only a database source declares an engine the server can be asked about; the other types connect to nothing here.
    private Task EnsureDeclaredEngineAsync(
        int? dataSourceId,
        DataSourceType dataSourceType,
        DatabaseEngineType? databaseEngineType,
        string connectionString,
        bool refuseUnreachable,
        CancellationToken cancellationToken)
    {
        return dataSourceType == DataSourceType.Database && databaseEngineType.HasValue
            ? engineVerifier.EnsureDeclaredEngineAsync(dataSourceId, databaseEngineType.Value, connectionString, refuseUnreachable, cancellationToken)
            : Task.CompletedTask;
    }

    // A connection test is refused, never opened, when the connection does not authenticate the server or its connector does
    // not accept the configuration: its message names the option to change, never a value from the connection string.
    private BaseResponse? TransportRefusal(DataSourceType dataSourceType, DatabaseEngineType? databaseEngineType, string connectionString)
    {
        try
        {
            transportPolicy.EnsureNewConnectionAllowed(dataSourceType, databaseEngineType, connectionString);
            providerFactory.GetProvider(dataSourceType).EnsureNewConnectionDataAllowed(connectionString);

            return null;
        }
        catch (Exception ex) when (ex is BeaconException or InvalidOperationException)
        {
            return new BaseResponse
            {
                Success = false,
                Message = $"Connection refused: {ex.Message}"
            };
        }
    }

    // Cached metadata and the relationship graph are rebuilt on their next read; an archived source's reads are refused.
    // A changed source may be refreshed at once: its forced-refresh cooldown starts over.
    private void EvictMetadata(int dataSourceId)
    {
        cache.Remove(DatabaseMetadataService.GetCacheKey(dataSourceId));
        cache.Remove(ForcedMetadataRefreshCooldown.CacheKey(dataSourceId));
        schemaGraphService.Invalidate(dataSourceId);
        catalogSnapshots?.Invalidate(dataSourceId);
    }

    // A source pointed at another target (or whose connection was approved) still has the tables and columns stored before:
    // a metadata re-scan replaces them in the background, when the source loads metadata and Beacon may read it. Until it
    // finishes, the stored schema is shown. The change is saved already, so a failure to enqueue is logged by its type only.
    private async Task EnqueueMetadataRefreshAsync(DataSource dataSource)
    {
        if (dataSource.DataSourceType != DataSourceType.Database
            || dataSource.DatabaseEngineType == null
            || !dataSource.MetadataLoadingEnabled
            || dataSource.AllowMigrationWrites
            || dataSource.DisabledTime != null)
        {
            return;
        }

        try
        {
            var jobId = scheduler == null ? null : await scheduler.EnqueueMetadataRefresh(dataSource.Id);
            if (jobId == null)
            {
                logger.LogWarning(
                    "No metadata refresh was enqueued for data source {DataSourceId} after its connection changed: this host runs no such job, so the schema stored before stays until its metadata is refreshed",
                    dataSource.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The metadata refresh of data source {DataSourceId} could not be enqueued after its connection changed ({ExceptionType}); the schema stored before stays until its metadata is refreshed",
                dataSource.Id,
                ex.GetType().Name);
        }
    }

    // Host-managed data sources belong to the host application (ExposeDbContext): their connection, engine and
    // lifetime are declared in code and re-applied at every startup, so the UI/REST surface must not change them.
    private static void EnsureNotHostManaged(DataSource dataSource)
    {
        if (dataSource.HostManagedKey != null)
        {
            throw new InvalidOperationException(
                $"Data source '{dataSource.Name}' is managed by the host application (ExposeDbContext) and cannot be edited or deleted here.");
        }
    }

    // The stored connection data in plain text, only to compare an update's target with it and to empty its driver pool;
    // null when Beacon cannot read it (encrypted under a key no longer configured), logged by the source's id and engine
    // and the exception's type only, never its message.
    private string? StoredConnection(string? storedEncrypted, int dataSourceId, DatabaseEngineType? engine)
    {
        if (string.IsNullOrEmpty(storedEncrypted))
        {
            return null;
        }

        try
        {
            return encryptionService.Decrypt(storedEncrypted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The stored connection of data source {DataSourceId} ({Engine}) could not be decrypted ({ExceptionType}); it cannot be kept or have its connection pool cleared",
                dataSourceId,
                engine,
                ex.GetType().Name);

            return null;
        }
    }

    private static string? SchemaList(List<string>? schemas)
    {
        return schemas is { Count: > 0 } ? string.Join(",", schemas) : null;
    }

    private static SecurityAuditEntry CreatedEntry(DataSource dataSource, List<int> projectIds)
    {
        return new SecurityAuditEntry(SecurityAuditActions.DataSourceCreated)
            .On(SecurityAuditResources.DataSource, dataSource.Id)
            .With(new SecurityAuditDetails()
                .Add("dataSourceType", dataSource.DataSourceType)
                .AddCode("databaseEngineType", dataSource.DatabaseEngineType?.ToString())
                .Add("projectIds", projectIds));
    }

    // What a save of a source's connection does to its approval, decided before anything is changed: the definition is
    // the connection as the source's inspector reads it, secrets left out (the whole connection data when the inspector
    // gives none). Not governed and not covered, or a definition left as it is on a source that is not governed: nothing.
    // Not covered, and the definition of a governed source changed: it is no longer governed. Covered: the approved hash
    // the source keeps (for one not governed yet, its old definition's; for one whose old connection cannot be read,
    // nothing approved), and whether the source is held because its definition is not the approved one.
    // What saving dataSourceData over dataSource changes, decided before anything is written: refused (400, 403, 404, 409)
    // as the save would be. A change that waits for approval needs a requester Beacon can name; one that applies at once
    // does not.
    private async Task<UpdatePlan> PlanUpdateAsync(DataSource dataSource, DataSourceData dataSourceData, ChangeRequester? requester, CancellationToken cancellationToken)
    {
        EnsureNotHostManaged(dataSource);

        if (dataSource.DataSourceType == DataSourceType.Database && dataSourceData.DatabaseEngineType == null)
        {
            throw new InvalidOperationException("A database data source needs a database engine.");
        }

        // No connection given keeps the stored one, which is never shown and so never sent back; a stored one Beacon cannot
        // read (encrypted under a key no longer configured) cannot be kept.
        var storedConnection = StoredConnection(dataSource.EncryptedConnectionData, dataSource.Id, dataSource.DatabaseEngineType);
        var keepsConnection = string.IsNullOrWhiteSpace(dataSourceData.ConnectionString);
        if (keepsConnection && storedConnection == null)
        {
            throw new InvalidOperationException("The stored connection cannot be read, so it cannot be kept. Enter the connection again.");
        }

        var engineChanged = dataSource.DatabaseEngineType != dataSourceData.DatabaseEngineType;
        if (keepsConnection && engineChanged)
        {
            throw new InvalidOperationException("Changing the engine needs the connection entered again: a stored connection is never read as another engine's.");
        }

        var connectionString = keepsConnection ? storedConnection! : dataSourceData.ConnectionString!;
        var connectionChanged = storedConnection == null || !string.Equals(storedConnection, connectionString, StringComparison.Ordinal);
        if (connectionChanged || engineChanged)
        {
            // A connection kept as it is stays a stored one (warned about when it connects); a new one must authenticate
            // the server.
            transportPolicy.EnsureNewConnectionAllowed(dataSource.DataSourceType, dataSourceData.DatabaseEngineType, connectionString);
            providerFactory.GetProvider(dataSource.DataSourceType).EnsureNewConnectionDataAllowed(connectionString);
        }

        var approval = ApprovalOf(requester, dataSource, storedConnection, dataSourceData.DatabaseEngineType, connectionString);
        if (approval.Held)
        {
            await changeApprovals.DemandNamedAsync(requester!, ConnectionKind, dataSource.Id, cancellationToken);
        }

        // Pointing a source somewhere else is confirmed: the queries, contracts and grounding built on the old target would
        // read the new one.
        var targetChanged = engineChanged
            || (connectionChanged && TargetChanged(dataSource, storedConnection, dataSourceData.DatabaseEngineType, connectionString));
        if (targetChanged && !dataSourceData.ConfirmTargetChange)
        {
            throw new BeaconConflictException(TargetChangeUnconfirmedMessage);
        }

        return new UpdatePlan(storedConnection, connectionString, connectionChanged, engineChanged, targetChanged, approval);
    }

    private ConnectionApproval ApprovalOf(ChangeRequester? requester, DataSource dataSource, string? storedConnection, DatabaseEngineType? engine, string connectionString)
    {
        var approved = dataSource.ApprovedTargetHash;
        if (requester == null && approved == null)
        {
            return ConnectionApproval.Nothing;
        }

        var type = dataSource.DataSourceType;
        var previousEngine = dataSource.DatabaseEngineType;
        var previousVerdict = storedConnection == null ? null : transportPolicy.Inspect(type, previousEngine, storedConnection);
        var previousCanonical = previousVerdict == null
            ? null
            : DataSourceApprovalDefinition.Canonical(type, previousEngine, previousVerdict.Definition, storedConnection!);
        var verdict = transportPolicy.Inspect(type, engine, connectionString);
        var canonical = DataSourceApprovalDefinition.Canonical(type, engine, verdict.Definition, connectionString);
        var unchanged = string.Equals(previousCanonical, canonical, StringComparison.Ordinal);
        if (unchanged && (requester == null || approved == null))
        {
            return ConnectionApproval.Nothing;
        }

        if (requester == null)
        {
            return ConnectionApproval.Ungovern;
        }

        var hash = definitionHasher.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, canonical);
        var previousHash = previousCanonical == null ? null : definitionHasher.Hash(ConnectionDefinitionPurpose.DataSourceDefinition, previousCanonical);

        // An approved definition the source holds, before or after, is recorded under the current key.
        var approvedAfter = approved switch
        {
            null => previousHash ?? DataSourceApprovalDefinition.Held,
            _ when DataSourceApprovalDefinition.IsHeld(approved) => approved,
            _ when definitionHasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, approved, canonical) => hash,
            _ when previousCanonical != null && definitionHasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, approved, previousCanonical) => previousHash!,
            _ => approved
        };

        return new ConnectionApproval(ApprovalStep.Submit, approvedAfter, canonical, hash, previousHash, verdict, previousVerdict, connectionString, storedConnection, previousEngine);
    }

    // A created source's request, after its first save gave it an id.
    private async Task<ChangeSubmission> SubmitNewAsync(
        BeaconContext context,
        ChangeRequester requester,
        DataSource dataSource,
        string connectionString,
        CancellationToken cancellationToken)
    {
        var verdict = transportPolicy.Inspect(dataSource.DataSourceType, dataSource.DatabaseEngineType, connectionString);
        var hash = DataSourceApprovalDefinition.Hash(definitionHasher, dataSource.DataSourceType, dataSource.DatabaseEngineType, verdict.Definition, connectionString);
        var proposed = DataSourceApprovalDefinition.View(
            definitionHasher,
            dataSource.Name,
            dataSource.DataSourceType,
            dataSource.DatabaseEngineType,
            verdict.Target,
            verdict.Definition,
            connectionString,
            previous: null,
            authenticatesServer: verdict.Verified,
            databaseReadOnlyEnforced: DataSourceReadOnlyEnforcement.Of(providerFactory, dataSource.DataSourceType, dataSource.DatabaseEngineType) == true,
            isTargetChange: false);
        var proposal = new ChangeProposal(
            ConnectionKind,
            dataSource.Id,
            ResourceName(dataSource.Name),
            PreviousDefinitionHash: null,
            hash,
            NeedsApproval: true,
            proposed,
            Previous: null,
            "Data source created");

        return await changeApprovals.SubmitAsync(context, requester, proposal, cancellationToken);
    }

    // Applies what ApprovalOf decided to the tracked source (which already holds the saved values) in the save's unit of
    // work: the approved hash it keeps, the request its definition needs, and the pending requests that became moot.
    private async Task<ChangeSubmission?> SubmitUpdateAsync(
        BeaconContext context,
        ChangeRequester? requester,
        DataSource dataSource,
        string previousName,
        ConnectionApproval approval,
        bool targetChanged,
        bool engineChanged,
        CancellationToken cancellationToken)
    {
        if (approval.Step == ApprovalStep.None)
        {
            return null;
        }

        if (approval.Step == ApprovalStep.Ungovern || requester == null)
        {
            // Nobody reviews it any more: what it waited for can no longer be approved.
            dataSource.ApprovedTargetHash = null;

            return await changeApprovals.SupersedeOrphanedAsync(context, ConnectionKind, dataSource.Id, null, cancellationToken);
        }

        dataSource.ApprovedTargetHash = approval.ApprovedAfter;

        // A held source has no approved definition to compare with: the change is made against the one its pending request
        // proposes (none when nothing waits, as after a rejection: the save submits it again).
        var basedOn = DataSourceApprovalDefinition.IsHeld(approval.ApprovedAfter)
            ? await PendingProposalAsync(context, dataSource.Id, approval, cancellationToken)
            : approval.PreviousHash;
        var type = dataSource.DataSourceType;
        var verdict = approval.Verdict!;
        var previousDefinition = approval.PreviousVerdict?.Definition;
        var proposed = DataSourceApprovalDefinition.View(
            definitionHasher,
            dataSource.Name,
            type,
            dataSource.DatabaseEngineType,
            verdict.Target,
            verdict.Definition,
            approval.ConnectionData!,
            previousDefinition,
            verdict.Verified,
            DataSourceReadOnlyEnforcement.Of(providerFactory, type, dataSource.DatabaseEngineType) == true,
            targetChanged);
        var previous = approval.PreviousVerdict == null
            ? null
            : DataSourceApprovalDefinition.View(
                definitionHasher,
                previousName,
                type,
                approval.PreviousEngine,
                approval.PreviousVerdict.Target,
                previousDefinition,
                approval.StoredConnection!,
                previousDefinition,
                approval.PreviousVerdict.Verified,
                DataSourceReadOnlyEnforcement.Of(providerFactory, type, approval.PreviousEngine) == true,
                isTargetChange: false);

        // A pending request proposes the definition the source now holds when its hash verifies under the current key or a
        // previous one, as the connection resolver verifies it.
        var canonical = approval.Canonical!;
        var proposal = new ChangeProposal(
            ConnectionKind,
            dataSource.Id,
            ResourceName(dataSource.Name),
            basedOn,
            approval.Hash!,
            NeedsApproval: approval.Held,
            proposed,
            previous,
            SummaryOf(proposed, engineChanged),
            MatchesDefinition: x => definitionHasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, x, canonical));

        return await changeApprovals.SubmitAsync(context, requester, proposal, cancellationToken);
    }

    // The definition hash the latest pending request of a held source proposes: the new one when it proposes exactly the
    // definition the source now holds (under the current key or a previous one), null when none waits.
    private async Task<string?> PendingProposalAsync(BeaconContext context, int dataSourceId, ConnectionApproval approval, CancellationToken cancellationToken)
    {
        var pending = await context.ChangeApprovalRequests
            .Where(x => x.Kind == ConnectionKind)
            .Where(x => x.ResourceId == dataSourceId)
            .Where(x => x.Status == ApprovalStatus.Pending)
            .OrderByDescending(x => x.Id)
            .Select(x => x.DefinitionHash)
            .FirstOrDefaultAsync(cancellationToken);

        return pending != null && definitionHasher.Matches(ConnectionDefinitionPurpose.DataSourceDefinition, pending, approval.Canonical!)
            ? approval.Hash
            : pending;
    }

    // What changed, by setting name only (never a value): a connection its connector cannot read setting by setting is
    // compared by its fingerprint.
    private static string SummaryOf(DataSourceConnectionView proposed, bool engineChanged)
    {
        if (proposed.SecretOnlyDefinition)
        {
            return "Connection changed (compare it by its fingerprint)";
        }

        var names = (engineChanged ? ["Engine"] : Array.Empty<string>())
            .Concat(proposed.ChangedSettings)
            .ToList();
        if (names.Count == 0)
        {
            return "Connection submitted again";
        }

        var summary = $"{string.Join(", ", names)} changed";

        return summary.Length <= MaxChangeSummaryLength ? summary : $"{names.Count} connection settings changed";
    }

    private static string ResourceName(string name)
    {
        return name.Length <= MaxResourceNameLength ? name : name[..MaxResourceNameLength];
    }

    // Whether the new connection goes to another host, port or database than the stored one, as the source's transport
    // inspector reads them. A target that cannot be read on either side counts as changed: the change must be confirmed.
    private bool TargetChanged(DataSource dataSource, string? storedConnection, DatabaseEngineType? engine, string connectionString)
    {
        if (storedConnection == null)
        {
            return true;
        }

        var before = transportPolicy.Inspect(dataSource.DataSourceType, dataSource.DatabaseEngineType, storedConnection).Target;
        var after = transportPolicy.Inspect(dataSource.DataSourceType, engine, connectionString).Target;
        if (before == null || after == null)
        {
            return true;
        }

        return !string.Equals(before.Host, after.Host, StringComparison.OrdinalIgnoreCase)
            || before.Port != after.Port
            || !string.Equals(before.Database, after.Database, StringComparison.Ordinal);
    }

    // Empties the driver's pools of the old connection string (PostgreSQL, MySQL with its read-only pool, SQL Server,
    // Synapse; Snowflake and the JSON-configured connectors keep no pool Beacon can empty, which is logged for a database
    // engine). The change is saved already, so a failure is logged by its type only, never its message, which can quote
    // the connection string.
    private void ClearConnectionPool(int? dataSourceId, DataSourceType dataSourceType, DatabaseEngineType? engine, string? connectionString)
    {
        if (dataSourceType != DataSourceType.Database || engine == null || connectionString == null)
        {
            return;
        }

        try
        {
            if (!DbConnectionFactory.ClearPool(engine.Value, connectionString))
            {
                logger.LogWarning(
                    "The connection pool of data source {DataSourceId} was not cleared: no pool clearer is registered for {Engine}",
                    dataSourceId,
                    engine.Value);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The connection pool of data source {DataSourceId} could not be cleared ({ExceptionType}); its pooled connections close when they expire",
                dataSourceId,
                ex.GetType().Name);
        }
    }

    private enum ApprovalStep
    {
        None,
        Ungovern,
        Submit
    }

    // What ApprovalOf decided, with the definitions it read, for SubmitUpdateAsync to apply.
    private sealed record ConnectionApproval(
        ApprovalStep Step,
        string? ApprovedAfter = null,
        string? Canonical = null,
        string? Hash = null,
        string? PreviousHash = null,
        DataSourceTransportVerdict? Verdict = null,
        DataSourceTransportVerdict? PreviousVerdict = null,
        string? ConnectionData = null,
        string? StoredConnection = null,
        DatabaseEngineType? PreviousEngine = null)
    {
        public static readonly ConnectionApproval Nothing = new(ApprovalStep.None);

        public static readonly ConnectionApproval Ungovern = new(ApprovalStep.Ungovern);

        // Under the workflow, the source connects to nothing until a decision: its definition is not the approved one.
        public bool Held => Step == ApprovalStep.Submit && !string.Equals(ApprovedAfter, Hash, StringComparison.Ordinal);

        // Holds connection strings and definitions: printed by its step only.
        public override string ToString() => $"{nameof(ConnectionApproval)} {{ Step = {Step} }}";
    }

    // What an update changes, planned before it is written. Holds connection strings: printed without its values.
    private sealed record UpdatePlan(
        string? StoredConnection,
        string ConnectionString,
        bool ConnectionChanged,
        bool EngineChanged,
        bool TargetChanged,
        ConnectionApproval Approval)
    {
        // A new connection or engine is checked against the server: a connection kept as it is connects to nothing, nor
        // does a disabled source (§1.1) — it is checked when its connection or engine next changes while enabled — nor a
        // source held until its change is approved, which is checked at its approval.
        public bool ChecksServer(DataSource dataSource) => (ConnectionChanged || EngineChanged) && dataSource.DisabledTime == null && !Approval.Held;

        public override string ToString() => nameof(UpdatePlan);
    }

    // What an update saved, for the work done after its commit.
    private sealed record SavedUpdate(
        DataSource DataSource,
        DatabaseEngineType? PreviousEngine,
        string? StoredConnection,
        bool ConnectionChanged,
        bool EngineChanged,
        bool TargetChanged,
        bool Held,
        ChangeSubmission? Submission);
}
