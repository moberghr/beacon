using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// The documentation export (which knowledge answers, get_documentation and the doc-chunk index read) and the
/// documentation-generation prompt show a learned pattern's example SQL as a prompt does: without its comments, read in
/// the dialect of the pattern's own data source; SQL that cannot be shown so is left out (the lesson stays), logged by
/// pattern id only; example questions render on one line. Real service over in-memory sets, no database (§4.7).
/// </summary>
[TestFixture]
public class DocumentationGroundingTests
{
    private const int ProjectId = 1;
    private const int PostgresSource = 7;
    private const int MySqlSource = 8;

    [Test]
    public async Task TheExport_ShowsExampleSqlWithoutComments_InEachPatternsDialect_AndLeavesOutWhatCannotBeShown()
    {
        var logs = new LogRecorder();
        var service = Service(Patterns(), logs);

        var markdown = await service.ExportToMarkdownAsync(documentationId: 20, CancellationToken.None);

        markdown.Should().Contain("- **public.orders**: lesson 1\n  - Example: `SELECT id FROM orders`");
        markdown.Should().Contain("- **public.orders**: lesson 2\n  - Example: `SELECT id FROM orders`");
        markdown.Should().Contain("lesson 3").And.NotContain("never closed", "the lesson stays, its SQL is left out");
        markdown.Should().Contain("Example: `SELECT id FROM orders WHERE note = 'a--b'`", "comment characters inside a literal stay");
        markdown.Should().NotContain("pg-note").And.NotContain("mysql-note");
        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().EndWith("(patterns 3)");
        logs.Contains("never closed").Should().BeFalse("no SQL reaches the log");
    }

    [Test]
    public async Task TheDocumentationPrompt_IsGivenExampleSqlWithoutComments_AndQuestionsOnOneLine()
    {
        var service = Service(Patterns(), new LogRecorder());

        var patterns = await service.GetLearnedPatternsForDocAsync(ProjectId, [PostgresSource, MySqlSource], CancellationToken.None);

        patterns.Select(x => (x.Content, x.ExampleQuestion, x.ExampleSql)).Should().BeEquivalentTo(
            new (string, string?, string?)[]
            {
                ("lesson 1", "first question on two lines", "SELECT id FROM orders"),
                ("lesson 2", null, "SELECT id FROM orders"),
                ("lesson 3", null, null),
                ("lesson 4", null, "SELECT id FROM orders WHERE note = 'a--b'")
            });
    }

    private static List<McpLearnedPattern> Patterns()
    {
        return
        [
            Pattern(1, PostgresSource, "SELECT id FROM orders -- pg-note", "first question\non two lines", 0.9),
            Pattern(2, MySqlSource, "SELECT id FROM orders # mysql-note", null, 0.8),
            Pattern(3, PostgresSource, "SELECT id /* never closed", null, 0.7),
            Pattern(4, PostgresSource, "SELECT id FROM orders WHERE note = 'a--b'", null, 0.6)
        ];
    }

    private static McpLearnedPattern Pattern(int id, int dataSourceId, string exampleSql, string? exampleQuestion, double confidence)
    {
        return new McpLearnedPattern
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = dataSourceId,
            SchemaName = "public",
            TableName = "orders",
            PatternType = McpPatternType.CommonQuery,
            PatternContent = $"lesson {id}",
            ExampleQuestion = exampleQuestion,
            ExampleSql = exampleSql,
            Confidence = confidence,
            Status = McpPatternStatus.Approved
        };
    }

    private static ProjectDocumentationService Service(List<McpLearnedPattern> patterns, LogRecorder logs)
    {
        var saved = new List<object>();
        var sources = new List<DataSource>
        {
            new() { Id = PostgresSource, Name = "warehouse", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.PostgreSQL, EncryptedConnectionData = "encrypted" },
            new() { Id = MySqlSource, Name = "shop", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.MySQL, EncryptedConnectionData = "encrypted" }
        };
        var links = new List<ProjectDataSource>
        {
            new() { ProjectId = ProjectId, DataSourceId = PostgresSource },
            new() { ProjectId = ProjectId, DataSourceId = MySqlSource }
        };
        var documentation = new List<ProjectDocumentation>
        {
            new() { Id = 20, ProjectId = ProjectId, GeneratedByModel = "test-model", Project = new Project { Id = ProjectId, Name = "Project" } }
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(patterns, saved),
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet(sources, saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, saved),
                    [typeof(ProjectDocumentation)] = RecordingBeaconContext.MemorySet(documentation, saved)
                },
                saved));

        return new ProjectDocumentationService(
            factory.Object,
            new Mock<ILlmProvider>().Object,
            new Mock<IDataSourceProviderFactory>().Object,
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            TestRowSecurity.Resolver,
            Mock.Of<ISharedSourceSettings>(),
            new QueryGuardrailService(),
            TestRowDataEgress.Discarding(),
            new RecordingSecurityAudit(),
            logs.For<ProjectDocumentationService>());
    }
}
