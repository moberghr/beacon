using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// The UI's saved-query step preview on a host data source: the host policy must judge the BOUND SQL (a
/// <c>{placeholder}</c> is not SQL, so judging the stored text rejected every parameterised step) with the real
/// validator. Execution is stopped at the connection lookup, which is only reached once both gates passed.
/// </summary>
[TestFixture]
public class QueryServiceHostStepTests
{
    private const string HostKey = "efcore:Netgiro";

    [TestCase(false, "SELECT c.Name FROM dbo.Customer c WHERE c.Id = {id}")]
    [TestCase(true, "SELECT c.\"Name\" FROM public.\"Customer\" c WHERE c.\"Id\" = {id}")]
    public async Task PreviewQueryStep_ParameterisedHostStep_IsJudgedOnTheBoundSql(bool npgsql, string sql)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(new ExecutionReachedException());
        var service = BuildService(Step(sql, npgsql), resolver.Object, npgsql);

        var act = () => service.PreviewQueryStep(1, 1, [new ParameterValue { Name = "id", Value = "5" }], CancellationToken.None);

        await act.Should().ThrowAsync<ExecutionReachedException>("the bound step passed the read-only gate and the host policy");
    }

    [TestCase(false, "SELECT c.Name FROM dbo.Customer c WHERE c.SSN = {ssn}")]
    [TestCase(true, "SELECT c.\"Name\" FROM public.\"Customer\" c WHERE c.\"SSN\" = {ssn}")]
    public async Task PreviewQueryStep_HostPolicyViolation_IsRefusedBeforeConnecting(bool npgsql, string sql)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        var step = Step(sql, npgsql);
        step.Parameters[0].Name = "ssn";
        step.Parameters[0].Placeholder = "{ssn}";
        var service = BuildService(step, resolver.Object, npgsql);

        var act = () => service.PreviewQueryStep(1, 1, [new ParameterValue { Name = "ssn", Value = "0101302989" }], CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Masked column");
        resolver.Verify(x => x.GetConnectionString(It.IsAny<DataSource>()), Times.Never);
    }

    private static QueryStep Step(string sql, bool npgsql)
    {
        var dataSource = new DataSource
        {
            Id = 7,
            Name = "Netgiro",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = HostTestModelFactory.EngineOf(npgsql),
            HostManagedKey = HostKey,
            EncryptedConnectionData = "reference"
        };

        return new QueryStep
        {
            QueryId = 1,
            StepOrder = 1,
            DataSourceId = dataSource.Id,
            DataSource = dataSource,
            SqlValue = sql,
            Parameters =
            [
                new QueryStepParameter { QueryStepId = 1, Name = "id", Type = ParameterType.Number, Placeholder = "{id}" }
            ]
        };
    }

    private static QueryService BuildService(QueryStep step, IDataSourceConnectionResolver resolver, bool npgsql)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SingleStepContext(step));

        var snapshot = HostTestModelFactory.Read(x => x.AllowTables("Customer").MaskColumns(y => y.Name == "SSN"), npgsql);

        return new QueryService(
            factory.Object,
            resolver,
            Mock.Of<IManualQueryExecutionLogger>(),
            NullLogger<QueryService>.Instance,
            NullLoggerFactory.Instance,
            Mock.Of<IQueryVersionService>(),
            null!,
            Mock.Of<IBeaconUserContext>(),
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new HostDataSourceGuard(new SingleHostRegistry(snapshot)));
    }

    private sealed class ExecutionReachedException : Exception;

    private sealed class SingleHostRegistry(HostExposureSnapshot snapshot) : IHostDataSourceRegistry
    {
        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => hostManagedKey == HostKey ? snapshot : null;
    }

    /// <summary>A context whose <c>QuerySteps</c> is an in-memory async sequence holding one step (§4.7).</summary>
    private sealed class SingleStepContext(QueryStep step) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<SingleStepContext> Options =
            new DbContextOptionsBuilder<SingleStepContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(QueryStep))
            {
                return (DbSet<TEntity>)(object)BuildSet(step);
            }

            return base.Set<TEntity>();
        }

        private static DbSet<QueryStep> BuildSet(QueryStep step)
        {
            var data = new List<QueryStep> { step }.AsQueryable();
            var set = new Mock<DbSet<QueryStep>>();
            set.As<IAsyncEnumerable<QueryStep>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<QueryStep>(data.GetEnumerator()));
            set.As<IQueryable<QueryStep>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<QueryStep>(data.Provider));
            set.As<IQueryable<QueryStep>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<QueryStep>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<QueryStep>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());

            return set.Object;
        }
    }
}
