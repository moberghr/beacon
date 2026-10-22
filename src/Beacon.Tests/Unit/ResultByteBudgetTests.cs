using Beacon.Core.Services.Providers;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The byte estimate a result read is held to (<c>Beacon:Query:MaxResultBytes</c>): text two bytes a character, binary its
/// length, arrays, collections and dictionaries their elements, every value at least 8 bytes, each row 32 bytes plus 8 a
/// column, and the limit itself still within it.
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
        yield return Case("deeper than four levels", new object[] { new object[] { new object[] { new object[] { new object[] { new string('x', 1000) } } } } }, 16);
        yield return Case("null", null, 8);
        yield return Case("DBNull", DBNull.Value, 8);
        yield return Case("int", 5, 16);

        static TestCaseData Case(string name, object? value, long bytes) =>
            new TestCaseData(value, bytes).SetArgDisplayNames(name, bytes.ToString());
    }

    [TestCaseSource(nameof(Values))]
    public void AValue_IsEstimatedByWhatItHolds(object? value, long bytes)
    {
        ResultByteBudget.EstimatedBytes(value).Should().Be(bytes);
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
}
