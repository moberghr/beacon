using Beacon.Core.Models;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Queries;
using Beacon.Core.Validators;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class QueryParameterPlaceholderTests
{
    [TestCase("{n}")]
    [TestCase("{start_date}")]
    [TestCase("{_x1}")]
    [TestCase("{A}")]
    public void AnIdentifierInBraces_IsValid(string placeholder)
    {
        QueryParameterPlaceholder.IsValid(placeholder).Should().BeTrue();
    }

    [TestCase("branch_id")]
    [TestCase("{ a }")]
    [TestCase("{a-b}")]
    [TestCase("'{a}'")]
    [TestCase("{}")]
    [TestCase("{1a}")]
    [TestCase("{a}{b}")]
    [TestCase("{a}\n")]
    [TestCase("@from")]
    [TestCase("")]
    [TestCase(null)]
    public void AnythingElse_IsInvalid(string? placeholder)
    {
        QueryParameterPlaceholder.IsValid(placeholder).Should().BeFalse();
    }

    [Test]
    public void TheIdentifier_IsCappedAt63Characters()
    {
        QueryParameterPlaceholder.IsValid("{a" + new string('b', 62) + "}").Should().BeTrue();
        QueryParameterPlaceholder.IsValid("{a" + new string('b', 63) + "}").Should().BeFalse();
    }

    [Test]
    public void ARefusal_NamesTheParameter_AndNoSql()
    {
        var refusal = QueryParameterPlaceholder.RefusalFor([("branch", "branch_id")]);

        refusal.Should().Be("Parameter 'branch' has an invalid placeholder; use {identifier} (letters, digits, underscore).");
    }

    [Test]
    public void TwoParametersOfAStep_CannotShareAPlaceholder()
    {
        var refusal = QueryParameterPlaceholder.RefusalFor([("a", "{x}"), ("b", "{x}")]);

        refusal.Should().Contain("'b'").And.Contain("already uses");
    }

    [Test]
    public void CheckForParameters_RefusesAnInvalidPlaceholder_EvenWhenTheSqlContainsIt()
    {
        var check = () => QueryValidator.CheckForParameters("SELECT branch_id FROM loans", [Parameter("branch", "branch_id")]);

        check.Should().Throw<BeaconException>().WithMessage("Parameter 'branch' has an invalid placeholder*");
    }

    [Test]
    public void CheckForParameters_RefusesTwoParametersSharingAPlaceholder()
    {
        var check = () => QueryValidator.CheckForParameters("SELECT {x}", [Parameter("a", "{x}"), Parameter("b", "{x}")]);

        check.Should().Throw<BeaconException>().WithMessage("Parameter 'b' uses a placeholder another parameter*");
    }

    [Test]
    public void CheckForParameters_StillRequiresEveryPlaceholderInTheSql()
    {
        var check = () => QueryValidator.CheckForParameters("SELECT 1", [Parameter("n", "{n}")]);

        check.Should().Throw<BeaconException>().WithMessage("*missing required parameters: {n}*");
    }

    [Test]
    public void CheckForParameters_AcceptsValidPlaceholdersPresentInTheSql()
    {
        var check = () => QueryValidator.CheckForParameters("SELECT {n}, {m}", [Parameter("n", "{n}"), Parameter("m", "{m}")]);

        check.Should().NotThrow();
    }

    private static QueryParameterData Parameter(string name, string placeholder) =>
        new() { Name = name, Type = ParameterType.Number, Description = "", Placeholder = placeholder };
}
