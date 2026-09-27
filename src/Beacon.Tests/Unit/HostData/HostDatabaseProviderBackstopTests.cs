using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// The provider is the backstop every SQL path shares (MCP query/ask/dry_run, ad-hoc UI queries, value sampling):
/// a host-policy violation must be refused there before any connection is resolved.
/// </summary>
[TestFixture]
public class HostDatabaseProviderBackstopTests
{
    [Test]
    public async Task ExecuteReadOnlyQueryAsync_HiddenTable_IsRefusedBeforeConnecting()
    {
        var resolver = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        var provider = CreateProvider(resolver);

        var result = await provider.ExecuteReadOnlyQueryAsync(HostDataSource(), "SELECT Payload FROM AuditLog", [], CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not exposed");
        resolver.Verify(x => x.GetConnectionString(It.IsAny<DataSource>()), Times.Never);
    }

    [Test]
    public async Task ExecuteQueryAsync_ExcludedColumn_IsRefusedOnTheNonMcpPathToo()
    {
        var resolver = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        var provider = CreateProvider(resolver);

        var result = await provider.ExecuteQueryAsync(HostDataSource(), "SELECT PasswordHash FROM dbo.Customer", [], CancellationToken.None);

        result.Success.Should().BeFalse();
        resolver.Verify(x => x.GetConnectionString(It.IsAny<DataSource>()), Times.Never);
    }

    [Test]
    public async Task ValidateQueryAsync_HiddenTable_IsInvalidWithoutDryRun()
    {
        var resolver = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        var provider = CreateProvider(resolver);

        var result = await provider.ValidateQueryAsync(HostDataSource(), "SELECT Payload FROM AuditLog", CancellationToken.None);

        result.IsValid.Should().BeFalse();
        result.Skipped.Should().BeFalse();
        result.Errors.Should().ContainSingle(x => x.Contains("not exposed"));
    }

    [TestCase(false, "SELECT Name FROM dbo.Customer WHERE Id = @p0")]
    [TestCase(true, "SELECT \"Name\" FROM public.\"Customer\" WHERE \"Id\" = @p0")]
    public async Task ExecuteReadOnlyQueryAsync_UnboundParameter_IsRefusedBeforeConnecting(bool npgsql, string sql)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>(MockBehavior.Strict);
        var provider = CreateProvider(resolver, npgsql);

        var result = await provider.ExecuteReadOnlyQueryAsync(HostDataSource(npgsql), sql, [], CancellationToken.None);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("'@p0' has no bound value");
        resolver.Verify(x => x.GetConnectionString(It.IsAny<DataSource>()), Times.Never);
    }

    [TestCase(false, "SELECT Name FROM dbo.Customer WHERE Id = @p0")]
    [TestCase(true, "SELECT \"Name\" FROM public.\"Customer\" WHERE \"Id\" = @p0")]
    public async Task ExecuteReadOnlyQueryAsync_BoundParameter_PassesThePolicy_AndServerErrorsStayGeneric(bool npgsql, string sql)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(new InvalidOperationException("conversion failed for value '0101302989'"));
        var provider = CreateProvider(resolver, npgsql);

        var result = await provider.ExecuteReadOnlyQueryAsync(HostDataSource(npgsql), sql, new Dictionary<string, object?> { ["p0"] = 5 }, CancellationToken.None);

        resolver.Verify(x => x.GetConnectionString(It.IsAny<DataSource>()), Times.Once, "the policy accepted the parameterised SQL and execution was attempted");
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(DatabaseProvider.HostQueryFailedMessage, "a server message can quote column values");
    }

    [Test]
    public async Task ExecuteQueryAsync_OrdinarySource_KeepsTheServerMessage()
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(new InvalidOperationException("relation \"loans\" does not exist"));
        var provider = CreateProvider(resolver);
        var dataSource = HostDataSource(npgsql: true);
        dataSource.HostManagedKey = null;

        var result = await provider.ExecuteQueryAsync(dataSource, "SELECT 1", [], CancellationToken.None);

        result.ErrorMessage.Should().Contain("does not exist");
    }

    [Test]
    public void SupportsDatabaseReadOnlyEnforcement_StaysHonestForSqlServer()
    {
        var provider = CreateProvider(new Mock<IDataSourceConnectionResolver>());

        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.MSSQL).Should().BeFalse();
    }

    private static DatabaseProvider CreateProvider(Mock<IDataSourceConnectionResolver> resolver, bool npgsql = false)
    {
        var registry = new SingleSnapshotRegistry(HostTestModelFactory.Read(x => x.AllowTables("Customer"), npgsql));

        return new DatabaseProvider(
            resolver.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new HostDataSourceGuard(registry),
            NullLogger<DatabaseProvider>.Instance);
    }

    private sealed class SingleSnapshotRegistry(HostExposureSnapshot snapshot) : IHostDataSourceRegistry
    {
        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => hostManagedKey == "efcore:Netgiro" ? snapshot : null;
    }

    private static DataSource HostDataSource(bool npgsql = false)
    {
        return new DataSource
        {
            Id = 7,
            Name = "Netgiro",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = HostTestModelFactory.EngineOf(npgsql),
            HostManagedKey = "efcore:Netgiro",
            EncryptedConnectionData = "reference"
        };
    }
}
