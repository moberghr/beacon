using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The data-quality rules table knowledge (and so <c>get_documentation</c>) shows: the rules of the table's enabled
/// contracts as their contract's latest evaluation found them, with each result's text on one line, cut, and left out
/// when it holds characters a reviewer cannot see. The service runs over in-memory sets (no database, §4.7); the query's
/// translation is checked on both providers.
/// </summary>
[TestFixture]
public class TableKnowledgeQualityRulesTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 1;
    private const string Schema = "public";
    private const string Table = "orders";

    // As documented: a result's text is cut at 200 characters.
    private const int MaxDetailsLength = 200;

    [Test]
    public async Task QualityRules_ComeFromTheLatestEvaluationOfEnabledContracts_AndOnlyRulesItRan()
    {
        var contract = Contract(1, isEnabled: true);
        var ran = Rule(contract, 11, DataContractRuleType.NullRate, "id");
        var disabled = Rule(contract, 12, DataContractRuleType.Uniqueness, "id", isEnabled: false);
        Rule(contract, 13, DataContractRuleType.CustomSql, null);
        Evaluate(contract, 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), (ran, false, "Null rate 9.00% exceeds threshold 1"));
        Evaluate(contract, 101, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), (ran, true, "Null rate within threshold"), (disabled, false, "3 duplicate values found"));
        var disabledContract = Contract(2, isEnabled: false);
        var stale = Rule(disabledContract, 21, DataContractRuleType.Volume, null);
        Evaluate(disabledContract, 200, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), (stale, false, "Row count outside expected range"));
        var service = BuildService([contract, disabledContract]);

        var knowledge = await service.GetTableKnowledgeAsync(DataSourceId, ProjectId, Schema, Table);

        knowledge.QualityRules.Should().Equal(new QualityRuleInfo("NullRate", "id", true, "Null rate within threshold"));
    }

    [Test]
    public async Task QualityRules_OfSeveralContractsOnTheTable_AreAllShownInContractOrder()
    {
        var first = Contract(1, isEnabled: true);
        var second = Contract(2, isEnabled: true);
        var firstRule = Rule(first, 11, DataContractRuleType.NullRate, "id");
        var secondRule = Rule(second, 21, DataContractRuleType.Freshness, "placed_time");
        Evaluate(second, 200, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), (secondRule, false, "Data is stale"));
        Evaluate(first, 100, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), (firstRule, true, "Null rate within threshold"));
        var service = BuildService([second, first]);

        var knowledge = await service.GetTableKnowledgeAsync(DataSourceId, ProjectId, Schema, Table);

        knowledge.QualityRules.Select(x => x.RuleType).Should().Equal("NullRate", "Freshness");
    }

    [Test]
    public async Task QualityRuleDetails_AreOneLine_CutToTheLimit_AndLeftOutWithHiddenCharacters()
    {
        var contract = Contract(1, isEnabled: true);
        var multiLine = Rule(contract, 11, DataContractRuleType.CustomSql, null);
        var tooLong = Rule(contract, 12, DataContractRuleType.CustomSql, null);
        var hidden = Rule(contract, 13, DataContractRuleType.CustomSql, null);
        var empty = Rule(contract, 14, DataContractRuleType.CustomSql, null);
        Evaluate(
            contract,
            100,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            (multiLine, false, "first line\n## second line\r\nthird"),
            (tooLong, false, new string('x', MaxDetailsLength + 50)),
            (hidden, false, "looks plain‮but is not"),
            (empty, true, "  "));
        var service = BuildService([contract]);

        var knowledge = await service.GetTableKnowledgeAsync(DataSourceId, ProjectId, Schema, Table);

        knowledge.QualityRules.Select(x => x.Details).Should().Equal(
            "first line ## second line  third",
            new string('x', MaxDetailsLength) + "…",
            null,
            null);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void TableKnowledgeQualityRules_Translates(bool sqlServer)
    {
        using var context = sqlServer ? (BeaconContext)SqlServerTestContext.Create() : NpgsqlTestContext.Create();

        // The query BuildTableKnowledgeAsync sends for the table's rule results.
        var sql = context.DataContracts
            .Where(x => x.DataSourceId == DataSourceId)
            .Where(x => x.SchemaName == Schema)
            .Where(x => x.TableName == Table)
            .Where(x => x.IsEnabled)
            .SelectMany(x => x.Evaluations
                .OrderByDescending(y => y.CreatedTime)
                .ThenByDescending(y => y.Id)
                .Take(1)
                .SelectMany(y => y.RuleResults))
            .Where(x => x.DataContractRule.IsEnabled)
            .OrderBy(x => x.DataContractRule.DataContractId)
            .ThenBy(x => x.DataContractRuleId)
            .Select(x =>
                new
                {
                    x.DataContractRule.RuleType,
                    x.DataContractRule.ColumnName,
                    x.Passed,
                    x.Message
                })
            .ToQueryString();

        var archivedTime = sqlServer ? "[ArchivedTime] IS NULL" : "archived_time IS NULL";
        sql.Should().Contain(archivedTime, "an archived contract is never shown");
        sql.Should().Contain(sqlServer ? "[IsEnabled]" : "is_enabled");
        sql.Should().Contain(sqlServer ? "APPLY" : "LATERAL", "each contract contributes its latest evaluation only");
    }

    private static DataContract Contract(int id, bool isEnabled)
    {
        return new DataContract
        {
            Id = id,
            DataSourceId = DataSourceId,
            SchemaName = Schema,
            TableName = Table,
            Name = $"contract-{id}",
            CronExpression = "0 * * * *",
            IsEnabled = isEnabled
        };
    }

    private static DataContractRule Rule(DataContract contract, int id, DataContractRuleType type, string? column, bool isEnabled = true)
    {
        var rule = new DataContractRule
        {
            Id = id,
            DataContractId = contract.Id,
            DataContract = contract,
            Name = $"rule-{id}",
            RuleType = type,
            ColumnName = column,
            Configuration = "{}",
            IsEnabled = isEnabled
        };
        contract.Rules.Add(rule);

        return rule;
    }

    private static void Evaluate(DataContract contract, int id, DateTime createdTime, params (DataContractRule Rule, bool Passed, string Message)[] results)
    {
        var evaluation = new DataQualityEvaluation
        {
            Id = id,
            DataContractId = contract.Id,
            DataContract = contract,
            CreatedTime = createdTime
        };
        foreach (var result in results)
        {
            evaluation.RuleResults.Add(new DataQualityRuleResult
            {
                DataQualityEvaluationId = id,
                DataQualityEvaluation = evaluation,
                DataContractRuleId = result.Rule.Id,
                DataContractRule = result.Rule,
                Passed = result.Passed,
                Message = result.Message
            });
        }

        contract.Evaluations.Add(evaluation);
    }

    private static KnowledgeGraphService BuildService(List<DataContract> contracts)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource], saved),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(
                [new ProjectDataSource { ProjectId = ProjectId, DataSourceId = DataSourceId }],
                saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(new List<DatabaseMetadata>(), saved),
            [typeof(ProjectDocumentationSection)] = RecordingBeaconContext.MemorySet(new List<ProjectDocumentationSection>(), saved),
            [typeof(CodeReference)] = RecordingBeaconContext.MemorySet(new List<CodeReference>(), saved),
            [typeof(DataQualityScore)] = RecordingBeaconContext.MemorySet(new List<DataQualityScore>(), saved),
            [typeof(DataContract)] = RecordingBeaconContext.MemorySet(contracts, saved),
            [typeof(QueryExecutionHistory)] = RecordingBeaconContext.MemorySet(new List<QueryExecutionHistory>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return new KnowledgeGraphService(
            factory.Object,
            SettingsProviderMock.Create().Object,
            SettingsProviderMock.Unshared(SettingsProviderMock.Create().Object),
            Mock.Of<IBeaconEmbeddingService>(),
            Mock.Of<ISchemaGraphService>(),
            Mock.Of<IValueGroundingService>(),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            NullLogger<KnowledgeGraphService>.Instance);
    }
}
