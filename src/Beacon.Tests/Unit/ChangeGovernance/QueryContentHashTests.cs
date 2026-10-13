using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The content hash covers exactly what a run executes — the steps in run order with their names, data sources, SQL and
/// parameters (name, type, placeholder), and the final query — and reads the same from the stored steps, an edit and a
/// version's snapshot, so a version recorded from one compares with the others. Every character of the SQL is content,
/// whitespace and line endings included.
/// </summary>
[TestFixture]
public class QueryContentHashTests
{
    [Test]
    public void TheSameContent_HashesAlike_FromTheStoredSteps_AnEdit_AndASnapshot()
    {
        var stored = QueryContentHash.Of(new Query
        {
            Name = "Orders",
            FinalQuery = "SELECT * FROM @result1",
            Steps =
            [
                new QueryStep
                {
                    QueryId = 1,
                    StepOrder = 1,
                    DataSourceId = 3,
                    Name = "Step 1",
                    Description = "stored description",
                    SqlValue = "SELECT {n}",
                    Parameters = [new QueryStepParameter { QueryStepId = 1, Name = "n", Type = ParameterType.Number, Placeholder = "{n}", Description = "a" }]
                }
            ]
        });
        var edit = QueryContentHash.Of("SELECT * FROM @result1", [Step()]);
        var snapshot = QueryContentHash.Of("SELECT * FROM @result1", [new QueryStepSnapshot
        {
            StepOrder = 1,
            DataSourceId = 3,
            DataSourceName = "renamed warehouse",
            Name = "Step 1",
            SqlValue = "SELECT {n}",
            Parameters = [new QueryStepParameterSnapshot { Name = "n", Type = ParameterType.Number, Placeholder = "{n}" }]
        }]);

        stored.Hash.Should().MatchRegex("^[0-9a-f]{64}$");
        edit.Hash.Should().Be(stored.Hash, "descriptions are not executable");
        snapshot.Hash.Should().Be(stored.Hash, "a data source's display name is not executable");
    }

    [Test]
    public void AMissingFinalQueryOrStepName_IsTheSameAsAnEmptyOne_AndParameterOrderDoesNotMatter()
    {
        var withNulls = QueryContentHash.Of(null, [Step(name: null!, parameters: [Parameter("a"), Parameter("b")])]);
        var withEmpty = QueryContentHash.Of(string.Empty, [Step(name: string.Empty, parameters: [Parameter("b"), Parameter("a")])]);

        withEmpty.SameAs(withNulls).Should().BeTrue();
    }

    [Test]
    public void EveryExecutablePart_ChangesTheHash()
    {
        var baseline = QueryContentHash.Of(null, [Step()]).Hash;

        new[]
            {
                QueryContentHash.Of("SELECT 1", [Step()]),
                QueryContentHash.Of(null, [Step(sql: "SELECT {n} + 1")]),
                QueryContentHash.Of(null, [Step(order: 2)]),
                QueryContentHash.Of(null, [Step(dataSourceId: 4)]),
                QueryContentHash.Of(null, [Step(name: "Totals")]),
                QueryContentHash.Of(null, [Step(parameters: [Parameter("m")])]),
                QueryContentHash.Of(null, [Step(parameters: [Parameter("n", ParameterType.String)])]),
                QueryContentHash.Of(null, [Step(parameters: [Parameter("n", placeholder: "{m}")])]),
                QueryContentHash.Of(null, [Step(parameters: [])]),
                QueryContentHash.Of(null, [Step(), Step(order: 2)]),
                QueryContentHash.Of(null, Array.Empty<QueryStepData>()),
            }
            .Select(x => x.Hash)
            .Should().NotContain(baseline).And.OnlyHaveUniqueItems();
    }

    // Steps hash in the order they run: by step order, whatever order they are listed or loaded in.
    [Test]
    public void TheSameSteps_HashAlike_InWhateverOrderTheyAreListed()
    {
        var first = Step(sql: "SELECT 1", parameters: []);
        var second = Step(order: 2, sql: "SELECT 2", parameters: []);

        QueryContentHash.Of(null, [second, first]).Hash.Should().Be(QueryContentHash.Of(null, [first, second]).Hash);
        StoredQuery((12, 2, "SELECT 2"), (11, 1, "SELECT 1")).Hash.Should().Be(StoredQuery((11, 1, "SELECT 1"), (12, 2, "SELECT 2")).Hash);
    }

    // Swapping two steps' orders changes the order they run in, and so the content.
    [Test]
    public void ASwapOfTwoStepsOrders_IsAChange()
    {
        var before = QueryContentHash.Of(null, [Step(sql: "SELECT 1", parameters: []), Step(order: 2, sql: "SELECT 2", parameters: [])]);
        var swapped = QueryContentHash.Of(null, [Step(order: 2, sql: "SELECT 1", parameters: []), Step(sql: "SELECT 2", parameters: [])]);

        swapped.SameAs(before).Should().BeFalse();
    }

