using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Data.Interceptors;

/// <summary>
/// Keeps the security audit trail append-only and sealed: a save that would change or delete a tracked
/// <see cref="SecurityAuditEvent"/> is refused, and every added event gets its key id and HMAC computed over the fields it
/// is saved with (<see cref="SecurityAuditIntegrity"/>). Once an event is durable — its save committed, or the
/// transaction it was saved in — a copy goes to the <c>Beacon.Audit</c> log stream (<see cref="SecurityAuditLogEvents.Recorded"/>);
/// an event whose save or transaction failed is never copied. Bulk deletes (<c>ExecuteDelete</c>) do not pass through
/// here: only the retention purge issues one. Registered on both providers beside the content retention interceptor.
/// </summary>
public sealed class SecurityAuditInterceptor(SecurityAuditIntegrity integrity, ILoggerFactory loggerFactory)
    : SaveChangesInterceptor, IDbTransactionInterceptor
{
    internal const string AppendOnlyMessage = "Security audit events are append-only: they cannot be changed or deleted.";

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);
    private readonly ConditionalWeakTable<DbContext, PendingEvents> _pending = new();

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

        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Saved(eventData.Context);

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Discard(eventData.Context, saving: true);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Discard(eventData.Context, saving: true);

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
    }
}
