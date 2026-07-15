using FluentAssertions;
using NUnit.Framework;
using Beacon.AI.Services.Eval;

namespace Beacon.Tests.Unit;

/// <summary>
/// Unit coverage for the eval harness result-set fingerprint (SC6). The fingerprint is the basis of
/// execution-accuracy scoring: equal result sets (as a multiset of rows) must fingerprint equal,
/// while any differing cell, extra/missing row, or differing column must fingerprint differently.
/// Empty result sets are handled deterministically. No DB / LLM involved.
/// </summary>
[TestFixture]
public class ResultSetFingerprintTests
{
    private static List<Dictionary<string, object?>> Rows(params (string Col, object? Value)[][] rows)
    {
        return rows
            .Select(r => r.ToDictionary(x => x.Col, x => x.Value))
            .ToList();
    }

    [Test]
    public void Compute_IdenticalResultSets_AreEqual()
    {
        var a = Rows(
            [("id", 1), ("name", "alice")],
            [("id", 2), ("name", "bob")]);
        var b = Rows(
            [("id", 1), ("name", "alice")],
            [("id", 2), ("name", "bob")]);

        ResultSetFingerprint.Compute(a).Should().Be(ResultSetFingerprint.Compute(b));
    }

    [Test]
    public void Compute_SameRowsDifferentOrder_AreEqual()
    {
        var a = Rows(
            [("id", 1), ("name", "alice")],
            [("id", 2), ("name", "bob")]);
        var b = Rows(
            [("id", 2), ("name", "bob")],
            [("id", 1), ("name", "alice")]);

        // Multiset semantics: row order must not change the fingerprint.
        ResultSetFingerprint.Compute(a).Should().Be(ResultSetFingerprint.Compute(b));
    }

    [Test]
    public void Compute_DifferingCellValue_IsDifferent()
    {
        var a = Rows([("id", 1), ("name", "alice")]);
        var b = Rows([("id", 1), ("name", "ALICE")]);

        ResultSetFingerprint.Compute(a).Should().NotBe(ResultSetFingerprint.Compute(b));
    }

    [Test]
    public void Compute_ExtraRow_IsDifferent()
    {
        var a = Rows([("id", 1)]);
        var b = Rows([("id", 1)], [("id", 2)]);

        ResultSetFingerprint.Compute(a).Should().NotBe(ResultSetFingerprint.Compute(b));
    }

    [Test]
    public void Compute_DifferingColumns_IsDifferent()
    {
        var a = Rows([("id", 1), ("name", "alice")]);
        var b = Rows([("id", 1), ("email", "alice")]);

        ResultSetFingerprint.Compute(a).Should().NotBe(ResultSetFingerprint.Compute(b));
    }

    [Test]
    public void Compute_NullAndNonNullCell_AreDifferent()
    {
        var a = Rows([("id", 1), ("name", null)]);
        var b = Rows([("id", 1), ("name", "")]);

        ResultSetFingerprint.Compute(a).Should().NotBe(ResultSetFingerprint.Compute(b));
    }

    [Test]
    public void Compute_EmptyResultSets_AreEqualAndStable()
    {
        var empty1 = new List<Dictionary<string, object?>>();
        var empty2 = new List<Dictionary<string, object?>>();

        ResultSetFingerprint.Compute(empty1).Should().Be(ResultSetFingerprint.Compute(empty2));
        ResultSetFingerprint.Compute(null).Should().Be(ResultSetFingerprint.Compute(empty1));
    }

    [Test]
    public void Compute_EmptyVersusNonEmpty_IsDifferent()
    {
        var empty = new List<Dictionary<string, object?>>();
        var nonEmpty = Rows([("id", 1)]);

        ResultSetFingerprint.Compute(empty).Should().NotBe(ResultSetFingerprint.Compute(nonEmpty));
    }
}
