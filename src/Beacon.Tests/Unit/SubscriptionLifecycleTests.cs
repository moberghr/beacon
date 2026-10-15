using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Models;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="SubscriptionService"/> saves a subscription before the host scheduler registers its recurring job
/// (the two stores can't share a transaction). A failed schedule must archive the row again, so the caller's error
/// never leaves an active-looking subscription that never runs — on create and on reactivate. Reactivation restores
/// only the parameters archived together with the subscription. Contexts are async-queryable doubles (§4.7).
/// </summary>
[TestFixture]
public class SubscriptionLifecycleTests
{
    private static readonly DateTime ArchivedAt = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private Mock<IBeaconScheduler> _scheduler = null!;

    [SetUp]
    public void SetUp()
    {
        _scheduler = new Mock<IBeaconScheduler>();
    }

    [Test]
    public async Task CreateSubscription_WhenSchedulingSucceeds_LeavesSubscriptionActive()
    {
        var query = new Query { Id = 3, Name = "Overdue invoices" };
        var context = new SubscriptionContext([], [query]);

        var result = await BuildService(context).CreateSubscription(NewSubscriptionData(query.Id), CancellationToken.None);

        result.Success.Should().BeTrue();
        context.Added.Should().ContainSingle()
            .Which.ArchivedTime.Should().BeNull();
        _scheduler.Verify(x => x.AddOrUpdate(It.IsAny<int>(), query.Name, "0 8 * * *"), Times.Once);
    }

    [Test]
    public async Task CreateSubscription_RecordsTheCallerAsItsOwner()
    {
        var query = new Query { Id = 3, Name = "Overdue invoices" };
        var context = new SubscriptionContext([], [query]);

        await BuildService(context).CreateSubscription(NewSubscriptionData(query.Id), CancellationToken.None);

        context.Added.Should().ContainSingle().Which.OwnerUserId.Should().Be("ext-ada");
    }

    // Every statement stops at Beacon:Query:StatementTimeoutSeconds, so a subscription's own timeout above it is refused
    // on create and on update instead of being cut to the limit unseen; one at the limit is kept.
    [Test]
    public async Task ATimeoutAboveTheStatementLimit_IsRefused_OnCreateAndUpdate()
    {
        var query = new Query { Id = 3, Name = "Overdue invoices" };
        var context = new SubscriptionContext([], [query]);
        var service = BuildService(context, queryOptions: new QueryExecutionOptions { StatementTimeoutSeconds = 60 });
        var tooLong = NewSubscriptionData(query.Id);
        tooLong.TimeoutSeconds = 61;
        var atTheLimit = NewSubscriptionData(query.Id);
        atTheLimit.TimeoutSeconds = 60;

        var create = () => service.CreateSubscription(tooLong, CancellationToken.None);
        var update = () => service.UpdateSubscription(tooLong, CancellationToken.None);

        await create.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at most 60 seconds*Beacon:Query:StatementTimeoutSeconds*");
        await update.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at most 60 seconds*");
        context.Added.Should().BeEmpty();
        (await service.CreateSubscription(atTheLimit, CancellationToken.None)).Success.Should().BeTrue();
        context.Added.Should().ContainSingle().Which.TimeoutSeconds.Should().Be(60);
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task CreateSubscription_ByACallerWithoutAStoredUser_NeedsAHostWithoutUserManagement(bool userManagement, bool created)
    {
        var query = new Query { Id = 3, Name = "Overdue invoices" };
        var context = new SubscriptionContext([], [query]);
        var hostSession = new Mock<IBeaconActorAccessor>();
        hostSession
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconActor("host-user", false));
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = userManagement;

        var act = () => BuildService(context, hostSession.Object, configuration).CreateSubscription(NewSubscriptionData(query.Id), CancellationToken.None);

        if (created)
        {
            await act.Should().NotThrowAsync();
            context.Added.Should().ContainSingle().Which.OwnerUserId.Should().Be("host-user");
        }
        else
        {
            await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*owner*");
            context.Added.Should().BeEmpty();
        }
    }

