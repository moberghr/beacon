using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Services.Retention;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC2 of spec <c>retention-lock</c>: the registry partitions EVERY string property of EVERY <c>Mcp*</c> entity into
/// Content / ErrorClass / Structural — a new column cannot ship unclassified. The negative case proves the check
/// actually bites by feeding a synthetic entity with an unregistered string property through the same helper.
/// </summary>
[TestFixture]
public class McpRetentionDenyListTests
{
    [Test]
    public void EveryStringPropertyOfEveryMcpEntity_IsClassifiedExactlyOnce()
    {
        var entities = McpEntityTypes();

        entities.Should().HaveCountGreaterThan(10, "the Mcp entity set should be discovered by reflection");

        var unclassified = McpRetentionDenyList.UnclassifiedProperties(entities);
        var duplicates = McpRetentionDenyList.Rules
            .GroupBy(x => $"{x.Entity.Name}.{x.Property}")
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToList();

        unclassified.Should().BeEmpty("every Mcp* string property must be registered: {0}", string.Join(", ", unclassified));
        duplicates.Should().BeEmpty("a property must carry exactly one rule: {0}", string.Join(", ", duplicates));
    }

    [Test]
    public void EveryRule_NamesAWritableStringPropertyOfItsEntity()
    {
        var dangling = McpRetentionDenyList.Rules
            .Where(x => McpRetentionDenyList.StringProperties(x.Entity).All(y => y.Name != x.Property))
            .Select(x => $"{x.Entity.Name}.{x.Property}")
            .ToList();

        dangling.Should().BeEmpty("a rule must point at a real property: {0}", string.Join(", ", dangling));
    }

    [Test]
    public void UnclassifiedProperties_ReportsAnUnregisteredStringProperty()
    {
        var unclassified = McpRetentionDenyList.UnclassifiedProperties([typeof(UnregisteredMcpEntity)]);

        unclassified.Should().ContainSingle()
            .Which.Should().Be($"{nameof(UnregisteredMcpEntity)}.{nameof(UnregisteredMcpEntity.LeakedText)}");
    }

    [Test]
    public void Classification_MatchesTheReviewedRegistry()
    {
        KindOf<McpQuerySignal>(nameof(McpQuerySignal.Question)).Should().Be(RetentionKind.Content);
        KindOf<McpQuerySignal>(nameof(McpQuerySignal.Tool)).Should().Be(RetentionKind.Structural);
        KindOf<McpQuerySignal>(nameof(McpQuerySignal.TablesUsed)).Should().Be(RetentionKind.Structural);
        KindOf<McpQuerySignal>(nameof(McpQuerySignal.ExecutionError)).Should().Be(RetentionKind.ErrorClass);
        KindOf<McpAuditLog>(nameof(McpAuditLog.Parameters)).Should().Be(RetentionKind.Content);
        KindOf<McpAuditLog>(nameof(McpAuditLog.ErrorMessage)).Should().Be(RetentionKind.ErrorClass);
        KindOf<McpLearnedPattern>(nameof(McpLearnedPattern.PatternContent)).Should().Be(RetentionKind.Structural);
        KindOf<McpLearnedPattern>(nameof(McpLearnedPattern.ExampleQuestion)).Should().Be(RetentionKind.Content);
        KindOf<McpEvalResult>(nameof(McpEvalResult.JudgeVerdict)).Should().Be(RetentionKind.Content);
        KindOf<McpGlossaryTerm>(nameof(McpGlossaryTerm.Definition)).Should().Be(RetentionKind.Structural);
    }

    [Test]
    public void RequiredStringProperties_CoverTheThreeNonNullableContentColumns()
    {
        McpRetentionDenyList.IsRequiredString(typeof(McpQuerySignal), nameof(McpQuerySignal.Question)).Should().BeTrue();
        McpRetentionDenyList.IsRequiredString(typeof(McpEvalCase), nameof(McpEvalCase.Question)).Should().BeTrue();
        McpRetentionDenyList.IsRequiredString(typeof(McpEvalCase), nameof(McpEvalCase.GoldSql)).Should().BeTrue();
        McpRetentionDenyList.IsRequiredString(typeof(McpQuerySignal), nameof(McpQuerySignal.GeneratedSql)).Should().BeFalse();
        McpRetentionDenyList.RequiredStringProperties.Should().HaveCount(3);
    }

    [Test]
    public void ProjectIdSelector_ReadsTheRowsProject_AndIsNullForProjectlessEntities()
    {
        var signal = new McpQuerySignal { Tool = "ask", Question = "q", ProjectId = 42 };

        McpRetentionDenyList.ProjectIdSelector(typeof(McpQuerySignal))!(signal).Should().Be(42);
        McpRetentionDenyList.ProjectIdSelector(typeof(McpEvalResult)).Should().BeNull();
        McpRetentionDenyList.ProjectIdSelector(typeof(McpSession)).Should().BeNull();
    }

    private static RetentionKind KindOf<TEntity>(string property) =>
        McpRetentionDenyList.RulesFor(typeof(TEntity))
            .Where(x => x.Property == property)
            .Select(x => x.Kind)
            .Single();

    private static IReadOnlyList<Type> McpEntityTypes() =>
        typeof(McpQuerySignal).Assembly.GetTypes()
            .Where(x => x.IsClass)
            .Where(x => !x.IsAbstract)
            .Where(x => x.Namespace == typeof(McpQuerySignal).Namespace)
            .Where(x => x.Name.StartsWith("Mcp", StringComparison.Ordinal))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

    private sealed class UnregisteredMcpEntity : BaseEntity
    {
        public string? LeakedText { get; set; }
    }
}
