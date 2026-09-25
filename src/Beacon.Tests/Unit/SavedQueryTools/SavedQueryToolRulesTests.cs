using System.Text.Json;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.SavedQueries;
using FluentAssertions;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.SavedQueryTools;

[TestFixture]
public class SavedQueryToolRulesTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [TestCase("loan_book_by_month", true)]
    [TestCase("abc", true)]
    [TestCase("a1_", true)]
    [TestCase("ab", false)]
    [TestCase("1abc", false)]
    [TestCase("_abc", false)]
    [TestCase("Loan_book", false)]
    [TestCase("loan-book", false)]
    [TestCase("loan book", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValidName_FollowsThePattern(string? name, bool expected)
    {
        SavedQueryToolRules.IsValidName(name).Should().Be(expected);
    }

    [Test]
    public void IsValidName_CapsTheLengthAt41()
    {
        SavedQueryToolRules.IsValidName("a" + new string('b', 40)).Should().BeTrue();
        SavedQueryToolRules.IsValidName("a" + new string('b', 41)).Should().BeFalse();
    }

    [Test]
    public void Inspect_MergesParametersAcrossStepsByName_InStepOrder()
    {
        var shape = Inspect(
            Step(2, 1, "SELECT 2 WHERE x > {from}", Parameter("from", ParameterType.DateTime), Parameter("region", ParameterType.String, "Region code")),
            Step(1, 1, "SELECT 1 WHERE x > {from} AND y < {to}", Parameter("from", ParameterType.DateTime, "Start"), Parameter("to", ParameterType.DateTime)));

        shape.Issue.Should().BeNull();
        shape.Steps.Select(x => x.StepOrder).Should().Equal(1, 2);
        shape.Parameters.Select(x => x.Name).Should().Equal("from", "to", "region");
        shape.Parameters[0].Description.Should().Be("Start");
        shape.Parameters[2].Type.Should().Be(ParameterType.String);
    }

    [Test]
    public void Inspect_RejectsConflictingParameterTypes()
    {
        var shape = Inspect(
            Step(1, 1, "SELECT {id}", Parameter("id", ParameterType.Number)),
            Step(2, 1, "SELECT {id}", Parameter("id", ParameterType.String)));

        shape.Issue.Should().Contain("different types");
    }

    [Test]
    public void Inspect_RejectsTheReservedProjectIdParameter()
    {
        Inspect(Step(1, 1, "SELECT {project_id}", Parameter("project_id", ParameterType.Number)))
            .Issue.Should().Contain("reserved");
    }

    [Test]
    public void Inspect_RejectsANonIdentifierParameterName()
    {
        var parameter = Parameter("x", ParameterType.String);
        parameter.Name = "from date";

        Inspect(Step(1, 1, "SELECT 1", parameter)).Issue.Should().Contain("plain identifier");
    }

    [Test]
    public void Inspect_RejectsAParameterWithoutAPlaceholder()
    {
        var parameter = Parameter("from", ParameterType.String);
        parameter.Placeholder = null;

        Inspect(Step(1, 1, "SELECT 1", parameter)).Issue.Should().Contain("no placeholder");
    }

    [Test]
    public void Inspect_RejectsNoStepsAndUnreadableJson()
    {
        SavedQueryToolRules.Inspect("[]").Issue.Should().Contain("no steps");
        SavedQueryToolRules.Inspect("{not json").Issue.Should().Contain("could not be read");
    }

    private static SavedQueryToolShape Inspect(params QueryStepSnapshot[] steps) =>
        SavedQueryToolRules.Inspect(JsonSerializer.Serialize(steps.ToList(), JsonOptions));
}
