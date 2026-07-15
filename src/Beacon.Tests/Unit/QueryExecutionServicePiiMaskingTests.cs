using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Proves the PII-masking WIRING in <see cref="QueryExecutionService"/> (the MCP ask path): rows
/// returned by the provider are routed through the real <see cref="QueryGuardrailService"/> before
/// formatting, so PII column values are masked (§1.6/§1.11). <c>MaskPiiValues</c>' own logic is
/// covered by <see cref="QueryGuardrailServiceTests"/>; this asserts the seam is actually connected.
/// </summary>
[TestFixture]
public class QueryExecutionServicePiiMaskingTests
{
    private const string RawEmail = "alice@example.com";

    [Test]
    public async Task ExecuteAsync_PiiDetectionOn_MasksPiiColumnValues()
    {
        var service = BuildService(piiDetectionOn: true);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().NotContain(RawEmail);
        result.FormattedResult.Should().Contain("a***m");   // masked email
        result.FormattedResult.Should().Contain("Alice");   // non-PII column untouched
    }

    [Test]
    public async Task ExecuteAsync_PiiDetectionOff_ReturnsRawValues()
    {
        var service = BuildService(piiDetectionOn: false);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().Contain(RawEmail);
    }

    private static QueryExecutionService BuildService(bool piiDetectionOn)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SeededDataSourceContext());

        var providerResult = new ProviderQueryResult
        {
            Success = true,
            Rows =
            [
                new Dictionary<string, object?> { ["email"] = RawEmail, ["name"] = "Alice" }
            ]
        };
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(providerResult);

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider.Object);

        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { EnablePiiDetection = piiDetectionOn });

        return new QueryExecutionService(
            factory.Object,
            providerFactory.Object,
            new QueryGuardrailService(),
            settingsProvider.Object);
    }

    private sealed class SeededDataSourceContext : BeaconContext
    {
        private static readonly DbContextOptions<SeededDataSourceContext> _options =
            new DbContextOptionsBuilder<SeededDataSourceContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public SeededDataSourceContext() : base(_options, "beacon") { }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource))
            {
                return (DbSet<TEntity>)(object)BuildDataSourceSet();
            }

            return base.Set<TEntity>();
        }

        private static DbSet<DataSource> BuildDataSourceSet()
        {
            var data = new List<DataSource>
            {
                new()
                {
                    Id = 1,
                    Name = "ds",
                    DataSourceType = DataSourceType.Database,
                    EncryptedConnectionData = "x",
                    DatabaseEngineType = DatabaseEngineType.PostgreSQL
                }
            }.AsQueryable();

            var set = new Mock<DbSet<DataSource>>();
            set.As<IAsyncEnumerable<DataSource>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(new TestAsyncEnumerator<DataSource>(data.GetEnumerator()));
            set.As<IQueryable<DataSource>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<DataSource>(data.Provider));
            set.As<IQueryable<DataSource>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<DataSource>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<DataSource>>().Setup(x => x.GetEnumerator()).Returns(data.GetEnumerator());
            return set.Object;
        }
    }
}
