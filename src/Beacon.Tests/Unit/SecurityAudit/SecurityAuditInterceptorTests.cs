using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Infrastructure.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Interceptors;
using Beacon.Core.PostgreSql;
using Beacon.Core.SecurityAudit;
using Beacon.Core.SqlServer;
using Beacon.Core.Worker;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.SecurityAudit;

/// <summary>
/// The interceptor that keeps the security audit trail append-only and sealed, driven on change trackers without a
/// database and through EF's own save and transactions over a connection that never opens: a save that would change or delete an
/// event is refused before any statement is sent, every added event is sealed, and the copy on the <c>Beacon.Audit</c> log
/// stream (9104) goes out only once the event is durable — at once without a transaction, at the commit inside one, never
/// after a rollback (not even when a later transaction commits) or a failed save. A staged event and the change it records
/// go to the database in the same save, so a failed save writes neither. Both providers attach the interceptor.
/// </summary>
[TestFixture]
public class SecurityAuditInterceptorTests
{
    [TestCase(EntityState.Modified)]
    [TestCase(EntityState.Deleted)]
    public void ASaveThatWouldChangeOrDeleteAnEvent_IsRefused(EntityState state)
    {
        using var context = NpgsqlTestContext.Create();
        var auditEvent = Event();
        auditEvent.Id = 5;
        context.Attach(auditEvent);
        context.Entry(auditEvent).State = state;

        var act = () => Interceptor(new LogRecorder()).Seal(context);

        act.Should().Throw<InvalidOperationException>().WithMessage("*append-only*");
    }

    [Test]
    public void AnAddedEvent_IsSealedWithTheCurrentKey_OverTheFieldsItIsSavedWith()
    {
        using var context = NpgsqlTestContext.Create();
        var auditEvent = Event();
        context.SecurityAuditEvents.Add(auditEvent);
        auditEvent.ResourceId = "42";

        Interceptor(new LogRecorder()).Seal(context);

        auditEvent.KeyId.Should().Be(SecurityAuditTestKit.Integrity.CurrentKeyId);
        SecurityAuditTestKit.Integrity.Verify(auditEvent).Should().Be(SecurityAuditVerdict.Intact, "the HMAC covers the value saved, not the staged one");
    }

    [Test]
    public void WithoutATransaction_TheCopyGoesToTheLogStream_OnceSaved()
    {
        var logs = new LogRecorder();
        var interceptor = Interceptor(logs);
        using var context = NpgsqlTestContext.Create();
        var auditEvent = Event();
        context.SecurityAuditEvents.Add(auditEvent);

        interceptor.Seal(context);
        Copies(logs).Should().BeEmpty("nothing is copied before the save completed");
        interceptor.Saved(context);

        var copy = Copies(logs).Should().ContainSingle().Subject;
        copy.Category.Should().Be("Beacon.Audit");
        copy.Message.Should().Contain(SecurityAuditActions.UserCreated).And.Contain(auditEvent.EventGuid.ToString()).And.Contain("ResourceId=7");
    }

    [Test]
    public async Task InATransaction_TheCopyWaitsForTheCommit_AndARollbackDropsIt()
    {
        var logs = new LogRecorder();
        var interceptor = Interceptor(logs);
        var capture = new SqlCapture();
        await using var context = await capture.Factory().CreateDbContextAsync();

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.SecurityAuditEvents.Add(Event());
            interceptor.Seal(context);
            interceptor.Saved(context);
            Copies(logs).Should().BeEmpty("the transaction has not committed");
            interceptor.Committed(context);
        }