    // Steps sharing an order (data written before orders were checked) keep their run order: as listed, and stored steps by
    // id, so swapping which of them runs first is a change.
    [Test]
    public void StepsSharingAnOrder_HashInTheOrderTheyRun()
    {
        var listed = QueryContentHash.Of(null, [Step(sql: "SELECT 1", parameters: []), Step(sql: "SELECT 2", parameters: [])]);
        var reversed = QueryContentHash.Of(null, [Step(sql: "SELECT 2", parameters: []), Step(sql: "SELECT 1", parameters: [])]);

        reversed.SameAs(listed).Should().BeFalse();
        StoredQuery((12, 1, "SELECT 2"), (11, 1, "SELECT 1")).Hash.Should().Be(listed.Hash, "stored steps sharing an order run by id");
    }

    // Every character the SQL holds is content, line endings included: inside a string literal a CRLF and an LF match
    // different values, so the SQL is hashed exactly as it is stored and runs.
    [Test]
    public void EveryCharacter_LineEndingsIncluded_IsContent()
    {
        var unix = QueryContentHash.Of("SELECT *\nFROM @result1", [Step(sql: "SELECT {n}\nFROM orders")]);

        new[]
            {
                QueryContentHash.Of("SELECT *\r\nFROM @result1", [Step(sql: "SELECT {n}\nFROM orders")]),
                QueryContentHash.Of("SELECT *\nFROM @result1", [Step(sql: "SELECT {n}\r\nFROM orders")]),
                QueryContentHash.Of("SELECT *\nFROM @result1", [Step(sql: "SELECT {n}\rFROM orders")]),
                QueryContentHash.Of("SELECT *\nFROM @result1", [Step(sql: "SELECT {n} \nFROM orders")]),
                QueryContentHash.Of("SELECT *\nFROM @result1", [Step(sql: "SELECT {n}\n\nFROM orders")]),
                QueryContentHash.Of("SELECT *\nFROM @result1", [Step(sql: "SELECT {n}\n\tFROM orders")]),
                QueryContentHash.Of("SELECT  *\nFROM @result1", [Step(sql: "SELECT {n}\nFROM orders")]),
            }
            .Should().OnlyContain(x => !x.SameAs(unix), "every character of the SQL is part of what runs");
    }

    // A literal whose CRLF became an LF matches other rows: the change has its own hash, so it needs its own review.
    [Test]
    public void ALineEndingChangedInsideAStringLiteral_IsAChange()
    {
        var windows = QueryContentHash.Of(null, [Step(sql: "SELECT id FROM notes WHERE body = 'paid\r\nlate'", parameters: [])]);
        var unix = QueryContentHash.Of(null, [Step(sql: "SELECT id FROM notes WHERE body = 'paid\nlate'", parameters: [])]);

        unix.SameAs(windows).Should().BeFalse();
    }

    // Two parameters of one name but different types bind differently: each is part of the content.
    [Test]
    public void SameNamedParametersOfDifferentTypes_AreDistinct()
    {
        var number = QueryContentHash.Of(null, [Step(parameters: [Parameter("n"), Parameter("n", ParameterType.String)])]);
        var twice = QueryContentHash.Of(null, [Step(parameters: [Parameter("n"), Parameter("n")])]);

        number.SameAs(twice).Should().BeFalse();
    }

    [Test]
    public void NoStepsAndNoFinalQuery_IsEmptyContent()
    {
        QueryContentHash.Of(null, Array.Empty<QueryStepData>()).IsEmpty.Should().BeTrue();
        QueryContentHash.Of("SELECT 1", Array.Empty<QueryStepData>()).IsEmpty.Should().BeFalse();
        QueryContentHash.Of(null, [Step()]).IsEmpty.Should().BeFalse();
    }

    [Test]
    public void AVersionsHash_IsItsStoredOne_OrItsSnapshots_AndNoneForASnapshotThatCannotBeRead()
    {
        var snapshot = """[{"stepOrder":1,"sqlValue":"SELECT 1","dataSourceId":3,"dataSourceName":"warehouse","name":"Step 1"}]""";

        QueryContentHash.OfVersion("stored", snapshot, null).Should().Be("stored");
        QueryContentHash.OfVersion(null, snapshot, null).Should().Be(QueryContentHash.Of(null, [Step(sql: "SELECT 1", parameters: [])]).Hash);
        QueryContentHash.OfVersion(null, "not json", null).Should().BeNull();
    }

    private static QueryStepData Step(
        int order = 1,
        int dataSourceId = 3,
        string name = "Step 1",
        string sql = "SELECT {n}",
        List<QueryStepParameterData>? parameters = null) =>
        new()
        {
            StepOrder = order,
            DataSourceId = dataSourceId,
            Name = name,
            Description = "edited description",
            SqlValue = sql,
            Parameters = parameters ?? [Parameter("n")]
        };

    // A stored query with one warehouse step per (id, order, SQL), listed in the order given.
    private static QueryContent StoredQuery(params (int Id, int Order, string Sql)[] steps) =>
        QueryContentHash.Of(new Query
        {
            Name = "Orders",
            Steps = steps
                .Select(x =>
                    new QueryStep
                    {
                        Id = x.Id,
                        QueryId = 1,
                        StepOrder = x.Order,
                        DataSourceId = 3,
                        Name = "Step 1",
                        SqlValue = x.Sql
                    })
                .ToList()
        });

    private static QueryStepParameterData Parameter(string name, ParameterType type = ParameterType.Number, string? placeholder = null) =>
        new()
        {
            Name = name,
            Type = type,
            Placeholder = placeholder ?? $"{{{name}}}",
            Description = "b"
        };
}
