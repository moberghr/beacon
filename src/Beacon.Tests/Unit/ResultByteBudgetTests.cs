using System.Numerics;
using Beacon.Core.Services.Providers;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The byte estimate a result read is held to (<c>Beacon:Query:MaxResultBytes</c>): text two bytes a character, binary its
/// length, arrays, collections and dictionaries their elements however deeply nested, numbers, dates and identifiers a
/// fixed size, any other value its text, every value at least 8 bytes, each row 32 bytes plus 8 a column, and the limit
/// itself still within it. A walk ends once the value passes what is left of the limit, also on a structure holding itself.
/// </summary>
[TestFixture]
public class ResultByteBudgetTests
{
    private static IEnumerable<TestCaseData> Values()
    {
        yield return Case("text", "abcdef", 12);
        yield return Case("short text", "ab", 8);
        yield return Case("char array", "abcdef".ToCharArray(), 12);
        yield return Case("byte array", new byte[100], 100);
        yield return Case("text array", new[] { "abcdef", "ab" }, 20);
        yield return Case("nested array", new object[] { new[] { "abcdef" }, "abcdef" }, 24);
        yield return Case("dictionary", new Dictionary<string, object?> { ["key1"] = "abcdef" }, 20);
        yield return Case("list", new List<string> { "abcdef", "abcdef" }, 24);
        yield return Case("primitive array", new int[100], 400);
        yield return Case("deeply nested array", Nested(new string('x', 1000), 50), 2000);
        yield return Case("deeply nested dictionary", new Dictionary<string, object?> { ["k"] = Nested(new string('x', 1000), 20) }, 2008);
        yield return Case("empty array", Array.Empty<object>(), 8);
        yield return Case("nested empty arrays", new object[] { Array.Empty<object>(), Array.Empty<object>() }, 16);
        yield return Case("null", null, 8);
        yield return Case("DBNull", DBNull.Value, 8);
        yield return Case("int", 5, 16);
        yield return Case("decimal", 5.5m, 16);
        yield return Case("date", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 16);
        yield return Case("identifier", Guid.Empty, 16);
        yield return Case("big integer", BigInteger.Pow(2, 800), 101);
        yield return Case("other value by its text", new TextValue(new string('q', 5000)), 10_000);
        yield return Case("other value with short text", new TextValue("ab"), 8);

        static TestCaseData Case(string name, object? value, long bytes) =>
            new TestCaseData(value, bytes).SetArgDisplayNames(name, bytes.ToString());
    }

    internal static object Nested(object value, int levels)
    {
        var nested = value;
        for (var i = 0; i < levels; i++)
        {
            nested = new object[] { nested };
        }

        return nested;
    }

    [TestCaseSource(nameof(Values))]
    public void AValue_IsEstimatedByWhatItHolds(object? value, long bytes)
    {
        ResultByteBudget.EstimatedBytes(value).Should().Be(bytes);
    }

    [Test]
    public void AListHoldingItself_EndsTheWalk_AsOverTheLimit()
    {
        var list = new List<object?>();
        list.Add(list);

        var budget = new ResultByteBudget(1_000);

        budget.AddValue(list).Should().BeTrue();
        budget.Exceeded.Should().BeTrue();
    }

    [Test]
    public void AListHoldingItselfAndText_EndsTheWalk_AsOverTheLimit()
    {
        var list = new List<object?> { "abcdef" };
        list.Add(list);

        new ResultByteBudget(1_000).AddValue(list).Should().BeTrue();
    }

    [Test]
    public void AnEndlessSequence_StopsOnceItPassesWhatIsLeftOfTheLimit()
    {
        var budget = new ResultByteBudget(1_000);
        budget.AddRow(1);

        budget.AddValue(Endless()).Should().BeTrue();
        budget.Used.Should().BeInRange(1_001, 1_100, "the walk stops at the first element past the limit");

        static IEnumerable<string> Endless()
        {
            while (true)
            {
                yield return "abcdef";
            }
        }
    }

    [Test]
    public void ADeeplyNestedLargeText_PassesTheLimit()
    {
        var budget = new ResultByteBudget(10_000);

        budget.AddValue(Nested(new string('x', 10_000), 100)).Should().BeTrue();
    }

    [Test]
    public void ARow_CostsItsOverhead_AlsoWithNoColumnsOrOnlyNulls()
    {
        var budget = new ResultByteBudget(long.MaxValue);

        budget.Add([]);
        budget.Used.Should().Be(32);

        budget.Add([null, null]);
        budget.Used.Should().Be(32 + 32 + 16 + 8 + 8);
    }

    [Test]
    public void AResultExactlyAtTheLimit_IsWithinIt_AndOneByteMoreIsNot()
    {
        var budget = new ResultByteBudget(64);

        budget.Add([null, null]).Should().BeFalse("32 + 2 × 8 overhead and 2 × 8 for the nulls is exactly 64");
        budget.Exceeded.Should().BeFalse();

        budget.AddValue(null).Should().BeTrue();
        budget.Exceeded.Should().BeTrue();
    }

    private sealed class TextValue(string text)
    {
        public override string ToString() => text;
    }
}
