using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
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

        var result = await provider.ExecuteQueryAsync(HostDataSource(), "SELECT PasswordHash FROM Customer", [], CancellationToken.None);

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

    [Test]
    public void SupportsDatabaseReadOnlyEnforcement_StaysHonestForSqlServer()
    {
        var provider = CreateProvider(new Mock<IDataSourceConnectionResolver>());

        provider.SupportsDatabaseReadOnlyEnforcement(DatabaseEngineType.MSSQL).Should().BeFalse();
    }

    private static DatabaseProvider CreateProvider(Mock<IDataSourceConnectionResolver> resolver)
    {
        var registry = new SingleSnapshotRegistry(HostTestModelFactory.Read(x => x.AllowTables("Customer")));

        return new DatabaseProvider(
            resolver.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new HostDataSourceGuard(registry, new QueryGuardrailService()),
            NullLogger<DatabaseProvider>.Instance);
    }

    private sealed class SingleSnapshotRegistry(HostExposureSnapshot snapshot) : IHostDataSourceRegistry
    {
        public IReadOnlyList<HostDataSourceRegistration> Registrations => [];

        public HostExposureSnapshot? GetSnapshot(string hostManagedKey) => hostManagedKey == "efcore:Netgiro" ? snapshot : null;
    }

    private static DataSource HostDataSource()
    {
        return new DataSource
        {
            Id = 7,
            Name = "Netgiro",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.MSSQL,
            HostManagedKey = "efcore:Netgiro",
            EncryptedConnectionData = "reference"
        };
    }
}
