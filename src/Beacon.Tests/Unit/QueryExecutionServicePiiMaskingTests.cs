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

        // The structured channel must carry the SAME masked rows — never the raw values.
        var structuredValues = StructuredRowValues(result);
        structuredValues.Should().Contain("a***m");
        structuredValues.Should().Contain("Alice");
        structuredValues.Should().NotContain(RawEmail);
    }

    [Test]
    public async Task ExecuteAsync_PiiDetectionOff_ReturnsRawValues()
    {
        var service = BuildService(piiDetectionOn: false);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().Contain(RawEmail);
        StructuredRowValues(result).Should().Contain(RawEmail);
    }

    private static List<string?> StructuredRowValues(QueryExecutionResult result)
    {
        result.Structured.Should().NotBeNull();
        return result.Structured!["rows"]!.AsArray()
            .SelectMany(x => x!.AsArray())
            .Select(x => x?.GetValue<string>())
            .ToList();
    }

    [Test]
    public async Task ExecuteAsync_ResolvesPiiDetectionForTheActiveProject_NotTheGlobalRow()
    {
        // T-F001 / SF-F001: the PII switch is per project now. Global says OFF, project 7 says ON, the request
        // runs under project 7 — masking must follow the project, and the provider must be asked for 7 only.
        Mock<IMcpSettingsProvider>? settingsProvider = null;
        var service = BuildService(
            piiDetectionOn: false,
            activeProjectId: 7,
            perProject: new Dictionary<int, McpSettingsData> { [7] = new McpSettingsData { EnablePiiDetection = true } },
            captureProvider: x => settingsProvider = x);

        var result = await service.ExecuteAsync(1, "SELECT email, name FROM users", 100, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.FormattedResult.Should().NotContain(RawEmail, "project 7 has PII detection on even though the global row has it off");
        settingsProvider!.Verify(x => x.GetEffectiveSettingsAsync(7, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        settingsProvider.Verify(x => x.GetEffectiveSettingsAsync(It.Is<int>(id => id != 7), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static QueryExecutionService BuildService(
        bool piiDetectionOn,
        int activeProjectId = 1,
        IReadOnlyDictionary<int, McpSettingsData>? perProject = null,
        Action<Mock<IMcpSettingsProvider>>? captureProvider = null)
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
        // QueryExecutionService routes through the database-level read-only path (§1.5 backstop).
        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(
                It.IsAny<DataSource>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(providerResult);

        var providerFactory = new Mock<IDataSourceProviderFactory>();
        providerFactory
            .Setup(x => x.GetProvider(It.IsAny<DataSourceType>()))
            .Returns(provider.Object);

        var settingsProvider = SettingsProviderMock.Create(
            new McpSettingsData { EnablePiiDetection = piiDetectionOn },
            projectSettings: perProject);
        captureProvider?.Invoke(settingsProvider);

        return new QueryExecutionService(
            factory.Object,
            providerFactory.Object,
            new QueryGuardrailService(),
            settingsProvider.Object,
            new McpProjectContext { UserId = 1, ActiveProjectId = activeProjectId, AllowedProjectIds = [activeProjectId] });
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
