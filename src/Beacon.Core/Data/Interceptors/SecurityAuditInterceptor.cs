using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Helpers;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Data.Interceptors;

/// <summary>
/// Keeps the security audit trail append-only and sealed: a save that would change or delete a tracked
/// <see cref="SecurityAuditEvent"/> is refused, and every added event gets its key id and HMAC computed over the fields it
/// is saved with (<see cref="SecurityAuditIntegrity"/>). Once an event is durable — its save committed, or the
/// transaction it was saved in — a copy goes to the <c>Beacon.Audit</c> log stream (<see cref="SecurityAuditLogEvents.Recorded"/>);
/// an event whose save or transaction failed is never copied. The MCP audit is kept the same way: a save that would change
/// or delete a tracked <see cref="McpAuditLog"/> or <see cref="McpAuditStatement"/>, or add a statement to a row saved
/// before, is refused, and every added row is sealed with its statements (<see cref="McpAuditSeal"/>). So are the execution
/// log (<see cref="ManualQueryExecutionLog"/>) and the settings history (<see cref="AppSettingHistory"/>): a change or delete
/// of a tracked row is refused and every added row is sealed (<see cref="AuditTableSeal"/>). Bulk deletes
/// (<c>ExecuteDelete</c>) do not pass through here: only the retention purges issue one. Registered on both providers
/// after the content retention interceptor, so a row is sealed with what that lock leaves of it.
/// </summary>
public sealed class SecurityAuditInterceptor(SecurityAuditIntegrity integrity, ILoggerFactory loggerFactory)
    : SaveChangesInterceptor, IDbTransactionInterceptor
{
    internal const string AppendOnlyMessage = "Security audit events are append-only: they cannot be changed or deleted.";
    internal const string McpAppendOnlyMessage = "MCP audit rows are append-only: a row and its statements cannot be changed or deleted, and a statement can only be saved with its row.";
    internal const string AuditTableAppendOnlyMessage = "Execution log and settings history rows are append-only: they cannot be changed or deleted.";

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);
    private readonly ConditionalWeakTable<DbContext, PendingEvents> _pending = new();
    private readonly AuditTableSealSchema? _sealSchema;

    /// <summary>
    /// The interceptor as Beacon registers it: it also reads whether the sealed audit tables have their seal columns
    /// (<paramref name="sealSchema"/>), and writes their rows without the seal while they do not.
    /// </summary>
    internal SecurityAuditInterceptor(SecurityAuditIntegrity integrity, ILoggerFactory loggerFactory, AuditTableSealSchema sealSchema)
        : this(integrity, loggerFactory)
    {
        _sealSchema = sealSchema;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Seal(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Seal(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Saved(eventData.Context);
        WriteUnsealed(eventData.Context);

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);
        await WriteUnsealedAsync(eventData.Context, cancellationToken);

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Discard(eventData.Context, saving: true);
        RestoreUnsealed(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, saving: true);
        RestoreUnsealed(eventData.Context);

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    public DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        Discard(eventData.Context, saving: false);

        return result;
    }

    public ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection,
        TransactionEndEventData eventData,
        DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, saving: false);

        return ValueTask.FromResult(result);
    }

    public void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Committed(eventData.Context);
    }

    public Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Committed(eventData.Context);

        return Task.CompletedTask;
    }

    public void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Discard(eventData.Context, saving: false);
    }

    public Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, saving: false);

        return Task.CompletedTask;
    }

    public void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        Discard(eventData.Context, saving: false);
    }

    public Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, saving: false);

        return Task.CompletedTask;
    }

    // The steps of a save and of a transaction, internal for tests that drive a change tracker without a database.

    /// <summary>Refuses changes to stored events and seals the added ones.</summary>
    internal void Seal(DbContext? context)
    {
        if (context == null)
        {
            return;
        }

        var saving = new List<SecurityAuditEvent>();
        foreach (var entry in context.ChangeTracker.Entries<SecurityAuditEvent>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(AppendOnlyMessage);
            }

            if (entry.State == EntityState.Added)
            {
                integrity.Seal(entry.Entity);
                saving.Add(entry.Entity);
            }
        }

        _pending.GetOrCreateValue(context).Saving = saving;
        SealMcpAudit(context);
        SealAuditTable(context, AuditTableStores.ExecutionLog);
        SealAuditTable(context, AuditTableStores.SettingHistory);
    }

    // The rows of the sealed audit tables a save kept out of EF's own insert (their table lacks the seal columns), written
    // once the save succeeded, in the transaction it ran in when there is one.
    private void WriteUnsealed(DbContext? context)
    {
        foreach (var (sql, parameters) in TakeUnsealed(context))
        {
            context!.Database.ExecuteSqlRaw(sql, parameters);
        }
    }

    private async Task WriteUnsealedAsync(DbContext? context, CancellationToken cancellationToken)
    {
        foreach (var (sql, parameters) in TakeUnsealed(context))
        {
            await context!.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
        }
    }

    private List<(string Sql, object[] Parameters)> TakeUnsealed(DbContext? context)
    {
        if (context == null || !_pending.TryGetValue(context, out var pending) || pending.Unsealed.Count == 0)
        {
            return [];
        }

        var inserts = pending.Unsealed
            .Select(x => AuditTableSealSchema.InsertWithoutSeal(context, x))
            .ToList();
        pending.Unsealed.Clear();

        return inserts;
    }

    // A failed save puts the rows it kept out back into the change tracker, as the caller added them, so a retried save
    // writes them.
    private void RestoreUnsealed(DbContext? context)
    {
        if (context == null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        foreach (var row in pending.Unsealed)
        {
            context.Add(row);
        }

        pending.Unsealed.Clear();
    }

    internal void Saved(DbContext? context)
    {
        if (context == null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        var saved = pending.Saving;
        pending.Saving = [];
        if (context.Database.CurrentTransaction == null)
        {
            Emit(saved);

            return;
        }

        pending.InTransaction.AddRange(saved);
    }

    internal void Committed(DbContext? context)
    {
        if (context == null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        var committed = pending.InTransaction.ToList();
        pending.InTransaction.Clear();
        Emit(committed);
    }

    internal void Discard(DbContext? context, bool saving)
    {
        if (context == null || !_pending.TryGetValue(context, out var pending))
        {
            return;
        }

        if (saving)
        {
            pending.Saving = [];

            return;
        }

        pending.InTransaction.Clear();
    }

    // The MCP audit rows added by this save, sealed with the statements they hold; anything else that touches the MCP
    // audit through the change tracker is refused.
    private void SealMcpAudit(DbContext context)
    {
        var sealedRows = new HashSet<McpAuditLog>(ReferenceEqualityComparer.Instance);
        foreach (var entry in context.ChangeTracker.Entries<McpAuditLog>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(McpAppendOnlyMessage);
            }

            if (entry.State == EntityState.Added)
            {
                McpAuditSeal.Seal(entry.Entity, integrity);
                sealedRows.Add(entry.Entity);
            }
        }

        foreach (var entry in context.ChangeTracker.Entries<McpAuditStatement>())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(McpAppendOnlyMessage);
            }

            if (entry.State == EntityState.Added && (entry.Entity.AuditLog == null || !sealedRows.Contains(entry.Entity.AuditLog)))
            {
                throw new InvalidOperationException(McpAppendOnlyMessage);
            }
        }
    }

    // The rows of a sealed audit table added by this save, sealed; a change or delete of one through the change tracker is
    // refused.
    private void SealAuditTable<T>(DbContext context, AuditTableStore<T> store)
        where T : BaseEntity, ISealedAuditRow
    {
        foreach (var entry in context.ChangeTracker.Entries<T>().ToList())
        {
            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(AuditTableAppendOnlyMessage);
            }

            if (entry.State != EntityState.Added)
            {
                continue;
            }

            if (_sealSchema != null && _sealSchema.SealColumnsMissing(context, typeof(T)))
            {
                // No place for the seal yet: the row leaves EF's insert and is written without it after the save.
                entry.State = EntityState.Detached;
                _pending.GetOrCreateValue(context).Unsealed.Add(entry.Entity);

                continue;
            }

            AuditTableSeal.Seal(entry.Entity, store, integrity);
        }
    }

    // The copy for the log stream must never fail the save it follows.
    private void Emit(IReadOnlyList<SecurityAuditEvent> events)
    {
        foreach (var auditEvent in events)
        {
            try
            {
                SecurityAuditLogEvents.LogPersisted(_auditLogger, auditEvent);
            }
            catch (Exception ex)
            {
                _auditLogger.LogWarning("Could not copy security audit event {EventGuid} to the log stream ({Failure})", auditEvent.EventGuid, FailureCodes.Describe(ex));
            }
        }
    }

    private sealed class PendingEvents
    {
        public List<SecurityAuditEvent> Saving { get; set; } = [];

        public List<SecurityAuditEvent> InTransaction { get; } = [];

        public List<object> Unsealed { get; } = [];
    }
}
