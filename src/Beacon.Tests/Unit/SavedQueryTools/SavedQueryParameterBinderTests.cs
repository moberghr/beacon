using Beacon.Core.Data.Enums;
using Beacon.Core.SavedQueries;
using FluentAssertions;
using NUnit.Framework;
using static Beacon.Tests.Unit.SavedQueryTools.SavedQueryTestData;

namespace Beacon.Tests.Unit.SavedQueryTools;

[TestFixture]
public class SavedQueryParameterBinderTests
{
    [Test]
    public void Bind_ReplacesPlaceholdersWithDatabaseParameters_AndKeepsValuesOutOfTheSql()
    {
        var hostile = "x' OR '1'='1";
        var (sql, parameters) = SavedQueryParameterBinder.Bind(
            "SELECT * FROM loans WHERE region = {region} AND issued >= {from} AND issued < {from}::date + 1",
            [Parameter("region", ParameterType.String), Parameter("from", ParameterType.DateTime)],
            new Dictionary<string, object?> { ["region"] = hostile, ["from"] = new DateTime(2026, 1, 1) });

        sql.Should().Be("SELECT * FROM loans WHERE region = @p0 AND issued >= @p1 AND issued < @p1::date + 1");
        sql.Should().NotContain("OR '1'");
        parameters.Should().BeEquivalentTo(new Dictionary<string, object?> { ["p0"] = hostile, ["p1"] = new DateTime(2026, 1, 1) });
    }

    [Test]
    public void Bind_MatchesTheLongestPlaceholderFirst()
    {
        var from = Parameter("from", ParameterType.Number);
        var fromDate = Parameter("from_date", ParameterType.Number);
        from.Placeholder = "{f}";
        fromDate.Placeholder = "{f}x";

        var (sql, parameters) = SavedQueryParameterBinder.Bind(
            "SELECT {f}, {f}x",
            [from, fromDate],
            new Dictionary<string, object?> { ["from"] = 1L, ["from_date"] = 2L });

        sql.Should().Be("SELECT @p0, @p1");
        parameters["p1"].Should().Be(2L);
    }

    [Test]
    public void Bind_KeepsTypedValues()
    {
        var (_, parameters) = SavedQueryParameterBinder.Bind(
            "SELECT {n}",
            [Parameter("n", ParameterType.Number)],
            new Dictionary<string, object?> { ["n"] = 12.5m });

        parameters["p0"].Should().BeOfType<decimal>().Which.Should().Be(12.5m);
    }

    [Test]
    public void Bind_ThrowsForAMissingValue()
    {
        var bind = () => SavedQueryParameterBinder.Bind("SELECT {n}", [Parameter("n", ParameterType.Number)], new Dictionary<string, object?>());

        bind.Should().Throw<InvalidOperationException>().WithMessage("*'n'*");
    }
}