        Copies(logs).Should().ContainSingle();

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.SecurityAuditEvents.Add(Event());
            interceptor.Seal(context);
            interceptor.Saved(context);
            interceptor.Discard(context, saving: false);
            interceptor.Committed(context);
        }

        Copies(logs).Should().ContainSingle("a rolled-back event is never copied");
    }

    [Test]
    public void AFailedSave_IsNeverCopied()
    {
        var logs = new LogRecorder();
        var interceptor = Interceptor(logs);
        using var context = NpgsqlTestContext.Create();
        context.SecurityAuditEvents.Add(Event());

        interceptor.Seal(context);
        interceptor.Discard(context, saving: true);
        interceptor.Saved(context);

        Copies(logs).Should().BeEmpty();
    }

    [Test]
    public async Task AStagedEvent_GoesInTheSameSaveAsItsChange_AndAFailedSaveWritesNeitherNorCopiesIt()
    {
        var logs = new LogRecorder();
        var capture = new SqlCapture().ThenThrow(new DbUpdateException("the write failed"));
        var factory = capture.Factory(Interceptor(logs));
        var recorder = new Core.SecurityAudit.SecurityAuditRecorder(
            factory,
            TestProjectAccess.Actors(3, "ext-ada"),
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            LoggerFactory.Create(x => x.AddProvider(logs)),
            TestRequestPrincipal.OffRequest());
        await using var context = await factory.CreateDbContextAsync();

        context.Projects.Add(new Project { Name = "Finance" });
        await recorder.StageAsync(context, new SecurityAuditEntry(SecurityAuditActions.ProjectCreated).On(SecurityAuditResources.Project, 1), CancellationToken.None);
        var act = () => context.SaveChangesAsync();

        var thrown = await act.Should().ThrowAsync<Exception>();
        thrown.Which.ToString().Should().Contain("the write failed", "the scripted write failure is what fails the save");
        var sql = string.Join("\n", capture.Commands);
        sql.Should().Contain("INSERT INTO beacon.projects").And.Contain("INSERT INTO beacon.security_audit_events");
        capture.Commits.Should().Be(0, "the change and its event commit together or not at all");
        Copies(logs).Should().BeEmpty();
    }

    // Through EF's own save, as every caller reaches the interceptor: a changed or removed event is refused before any
    // statement is sent.
    [TestCase(EntityState.Modified, false)]
    [TestCase(EntityState.Modified, true)]
    [TestCase(EntityState.Deleted, false)]
    [TestCase(EntityState.Deleted, true)]
    public async Task ASaveThatWouldChangeOrDeleteAStoredEvent_IsRefused_AndSendsNoStatement(EntityState state, bool useAsync)
    {
        var capture = new SqlCapture();
        await using var context = await capture.Factory(Interceptor(new LogRecorder())).CreateDbContextAsync();
        var auditEvent = Event();
        auditEvent.Id = 5;
        context.Attach(auditEvent);
        if (state == EntityState.Deleted)
        {
            context.SecurityAuditEvents.Remove(auditEvent);
        }
        else
        {
            // Changed on the tracked object, as an edit would be: the save finds the change itself.
            auditEvent.Outcome = SecurityAuditOutcomes.Failure;
        }

        var act = async () => _ = useAsync ? await context.SaveChangesAsync() : context.SaveChanges();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*append-only*");
        capture.Commands.Should().BeEmpty("the save is refused before any statement reaches the database");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ThroughTheSave_WithoutATransaction_AnEventIsSealed_AndCopiedOnceSaved(bool useAsync)
    {
        var logs = new LogRecorder();
        var capture = new SqlCapture();
        await using var context = await capture.Factory(Interceptor(logs), new AcceptedSaves()).CreateDbContextAsync();
        var auditEvent = Event();
        auditEvent.Hmac = string.Empty;
        context.SecurityAuditEvents.Add(auditEvent);

        _ = useAsync ? await context.SaveChangesAsync() : context.SaveChanges();

        capture.TransactionIsolationLevels.Should().BeEmpty("the save ran without a transaction");
        SecurityAuditTestKit.Integrity.Verify(auditEvent).Should().Be(SecurityAuditVerdict.Intact, "the save sealed the event");
        Copies(logs).Should().ContainSingle()
            .Which.Message.Should().Contain(auditEvent.EventGuid.ToString()).And.Contain("Persisted=True");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ThroughTheSave_InATransaction_TheCopyGoesOutOnlyAtTheCommit(bool useAsync)
    {
        var logs = new LogRecorder();
        var capture = new SqlCapture();
        await using var context = await capture.Factory(Interceptor(logs), new AcceptedSaves()).CreateDbContextAsync();
        var auditEvent = Event();

        await using var transaction = useAsync ? await context.Database.BeginTransactionAsync() : context.Database.BeginTransaction();
        context.SecurityAuditEvents.Add(auditEvent);
        _ = useAsync ? await context.SaveChangesAsync() : context.SaveChanges();

        Copies(logs).Should().BeEmpty("the event is saved but its transaction has not committed");
        if (useAsync)
        {
            await transaction.CommitAsync();
        }
        else
        {
            transaction.Commit();
        }

        capture.Commits.Should().Be(1);
        Copies(logs).Should().ContainSingle().Which.Message.Should().Contain(auditEvent.EventGuid.ToString());
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ThroughTheSave_ARolledBackEvent_IsNeverCopied_NotEvenAtALaterCommit(bool useAsync)
    {
        var logs = new LogRecorder();
        var capture = new SqlCapture();
        await using var context = await capture.Factory(Interceptor(logs), new AcceptedSaves()).CreateDbContextAsync();
        var rolledBack = Event();
        var committed = Event();

        await using (var transaction = useAsync ? await context.Database.BeginTransactionAsync() : context.Database.BeginTransaction())
        {
            context.SecurityAuditEvents.Add(rolledBack);
            _ = useAsync ? await context.SaveChangesAsync() : context.SaveChanges();
            if (useAsync)
            {
                await transaction.RollbackAsync();
            }
            else
            {
                transaction.Rollback();
            }
        }

        context.ChangeTracker.Clear();
        await using (var transaction = useAsync ? await context.Database.BeginTransactionAsync() : context.Database.BeginTransaction())
        {
            context.SecurityAuditEvents.Add(committed);
            _ = useAsync ? await context.SaveChangesAsync() : context.SaveChanges();
            if (useAsync)
            {
                await transaction.CommitAsync();
            }
            else
            {
                transaction.Commit();
            }
        }

        capture.Commits.Should().Be(1, "only the second transaction committed");
        Copies(logs).Should().ContainSingle("the rolled-back event is never copied")
            .Which.Message.Should().Contain(committed.EventGuid.ToString()).And.NotContain(rolledBack.EventGuid.ToString());
    }

    [TestCase(false)]
    [TestCase(true)]
    public void BothProviders_AttachTheInterceptor(bool sqlServer)
    {
        using var provider = BuildProvider(sqlServer);
        var interceptor = provider.GetRequiredService<SecurityAuditInterceptor>();
        using var context = provider.GetRequiredService<IDbContextFactory<BeaconContext>>().CreateDbContext();

        var attached = context.GetService<IDbContextOptions>()
            .FindExtension<CoreOptionsExtension>()!
            .Interceptors ?? [];

        attached.Should().Contain(interceptor);
    }

    private static SecurityAuditInterceptor Interceptor(LogRecorder logs)
    {
        return new SecurityAuditInterceptor(SecurityAuditTestKit.Integrity, LoggerFactory.Create(x => x.AddProvider(logs)));
    }

    private static List<RecordedLog> Copies(LogRecorder logs)
    {
        return logs.Entries
            .Where(x => x.EventId.Id == SecurityAuditLogEvents.Recorded.Id)
            .ToList();
    }

    private static SecurityAuditEvent Event()
    {
        return Core.SecurityAudit.SecurityAuditRecorder.ToEvent(
            new SecurityAuditEntry(SecurityAuditActions.UserCreated).On(SecurityAuditResources.User, 7),
            SecurityAuditActor.OfUser(3),
            DateTime.UtcNow,
            null,
            SecurityAuditTestKit.Integrity);
    }

    private static ServiceProvider BuildProvider(bool sqlServer)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = TestEncryptionKey.Value })
            .Build();
        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        if (sqlServer)
        {
            builder.UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True");
        }
        else
        {
            builder.UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");
        }

        return services.BuildServiceProvider();
    }

    // Stands in for a database that accepts every save: the save's interceptors run as for a real one, the audit interceptor
    // before this one (it is added after it), and no statement is sent.
    private sealed class AcceptedSaves : SaveChangesInterceptor
    {
        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            return InterceptionResult<int>.SuppressWithResult(Added(eventData));
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(Added(eventData)));
        }

        private static int Added(DbContextEventData eventData)
        {
            return eventData.Context!.ChangeTracker
                .Entries()
                .Count(x => x.State == EntityState.Added);
        }
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
