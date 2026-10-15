using System.Text.Json;
using Beacon.Core.Services;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A migration job's QueryText is read one way for running it and for deciding who may create or run it: JSON steps run on
/// the data source each names; plain SQL (what the job form sends) runs on the job's declared source.
/// </summary>
[TestFixture]
public class MigrationQueryStepsTests
{
    private const int DeclaredSource = 10;

    [Test]
    public void PlainSql_IsOneStepOnTheDeclaredSource()
    {
        var steps = MigrationQuerySteps.Parse(DeclaredSource, "SELECT id FROM accounts");

        steps.Should().ContainSingle().Which.Should().Match<Beacon.Core.Models.Queries.QueryStepData>(
            x => x.DataSourceId == DeclaredSource && x.SqlValue == "SELECT id FROM accounts" && x.StepOrder == 1);
    }

    [Test]
    public void JsonSteps_RunOnTheDataSourceEachNames()
    {
        var queryText = JsonSerializer.Serialize(new[]
        {
            new { StepOrder = 1, Name = "a", SqlValue = "SELECT 1", DataSourceId = 20 },
            new { StepOrder = 2, Name = "b", SqlValue = "SELECT 2", DataSourceId = 30 }
        });

        var steps = MigrationQuerySteps.Parse(DeclaredSource, queryText);

        steps.Select(x => (x.DataSourceId, x.SqlValue)).Should().Equal((20, "SELECT 1"), (30, "SELECT 2"));
    }

    [Test]
    public void EmptyQueryText_HasNoSteps()
    {
        MigrationQuerySteps.Parse(DeclaredSource, " ").Should().BeEmpty();
    }

    [Test]
    public void SourceIds_ArePlainSqlsDeclaredSource()
    {
        MigrationQuerySteps.SourceIds(DeclaredSource, "SELECT id FROM accounts").Should().Equal(DeclaredSource);
    }

    [Test]
    public void SourceIds_AreTheDeclaredSourceAndEveryStepsDataSource()
    {
        var queryText = JsonSerializer.Serialize(new[]
        {
            new { SqlValue = "SELECT 1", DataSourceId = 20 },
            new { SqlValue = "SELECT 2", DataSourceId = 20 },
            new { SqlValue = "SELECT 3", DataSourceId = 30 }
        });

        MigrationQuerySteps.SourceIds(DeclaredSource, queryText).Should().Equal(DeclaredSource, 20, 30);
    }

    // Every data source a run reads is one its access was decided on, in both forms.
    [TestCase("SELECT id FROM accounts")]
    [TestCase("[{\"SqlValue\":\"SELECT 1\",\"DataSourceId\":20},{\"SqlValue\":\"SELECT 2\",\"DataSourceId\":30}]")]
    public void EveryDataSourceARunReads_IsOneAccessWasDecidedOn(string queryText)
    {
        var decided = MigrationQuerySteps.SourceIds(DeclaredSource, queryText);

        MigrationQuerySteps.Parse(DeclaredSource, queryText)
            .Select(x => x.DataSourceId)
            .Should().OnlyContain(x => decided.Contains(x));
    }
}
