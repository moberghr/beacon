using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Handlers.McpSettings;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using McpSettingsEntity = Beacon.Core.Data.Entities.McpSettings;

namespace Beacon.Tests.Unit;

/// <summary>
/// Batch B4: the four ask-correctness settings (<see cref="McpSettingsData.EnableValueGrounding"/>,
/// <see cref="McpSettingsData.ValueGroundingMaxProbes"/>, <see cref="McpSettingsData.EnableSemanticLint"/>,
/// <see cref="McpSettingsData.SelfConsistencyMinTables"/>) and <see cref="ColumnMetadata.SampleValuesComplete"/>
/// carry the code defaults end to end — DTO defaults, provider read mapping, and handler write mapping —
/// exactly like the existing golden-exemplar settings.
/// </summary>
[TestFixture]
public class AskCorrectnessSettingsTests
{
    [Test]
    public void McpSettingsData_Defaults_MatchSpec()
    {
        var data = new McpSettingsData();

        data.EnableValueGrounding.Should().BeTrue();
        data.ValueGroundingMaxProbes.Should().Be(12);
        data.EnableSemanticLint.Should().BeTrue();
        data.SelfConsistencyMinTables.Should().Be(2);
    }

    [Test]
    public void ColumnMetadata_SampleValuesComplete_DefaultsFalse()
    {
        var column = new ColumnMetadata
        {
            ColumnName = "status",
            DataType = "varchar"
        };

        column.SampleValuesComplete.Should().BeFalse();
    }

    [Test]
    public void ColumnMetadataDto_SampleValuesComplete_DefaultsFalse()
    {
        var dto = new ColumnMetadataDto(
            "status",
            "varchar",
            IsNullable: false,
            IsPrimaryKey: false,
            IsForeignKey: false,
            OrdinalPosition: 1,
            ForeignKeyTable: null,
            ForeignKeyColumn: null,
            DefaultValue: null,
            MaxLength: 20,
            Description: null);

        dto.SampleValuesComplete.Should().BeFalse();
    }

    [Test]
    public async Task Provider_MapsFourNewSettings_FromEntity()
    {
        // Non-default values prove the provider actually maps these fields rather than
        // happening to match McpSettingsData's own defaults.
        var entity = new McpSettingsEntity
        {
            EnableValueGrounding = false,
            ValueGroundingMaxProbes = 25,
            EnableSemanticLint = false,
            SelfConsistencyMinTables = 7
        };

        var factory = BuildFactory(entity);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new McpSettingsProvider(factory, cache, Options.Create(new McpDeploymentOptions()), NullLogger<McpSettingsProvider>.Instance);

        var data = await provider.GetSettingsAsync();

        data.EnableValueGrounding.Should().BeFalse();
        data.ValueGroundingMaxProbes.Should().Be(25);
        data.EnableSemanticLint.Should().BeFalse();
        data.SelfConsistencyMinTables.Should().Be(7);
    }

    [Test]
    public async Task Handler_AssignsFourNewSettings_ToEntity()
    {
        var context = new SettingsContext(new List<McpSettingsEntity>());
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(context);

        var settingsProvider = SettingsProviderMock.Create();
        var handler = new UpdateMcpSettingsHandler(factory.Object, settingsProvider.Object, Options.Create(new McpDeploymentOptions()));

        var data = new McpSettingsData
        {
            EnableValueGrounding = false,
            ValueGroundingMaxProbes = 99,
            EnableSemanticLint = false,
            SelfConsistencyMinTables = 3
        };

        await handler.Handle(new UpdateMcpSettingsCommand(data), CancellationToken.None);

        context.Added.Should().HaveCount(1);
        var saved = context.Added[0];
        saved.EnableValueGrounding.Should().BeFalse();
        saved.ValueGroundingMaxProbes.Should().Be(99);
        saved.EnableSemanticLint.Should().BeFalse();
        saved.SelfConsistencyMinTables.Should().Be(3);

        settingsProvider.Verify(x => x.InvalidateCache(), Times.Once);
    }

    private static IDbContextFactory<BeaconContext> BuildFactory(McpSettingsEntity entity)
    {
        var context = new SettingsContext(new List<McpSettingsEntity> { entity });
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(context);
        return factory.Object;
    }

    /// <summary>
    /// A <see cref="BeaconContext"/> whose <c>McpSettings</c> set resolves to a mocked async-queryable
    /// backed by the supplied list (no DB, no forbidden <c>UseInMemoryDatabase</c> — §4.7). <c>Add</c> is
    /// captured so the handler's write mapping can be asserted without a real <c>SaveChangesAsync</c>.
    /// </summary>
    private sealed class SettingsContext : BeaconContext
    {
        private static readonly DbContextOptions<SettingsContext> Options =
            new DbContextOptionsBuilder<SettingsContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpSettingsEntity> _settings;

        public List<McpSettingsEntity> Added { get; } = new();

        public SettingsContext(List<McpSettingsEntity> data) : base(Options, "beacon")
        {
            var queryable = data.AsQueryable();
            var set = new Mock<DbSet<McpSettingsEntity>>();
            set.As<IAsyncEnumerable<McpSettingsEntity>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<McpSettingsEntity>(data.GetEnumerator()));
            set.As<IQueryable<McpSettingsEntity>>()
                .Setup(x => x.Provider)
                .Returns(new TestAsyncQueryProvider<McpSettingsEntity>(queryable.Provider));
            set.As<IQueryable<McpSettingsEntity>>().Setup(x => x.Expression).Returns(queryable.Expression);
            set.As<IQueryable<McpSettingsEntity>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
            set.As<IQueryable<McpSettingsEntity>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
            set.Setup(x => x.Add(It.IsAny<McpSettingsEntity>()))
                .Callback<McpSettingsEntity>(x => Added.Add(x));
            _settings = set.Object;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpSettingsEntity))
            {
                return (DbSet<TEntity>)(object)_settings;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
