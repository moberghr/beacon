using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Grounding reads a data source's live values only for a caller allowed to query the project, and under the strictest
/// guardrails of the live projects that link the source: value grounding and its probe budget, stored sample values and
/// the custom PII patterns of the grounding screen, which stored sample values pass again whenever they are shown. The
/// real knowledge graph and value grounding run over in-memory sets (§4.7); the provider is a mock that records every
/// probe it is sent.
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
    private IDbContextFactory<BeaconContext> _factory = null!;

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
        var context = await Service(Shared(new McpSettingsData { EnableValueGrounding = false, EnableSampleValueCollection = true })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);

        _probes.Should().BeEmpty();
        context.FullContext.Should().NotContain("## Value matches");
    }

    [Test]
    public async Task AnotherLinkingProjectsLowerProbeBudget_Applies()
    {
        await Service(Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = true, ValueGroundingMaxProbes = 1 })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);

        _probes.Should().ContainSingle();
    }

    [Test]
    public async Task AnotherLinkingProjectWithSampleValuesOff_HidesTheStoredSampleValues()
    {
        var withSamples = await Service(Unshared()).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);
        var withoutSamples = await Service(Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = false })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);

        withSamples.FullContext.Should().Contain("REFUNDED");
        withoutSamples.FullContext.Should().NotContain("REFUNDED").And.NotContain("PAID");
    }

    [Test]
    public async Task AnotherLinkingProjectsCustomPiiPattern_ScreensTheProbedValues()
    {
        var context = await Service(Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = true, CustomPiiPatterns = ["berlin"] })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);

        _probes.Should().NotBeEmpty();
        context.FullContext.Should().NotContain("'Berlin'");
    }

    [Test]
    public async Task TheRepairPromptsTableSchemas_FollowTheSampleValueSettingToo()
    {
        var withSamples = await Service(Unshared()).GetTablesContextAsync(DataSourceId, ProjectId, ["orders"]);
        var withoutSamples = await Service(Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = false })).GetTablesContextAsync(DataSourceId, ProjectId, ["orders"]);

        withSamples.Should().Contain("REFUNDED");
        withoutSamples.Should().Contain("status").And.NotContain("REFUNDED");
    }

    // Samples stored before a project with a stricter pattern linked the source, or before a pattern was added, are
    // screened again with the patterns that apply now: the matching values are gone, the others stay, and the list no
    // longer claims to hold every value of the column.
    [Test]
    public async Task ALinkingProjectsCustomPiiPattern_HidesTheMatchingStoredSampleValues_AndKeepsTheOthers()
    {
        var unscreened = await Service(Unshared()).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);
        var service = Service(Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = true, CustomPiiPatterns = ["^REFUND"] }));
        var context = await service.GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);
        var tableSchemas = await service.GetTablesContextAsync(DataSourceId, ProjectId, ["orders"]);

        unscreened.FullContext.Should().Contain("Values (all 2): [REFUNDED, PAID]").And.Contain("public.orders.status = 'REFUNDED'");
        context.FullContext.Should().NotContain("REFUNDED").And.Contain("Examples: [PAID]").And.NotContain("Values (all");
        tableSchemas.Should().NotContain("REFUNDED").And.Contain("Examples: [PAID]");
    }

    [Test]
    public async Task ALinkingProjectsCustomPiiPatternOnTheColumnName_HidesAllItsStoredSampleValues()
    {
        var context = await Service(Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = true, CustomPiiPatterns = ["^status$"] })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);

        context.FullContext.Should().Contain("status").And.NotContain("REFUNDED").And.NotContain("PAID");
    }

    // The built-in screen of PII-shaped values applies to stored samples as it does when values are sampled.
    [TestCase(true)]
    [TestCase(false)]
    public async Task BuiltInPiiShapedStoredValues_AreNotShown(bool piiDetection)
    {
        var asker = AskerSettings();
        asker.EnablePiiDetection = piiDetection;

        var context = await Service(new SettingsWorld(new Dictionary<int, McpSettingsData> { [ProjectId] = asker })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: false);

        context.FullContext.Should().NotContain("ada@example.com").And.Contain("Examples: [call back]");
    }

    [Test]
    public async Task GetQueryContext_ShowsOnlyTheStoredSampleValuesTheLinkingProjectsPatternsAllow()
    {
        var result = await QueryContextTool(Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = true, CustomPiiPatterns = ["^REFUND"] })).ExecuteAsync(
            question: Question, datasource_id: DataSourceId, cancellationToken: CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().NotContain("REFUNDED").And.NotContain("ada@example.com").And.Contain("Examples: [PAID]");
    }

    // The ask context names the row-derived data it carries, for the egress log: stored samples, and value matches when
    // grounding found some; neither when the settings keep row data from the prompt.
    [Test]
    public async Task TheAskContext_NamesTheRowDataItCarries()
    {
        var grounded = await Service(Unshared()).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);
        var samplesOnly = await Service(Shared(new McpSettingsData { EnableValueGrounding = false, EnableSampleValueCollection = true })).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);

        grounded.RowDataCategories.Should().Equal(LlmRowDataCategories.SampleValues, LlmRowDataCategories.ValueMatches);
        samplesOnly.RowDataCategories.Should().Equal(LlmRowDataCategories.SampleValues);
    }

    // Under the deployment lock Beacon:Mcp:ForbidRowDataToLlm every project's settings resolve with sample values and value
    // grounding off: no stored sample is shown, no value is probed, and the context carries no row data.
    [Test]
    public async Task UnderTheRowDataLock_NoSampleIsShown_NothingIsProbed_AndTheContextCarriesNoRowData()
    {
        var locked = new SettingsWorld(new Dictionary<int, McpSettingsData> { [ProjectId] = Locked(AskerSettings()) });

        var context = await Service(locked).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true);
        var tables = await Service(locked).GetTablesContextAsync(DataSourceId, ProjectId, ["orders"]);

        _probes.Should().BeEmpty();
        context.FullContext.Should().NotContain("REFUNDED").And.NotContain("## Value matches");
        tables.Should().Contain("status").And.NotContain("REFUNDED");
        context.RowDataCategories.Should().BeEmpty();
    }

    // The metadata response carries stored sample values for an Admin only; every other caller gets the columns without
    // them (the UI shows none), and the refresh's response follows the same rule.
    [Test]
    public async Task TheMetadataRead_CarriesSampleValues_ForAnAdminOnly()
    {
        var snapshot = new DatabaseMetadataSnapshot(
            DataSourceId,
            DatabaseEngineType.PostgreSQL,
            [
                new TableMetadataDto(
                    "public",
                    "orders",
                    [new ColumnMetadataDto("status", "varchar", true, false, false, 1, null, null, null, 20, null, ["PAID"], SampleValuesComplete: true)],
                    [],
                    null)
            ],
            DateTime.UtcNow);
        var metadata = new Mock<IDatabaseMetadataService>();
        metadata
            .Setup(x => x.GetMetadataAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var settings = Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = true });

        var asAdmin = await new GetDataSourceMetadataHandler(metadata.Object, new MetadataSampleView(TestPermissions.Admin, settings.SharedSources(DataSourceId), new QueryGuardrailService()))
            .Handle(new GetDataSourceMetadataQuery(DataSourceId), CancellationToken.None);
        var asViewer = await new GetDataSourceMetadataHandler(metadata.Object, new MetadataSampleView(TestPermissions.None, settings.SharedSources(DataSourceId), new QueryGuardrailService()))
            .Handle(new GetDataSourceMetadataQuery(DataSourceId), CancellationToken.None);
        var asDataSourceAdmin = await new GetDataSourceMetadataHandler(metadata.Object, new MetadataSampleView(TestPermissions.Holding(Beacon.Core.Authorization.BeaconPermissions.DataSourceAdmin), settings.SharedSources(DataSourceId), new QueryGuardrailService()))
            .Handle(new GetDataSourceMetadataQuery(DataSourceId), CancellationToken.None);

        asAdmin.Tables.Single().Columns.Single().SampleValues.Should().Equal("PAID");
        asAdmin.Tables.Single().Columns.Single().SampleValuesComplete.Should().BeTrue();
        asViewer.Tables.Single().Columns.Single().SampleValues.Should().BeNull();
        asViewer.Tables.Single().Columns.Single().SampleValuesComplete.Should().BeFalse();
        asViewer.Tables.Single().Columns.Single().ColumnName.Should().Be("status", "the catalog itself is unchanged");
        asDataSourceAdmin.Tables.Single().Columns.Single().SampleValues.Should().BeNull("only an Admin sees sample values");
    }

    // The metadata read serves stored sample values under the same screen, with the settings of every live project that
    // links the source; the cached snapshot keeps what was stored.
    [Test]
    public async Task TheMetadataRead_ScreensStoredSampleValues_WithTheLinkingProjectsPatterns()
    {
        var snapshot = new DatabaseMetadataSnapshot(
            DataSourceId,
            DatabaseEngineType.PostgreSQL,
            [
                new TableMetadataDto(
                    "public",
                    "orders",
                    [
                        new ColumnMetadataDto("status", "varchar", true, false, false, 1, null, null, null, 20, null, ["REFUNDED", "PAID"], SampleValuesComplete: true),
                        new ColumnMetadataDto("note", "text", true, false, false, 2, null, null, null, null, null, ["ada@example.com", "call back"]),
                        new ColumnMetadataDto("region", "varchar", true, false, false, 3, null, null, null, 20, null, ["Berlin"], SampleValuesComplete: true)
                    ],
                    [],
                    null)
            ],
            DateTime.UtcNow);
        var metadata = new Mock<IDatabaseMetadataService>();
        metadata
            .Setup(x => x.GetMetadataAsync(DataSourceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(snapshot);
        var settings = Shared(new McpSettingsData { EnableValueGrounding = true, EnableSampleValueCollection = true, CustomPiiPatterns = ["^REFUND"] });
        var handler = new GetDataSourceMetadataHandler(
            metadata.Object,
            new MetadataSampleView(TestPermissions.Admin, settings.SharedSources(DataSourceId), new QueryGuardrailService()));

        var read = await handler.Handle(new GetDataSourceMetadataQuery(DataSourceId), CancellationToken.None);

        var columns = read.Tables.Single().Columns;
        columns[0].SampleValues.Should().Equal("PAID");
        columns[0].SampleValuesComplete.Should().BeFalse();
        columns[1].SampleValues.Should().Equal("call back");
        columns[2].SampleValues.Should().Equal("Berlin");
        columns[2].SampleValuesComplete.Should().BeTrue("nothing was withheld from region");
        snapshot.Tables.Single().Columns[0].SampleValues.Should().Equal("REFUNDED", "PAID");
    }

    private static McpSettingsData Locked(McpSettingsData settings) =>
        McpSettingsProvider.Resolve(settings, null, new McpDeploymentOptions { ForbidRowDataToLlm = true }).Effective;

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
                new ColumnMetadata { ColumnName = "status", DataType = "varchar(20)", MaxLength = 20, SampleValues = "[\"REFUNDED\",\"PAID\"]", SampleValuesComplete = true },
                new ColumnMetadata { ColumnName = "region", DataType = "varchar(20)", MaxLength = 20 },
                new ColumnMetadata { ColumnName = "note", DataType = "text", SampleValues = "[\"ada@example.com\",\"call back\"]" }
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
        _factory = factory.Object;

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
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
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
            new QueryGuardrailService(),
            NullLogger<KnowledgeGraphService>.Instance);
    }

    // A caller who may read the project but not query it: grounding from stored sample values alone.
    private GetQueryContextTool QueryContextTool(SettingsWorld settings)
    {
        var knowledgeGraph = Service(settings);
        var audit = new McpAuditService(_factory, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance);
        var caller = new McpProjectContext { UserId = 1, AllowedProjectIds = [ProjectId], QueryProjectIds = [] };

        return new GetQueryContextTool(knowledgeGraph, _factory, caller, audit, TestRowDataEgress.Discarding(), NullLogger<GetQueryContextTool>.Instance);
    }

    // Every project of the map is live and links the data source.
    private sealed class SettingsWorld(IReadOnlyDictionary<int, McpSettingsData> projects)
    {
        public IEnumerable<int> ProjectIds => projects.Keys;

        public IMcpSettingsProvider Provider => SettingsProviderMock.Create(projects[ProjectId], projectSettings: projects).Object;

        // The global row is the asking project's settings, as for the provider above.
        public ISharedSourceSettings SharedSources(int dataSourceId) => TestSharedSources.Linking(dataSourceId, projects, projects[ProjectId]);
    }
}