    [Test]
    public async Task CreateSubscription_WhenSchedulingFails_ArchivesSubscriptionAndRethrows()
    {
        var query = new Query { Id = 3, Name = "Overdue invoices" };
        var context = new SubscriptionContext([], [query]);
        _scheduler
            .Setup(x => x.AddOrUpdate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("scheduler storage unreachable"));

        var act = () => BuildService(context).CreateSubscription(NewSubscriptionData(query.Id), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("scheduler storage unreachable");
        context.Added.Should().ContainSingle()
            .Which.ArchivedTime.Should().NotBeNull("a subscription whose job was never registered must not look active");
        context.SaveCount.Should().Be(2, "the create is saved, then the compensating archive is saved");
    }

    [Test]
    public async Task ReactivateSubscription_RestoresSubscriptionAndItsParameters_AndSchedulesIt()
    {
        var replacedByEdit = Parameter(ArchivedAt.AddDays(-5));
        var archivedWithSubscription = Parameter(ArchivedAt.AddMilliseconds(3));
        var subscription = ArchivedSubscription(replacedByEdit, archivedWithSubscription);
        var context = new SubscriptionContext([subscription], [subscription.Query]);

        await BuildService(context).ReactivateSubscription(subscription.Id, CancellationToken.None);

        subscription.ArchivedTime.Should().BeNull();
        archivedWithSubscription.ArchivedTime.Should().BeNull();
        replacedByEdit.ArchivedTime.Should().Be(ArchivedAt.AddDays(-5), "a parameter replaced by an earlier edit stays archived");
        _scheduler.Verify(x => x.AddOrUpdate(subscription.Id, subscription.Query.Name, subscription.CronExpression), Times.Once);
    }

    [Test]
    public async Task ReactivateSubscription_WhenSchedulingFails_ArchivesItAgainAndRethrows()
    {
        var parameter = Parameter(ArchivedAt.AddMilliseconds(3));
        var subscription = ArchivedSubscription(parameter);
        var context = new SubscriptionContext([subscription], [subscription.Query]);
        _scheduler
            .Setup(x => x.AddOrUpdate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("scheduler storage unreachable"));

        var act = () => BuildService(context).ReactivateSubscription(subscription.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("scheduler storage unreachable");
        subscription.ArchivedTime.Should().NotBeNull();
        parameter.ArchivedTime.Should().NotBeNull();
    }

    [Test]
    public async Task ReactivateSubscription_WhenNotArchived_Throws()
    {
        var subscription = ArchivedSubscription();
        subscription.ArchivedTime = null;
        var context = new SubscriptionContext([subscription], [subscription.Query]);

        var act = () => BuildService(context).ReactivateSubscription(subscription.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*is not archived*");
        _scheduler.Verify(x => x.AddOrUpdate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Test]
    public async Task ReactivateSubscription_WhenQueryIsArchived_ThrowsWithoutScheduling()
    {
        var subscription = ArchivedSubscription();
        subscription.Query.ArchivedTime = ArchivedAt;
        var context = new SubscriptionContext([subscription], [subscription.Query]);

        var act = () => BuildService(context).ReactivateSubscription(subscription.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Restore the query*");
        subscription.ArchivedTime.Should().Be(ArchivedAt);
        _scheduler.Verify(x => x.AddOrUpdate(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    private SubscriptionService BuildService(
        SubscriptionContext context,
        IBeaconActorAccessor? actors = null,
        BeaconConfiguration? configuration = null,
        QueryExecutionOptions? queryOptions = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        return new SubscriptionService(
            factory.Object,
            _scheduler.Object,
            Mock.Of<IAnomalyDetectionService>(),
            actors ?? TestProjectAccess.Actors(),
            configuration ?? new BeaconConfiguration(),
            Microsoft.Extensions.Options.Options.Create(queryOptions ?? new QueryExecutionOptions()), new RecordingSecurityAudit(), TimeProvider.System);
    }

    private static SubscriptionData NewSubscriptionData(int queryId) =>
        new()
        {
            QueryId = queryId,
            CronExpression = "0 8 * * *",
            CreateTasks = true
        };

    private static SubscriptionParameter Parameter(DateTime archivedTime) =>
        new()
        {
            QueryPlaceholder = "@from",
            Value = "2026-01-01",
            ArchivedTime = archivedTime
        };

    private static Subscription ArchivedSubscription(params SubscriptionParameter[] parameters) =>
        new()
        {
            Id = 37,
            QueryId = 3,
            CronExpression = "0 8 * * *",
            Query = new Query { Id = 3, Name = "Overdue invoices" },
            Parameters = parameters.ToList(),
            ArchivedTime = ArchivedAt
        };

    private sealed class SubscriptionContext : BeaconContext
    {
        private static readonly DbContextOptions<SubscriptionContext> Options =
            new DbContextOptionsBuilder<SubscriptionContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<Subscription> _subscriptions;
        private readonly DbSet<Query> _queries;
        private readonly DbSet<QueryStep> _querySteps = BuildSet(new List<QueryStep>());
        private readonly DbSet<Recipient> _recipients = BuildSet(new List<Recipient>());

        public List<Subscription> Added { get; } = [];

        public int SaveCount { get; private set; }

        public SubscriptionContext(List<Subscription> subscriptions, List<Query> queries) : base(Options, "beacon")
        {
            _subscriptions = BuildSet(subscriptions, Added);
            _queries = BuildSet(queries);
        }

        // The service disposes its context with `await using`; a no-op keeps the double readable for the assertions.
        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            object? set = typeof(TEntity) switch
            {
                var x when x == typeof(Subscription) => _subscriptions,
                var x when x == typeof(Query) => _queries,
                var x when x == typeof(QueryStep) => _querySteps,
                var x when x == typeof(Recipient) => _recipients,
                _ => null
            };

            return set as DbSet<TEntity> ?? base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(0);
        }

        private static DbSet<T> BuildSet<T>(List<T> data, List<T>? added = null) where T : class
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(queryable.GetEnumerator()));
            set.As<IQueryable<T>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => queryable.GetEnumerator());

            if (added != null)
            {
                set.Setup(x => x.Add(It.IsAny<T>()))
                    .Callback<T>(x => added.Add(x));
            }

            return set.Object;
        }
    }
}
