using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Grounding reads a data source's live values only for a caller allowed to query the project, and under the strictest
/// guardrails of the live projects that link the source: value grounding and its probe budget, stored sample values and
/// the custom PII patterns of the grounding screen. The real knowledge graph and value grounding run over in-memory sets
/// (§4.7); the provider is a mock that records every probe it is sent.
/// </summary>
[TestFixture]
public class GroundingGuardrailsTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 1;
    private const int OtherProjectId = 2;

    // "refunded" is in the stored sample values of status; "Berlin" has to be probed for.
    private const string Question = "How many \"refunded\" orders ship to 'Berlin'?";

    private List<string> _probes = null!;

    [SetUp]
    public void SetUp()
    {
        _probes = [];
    }

    [Test]
    public async Task ACallerWhoMayOnlyReadTheProject_GetsStoredSampleValues_ButNoLiveProbe()
    {
        var context = await Service(Unshared()).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);

        _probes.Should().BeEmpty("a caller without Query in the project never has live data probed for it");
        context.FullContext.Should().Contain("public.orders.status = 'REFUNDED'").And.NotContain("'Berlin'");
    }

    [Test]
    public async Task ACallerWhoMayQueryTheProject_GetsLiveProbes()
    {
        var context = await Service(Unshared()).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);

        _probes.Should().NotBeEmpty();
        context.FullContext.Should().Contain("public.orders.region = 'Berlin'").And.Contain("public.orders.status = 'REFUNDED'");
    }

    [Test]
    public async Task AnotherLinkingProjectWithValueGroundingOff_TurnsItOffForThisProjectToo()
    {
        var context = await Service(Shared(new McpSettingsData { EnableValueGrounding = false })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);

        _probes.Should().BeEmpty();
        context.FullContext.Should().NotContain("## Value matches");
    }

    [Test]
    public async Task AnotherLinkingProjectsLowerProbeBudget_Applies()
    {
        await Service(Shared(new McpSettingsData { ValueGroundingMaxProbes = 1 })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);

        _probes.Should().ContainSingle();
    }

    [Test]
    public async Task AnotherLinkingProjectWithSampleValuesOff_HidesTheStoredSampleValues()
    {
        var withSamples = await Service(Unshared()).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);
        var withoutSamples = await Service(Shared(new McpSettingsData { EnableSampleValueCollection = false })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);

        withSamples.FullContext.Should().Contain("REFUNDED");
        withoutSamples.FullContext.Should().NotContain("REFUNDED").And.NotContain("PAID");
    }

    [Test]
    public async Task AnotherLinkingProjectsCustomPiiPattern_ScreensTheProbedValues()
    {
        var context = await Service(Shared(new McpSettingsData { CustomPiiPatterns = ["berlin"] })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);

        _probes.Should().NotBeEmpty();
        context.FullContext.Should().NotContain("'Berlin'");
    }

    [Test]
    public async Task TheRepairPromptsTableSchemas_FollowTheSampleValueSettingToo()
    {
        var withSamples = await Service(Unshared()).GetTablesContextAsync(DataSourceId, ProjectId, ["orders"]);
        var withoutSamples = await Service(Shared(new McpSettingsData { EnableSampleValueCollection = false })).GetTablesContextAsync(DataSourceId, ProjectId, ["orders"]);

        withSamples.Should().Contain("REFUNDED");
        withoutSamples.Should().Contain("status").And.NotContain("REFUNDED");
    }

    private static McpSettingsData AskerSettings() => new()
    {
        EnableValueGrounding = true,
        ValueGroundingMaxProbes = 12,
        EnableSampleValueCollection = true,
        EnableLearning = false,
        EnableGoldenExemplars = false
    };

    private static SettingsWorld Unshared() => new(new Dictionary<int, McpSettingsData> { [ProjectId] = AskerSettings() });

    // The other project inherits everything else from the asker's settings.
    private static SettingsWorld Shared(McpSettingsData other)
    {
        other.EnableLearning = false;
        other.EnableGoldenExemplars = false;

        return new SettingsWorld(new Dictionary<int, McpSettingsData> { [ProjectId] = AskerSettings(), [OtherProjectId] = other });
    }

    private KnowledgeGraphService Service(SettingsWorld settings)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted"
        };
        var orders = new DatabaseMetadata
        {
            Id = 10,
            DataSourceId = DataSourceId,
            DataSource = dataSource,
            SchemaName = "public",
            TableName = "orders",
            Columns =
            [
                new ColumnMetadata { ColumnName = "id", DataType = "int", IsPrimaryKey = true },
                new ColumnMetadata { ColumnName = "status", DataType = "varchar(20)", MaxLength = 20, SampleValues = "[\"REFUNDED\",\"PAID\"]" },
                new ColumnMetadata { ColumnName = "region", DataType = "varchar(20)", MaxLength = 20 }
            ]
        };
        foreach (var column in orders.Columns)
        {
            column.DatabaseMetadata = orders;
        }

        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource], saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet([orders], saved),
            [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(orders.Columns.ToList(), saved),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(
                settings.ProjectIds
                    .Select(x => new ProjectDataSource { ProjectId = x, DataSourceId = DataSourceId })
                    .ToList(),
                saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        var provider = new Mock<IDataSourceProvider>();
        provider
            .Setup(x => x.ExecuteReadOnlyQueryAsync(It.IsAny<DataSource>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .Callback<DataSource, string, Dictionary<string, object?>, CancellationToken>((_, sql, _, _) => _probes.Add(sql))
            .ReturnsAsync((DataSource _, string sql, Dictionary<string, object?> _, CancellationToken _) =>
                new ProviderQueryResult
                {
                    Success = true,
                    Rows = sql.Contains("\"region\"", StringComparison.Ordinal)
                        ? [new Dictionary<string, object?> { ["region"] = "Berlin" }]
                        : []
                });
        var providers = new Mock<IDataSourceProviderFactory>();
        providers
            .Setup(x => x.GetProvider(DataSourceType.Database))
            .Returns(provider.Object);

        var valueGrounding = new ValueGroundingService(
            factory.Object,
            providers.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            NullLogger<ValueGroundingService>.Instance);

        return new KnowledgeGraphService(
            factory.Object,
            settings.Provider,
            settings.SharedSources(DataSourceId),
            Mock.Of<IBeaconEmbeddingService>(),
            Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())
                == Task.FromResult(SchemaGraph.Build(Array.Empty<TableMetadataDto>(), Array.Empty<SchemaRelationshipEdge>()))),
            valueGrounding,
            TestConfiguredDatabases.None,
            NullLogger<KnowledgeGraphService>.Instance);
    }

    // Every project of the map is live and links the data source.
    private sealed class SettingsWorld(IReadOnlyDictionary<int, McpSettingsData> projects)
    {
        public IEnumerable<int> ProjectIds => projects.Keys;

        public IMcpSettingsProvider Provider => SettingsProviderMock.Create(projects[ProjectId], projectSettings: projects).Object;

        public ISharedSourceSettings SharedSources(int dataSourceId) => TestSharedSources.Linking(dataSourceId, projects);
    }
}
