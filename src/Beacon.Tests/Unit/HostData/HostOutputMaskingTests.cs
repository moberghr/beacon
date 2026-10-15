using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// Host column masking by output POSITION, for both dialects the host policy supports: the policy reports which output
/// positions carry masked values (renamed, duplicated, reordered and case-varied aliases alike), and
/// <see cref="HostResultMasker"/> masks those positions whatever the database named the columns — a column not named as
/// the policy derived it is masked too, and a result with another column count is discarded. On PostgreSQL an identifier
/// over 63 bytes is named as the server shortens it, two identifiers the shortening makes one are refused, and an unquoted
/// non-ASCII letter that changes case is refused. Masked columns: SSN (Customer) and AccountNumber (Loan).
/// </summary>
[TestFixture]
public class HostOutputMaskingTests
{
    private const string Ssn = "0101302989";

    private static readonly HostExposurePolicy SqlServerPolicy = HostTestModelFactory.StandardPolicy();
    private static readonly HostExposurePolicy PostgresPolicy = HostTestModelFactory.StandardPolicy(npgsql: true);

    // --- the positions the policy reports ---------------------------------------------------------------------------

    // Expected outputs: the derived name ("?" when the engine makes one up), "*" when the position is masked.
    [TestCase(false, "SELECT SSN AS kt FROM dbo.Customer", new[] { "kt*" })]
    [TestCase(false, "SELECT Name, SSN FROM dbo.Customer", new[] { "Name", "SSN*" })]
    [TestCase(false, "SELECT SSN, Name FROM dbo.Customer", new[] { "SSN*", "Name" })]
    [TestCase(false, "SELECT SSN AS x, Name AS x FROM dbo.Customer", new[] { "x*", "x" })]
    [TestCase(false, "SELECT Name AS x, SSN AS x FROM dbo.Customer", new[] { "x", "x*" })]
    [TestCase(false, "SELECT SSN AS a, Name AS A FROM dbo.Customer", new[] { "a*", "A" })]
    [TestCase(false, "SELECT UPPER(Name), SSN FROM dbo.Customer", new[] { "?", "SSN*" })]
    [TestCase(false, "SELECT Name FROM dbo.Customer UNION ALL SELECT AccountNumber FROM lending.Loan", new[] { "Name*" })]
    [TestCase(false, "WITH a AS (SELECT SSN AS kt, Name FROM dbo.Customer) SELECT Name AS kt, kt AS n FROM a", new[] { "kt", "n*" })]
    [TestCase(true, "SELECT \"SSN\" AS kt FROM public.\"Customer\"", new[] { "kt*" })]
    [TestCase(true, "SELECT \"Name\", \"SSN\" FROM public.\"Customer\"", new[] { "Name", "SSN*" })]
    [TestCase(true, "SELECT \"SSN\", \"Name\" FROM public.\"Customer\"", new[] { "SSN*", "Name" })]
    [TestCase(true, "SELECT \"SSN\" AS x, \"Name\" AS x FROM public.\"Customer\"", new[] { "x*", "x" })]
    [TestCase(true, "SELECT \"Name\" AS x, \"SSN\" AS x FROM public.\"Customer\"", new[] { "x", "x*" })]
    [TestCase(true, "SELECT \"SSN\" AS \"X\", \"Name\" AS x FROM public.\"Customer\"", new[] { "X*", "x" })]
    [TestCase(true, "SELECT \"SSN\" AS X, \"Name\" AS \"X\" FROM public.\"Customer\"", new[] { "x*", "X" })]
    [TestCase(true, "SELECT upper(\"Name\"), \"SSN\" FROM public.\"Customer\"", new[] { "?", "SSN*" })]
    [TestCase(true, "SELECT x.a, x.b FROM (SELECT \"Name\", \"SSN\" FROM public.\"Customer\") AS x(b, a)", new[] { "a*", "b" })]
    public void Validate_ReportsEachOutputPosition_AndWhetherItIsMasked(bool npgsql, string sql, string[] expected)
    {
        var result = HostQueryPolicyValidator.Validate(sql, npgsql ? PostgresPolicy : SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.OutputColumns!
            .Select(x => (x.Name ?? "?") + (x.Masked ? "*" : ""))
            .Should()
            .Equal(expected);
        result.Engine.Should().Be(npgsql ? DatabaseEngineType.PostgreSQL : DatabaseEngineType.MSSQL);
    }

    [Test]
    public void Validate_Rejection_ReportsNoOutputColumns()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT Name FROM dbo.Customer WHERE SSN = '1'", SqlServerPolicy);

        result.Allowed.Should().BeFalse();
        result.OutputColumns.Should().BeNull();
    }

    // --- PostgreSQL identifiers the server would rename ------------------------------------------------------------

    // An identifier over 63 bytes is read as PostgreSQL shortens it, wherever it stands; used the same way throughout, the
    // statement is allowed and its output named as the server names it.
    [TestCase("SELECT \"SSN\" AS {0} FROM public.\"Customer\"", true)]
    [TestCase("SELECT \"SSN\" AS \"{0}\" FROM public.\"Customer\"", true)]
    [TestCase("WITH c({0}) AS (SELECT \"SSN\" FROM public.\"Customer\") SELECT {0} FROM c", true)]
    [TestCase("SELECT d.{0} FROM (SELECT \"SSN\" FROM public.\"Customer\") AS d({0})", true)]
    [TestCase("SELECT {0}.\"Name\" FROM public.\"Customer\" {0}", false)]
    public void Validate_Postgres_AnIdentifierLongerThan63Bytes_IsReadAsTheServerShortensIt(string template, bool masked)
    {
        var result = HostQueryPolicyValidator.Validate(string.Format(template, new string('a', 64)), PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.OutputColumns.Should().ContainSingle()
            .Which.Should().Be(new HostOutputColumn(masked ? new string('a', 63) : "Name", masked));
    }

    // Two identifiers the shortening makes one — the same in their first 63 bytes, different after — would be read as one
    // by the server, so the statement is refused; at 63 bytes nothing is shortened and they stay two.
    [Test]
    public void Validate_Postgres_TwoIdentifiersTheShorteningMakesOne_AreRefused()
    {
        var prefix = new string('a', 63);

        var result = HostQueryPolicyValidator.Validate($"SELECT \"SSN\" AS {prefix}x, \"Name\" AS {prefix}y FROM public.\"Customer\"", PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("are the same in their first 63 bytes").And.EndWith("expose a view or rename the column on the host.");
    }

    [Test]
    public void Validate_Postgres_A63ByteIdentifierAndALongerOneSharingIt_AreRefused()
    {
        var prefix = new string('a', 63);

        var result = HostQueryPolicyValidator.Validate($"SELECT \"SSN\" AS {prefix}, \"Name\" AS {prefix}b FROM public.\"Customer\"", PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("are the same in their first 63 bytes");
    }

    [Test]
    public void Validate_Postgres_IdentifiersThatDifferWithin63Bytes_StayTwo()
    {
        var a = new string('a', 62) + "x" + "tail";
        var b = new string('a', 62) + "y" + "tail";

        var result = HostQueryPolicyValidator.Validate($"SELECT \"SSN\" AS {a}, \"Name\" AS {b} FROM public.\"Customer\"", PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.OutputColumns.Should().Equal(
            new HostOutputColumn(new string('a', 62) + "x", true),
            new HostOutputColumn(new string('a', 62) + "y", false));
    }

    [Test]
    public void Validate_Postgres_TheShorteningCountsBytes_OnACharacterBoundary()
    {
        // 32 characters, 64 bytes in UTF-8: the 32nd character would end at byte 64, so the server keeps 31 (62 bytes).
        var result = HostQueryPolicyValidator.Validate($"SELECT \"SSN\" AS {new string('é', 32)} FROM public.\"Customer\"", PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.OutputColumns.Should().Equal(new HostOutputColumn(new string('é', 31), true));
    }

    [TestCase(63, 63)]
    [TestCase(64, 63)]
    [TestCase(100, 63)]
    public void PostgresServerName_CutsAt63Bytes(int length, int kept)
    {
        HostQueryPolicyValidator.PostgresServerName(new string('a', length)).Should().HaveLength(kept);
    }

    [Test]
    public void Validate_Postgres_A63ByteAlias_IsAllowed_AndNamedInFull()
    {
        var alias = new string('a', 63);

        var result = HostQueryPolicyValidator.Validate($"SELECT \"SSN\" AS {alias} FROM public.\"Customer\"", PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.OutputColumns.Should().Equal(new HostOutputColumn(alias, true));
    }

    [TestCase("Kelvin")]
    [TestCase("ẞabc")]
    [TestCase("Šifra")]
    public void Validate_Postgres_AnUnquotedAliasWithANonAsciiCaseChange_IsRefused(string alias)
    {
        var result = HostQueryPolicyValidator.Validate($"SELECT \"SSN\" AS {alias} FROM public.\"Customer\"", PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("non-ASCII letter");
    }

    [Test]
    public void Validate_Postgres_ANonAsciiCaseChangeInAnyUnquotedIdentifier_IsRefused()
    {
        var sql = "WITH c(Kelvin) AS (SELECT \"SSN\" FROM public.\"Customer\") SELECT x AS kt FROM (SELECT Kelvin AS x FROM c) AS d";

        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("non-ASCII letter");
    }

    [TestCase("SELECT \"SSN\" AS \"Kelvin\" FROM public.\"Customer\"", "Kelvin")]
    [TestCase("SELECT \"SSN\" AS šifra FROM public.\"Customer\"", "šifra")]
    public void Validate_Postgres_AnIdentifierPostgresKeepsAsWritten_IsAllowed(string sql, string label)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.OutputColumns.Should().Equal(new HostOutputColumn(label, true));
    }

    [Test]
    public void Validate_SqlServer_LongAndNonAsciiAliases_AreNotRefused()
    {
        // SQL Server keeps an identifier up to 128 characters verbatim (a longer one is an error on the server) and compares
        // names case-insensitively; masking by position covers whatever it returns.
        var alias = new string('a', 100);

        var result = HostQueryPolicyValidator.Validate($"SELECT SSN AS [{alias}], Name AS [Kelvin] FROM dbo.Customer", SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.OutputColumns.Should().Equal(new HostOutputColumn(alias, true), new HostOutputColumn("Kelvin", false));
    }

    // --- masking a result by position ---------------------------------------------------------------------------------

    [TestCase(false, "SELECT SSN AS s, Name FROM dbo.Customer", new[] { "s", "Name" })]
    [TestCase(true, "SELECT \"SSN\" AS s, \"Name\" FROM public.\"Customer\"", new[] { "s", "Name" })]
    public void Mask_MasksTheMaskedPosition_FullyAndKeepsNulls(bool npgsql, string sql, string[] labels)
    {
        var rows = Rows([Ssn, "Anna"], [null, "Bo"]);

        var outcome = HostResultMasker.Mask(Check(sql, npgsql), true, labels, rows);

        outcome.Should().Be(new HostMaskOutcome(false, 2, 2, 0));
        rows[0].Should().Equal(HostResultMasker.MaskedValue, "Anna");
        rows[1].Should().Equal(new object?[] { null, "Bo" }, "null carries no value to hide");
    }

    // The database labels the masked column differently from the name the policy derived (a shortened or differently
    // folded alias): the position is masked all the same.
    [TestCase(false, "SELECT SSN AS s, Name FROM dbo.Customer", new[] { "s_as_the_server_named_it", "Name" })]
    [TestCase(true, "SELECT \"SSN\" AS s, \"Name\" FROM public.\"Customer\"", new[] { "S", "Name" })]
    [TestCase(true, "SELECT \"SSN\" AS s, \"Name\" FROM public.\"Customer\"", new[] { "Name", "Name" })]
    public void Mask_AMaskedColumnTheDatabaseNamedOtherwise_IsMasked(bool npgsql, string sql, string[] labels)
    {
        var rows = Rows([Ssn, "Anna"]);

        var outcome = HostResultMasker.Mask(Check(sql, npgsql), true, labels, rows);

        rows[0][0].Should().Be(HostResultMasker.MaskedValue);
        rows[0][1].Should().Be("Anna");
        outcome.RenamedColumns.Should().Be(1);
    }

    // An unmasked column not named as the policy derived it fails closed: masked too.
    [TestCase(false, "SELECT SSN AS s, Name FROM dbo.Customer", new[] { "s", "Nickname" })]
    [TestCase(true, "SELECT \"SSN\" AS s, \"Name\" FROM public.\"Customer\"", new[] { "s", "name" })]
    public void Mask_AnUnmaskedColumnNamedOtherwise_FailsClosed(bool npgsql, string sql, string[] labels)
    {
        var rows = Rows([Ssn, "Anna"]);

        var outcome = HostResultMasker.Mask(Check(sql, npgsql), true, labels, rows);

        rows[0].Should().Equal(HostResultMasker.MaskedValue, HostResultMasker.MaskedValue);
        outcome.Discarded.Should().BeFalse();
        outcome.RenamedColumns.Should().Be(1);
    }

    // An expression without an alias: the policy derives no name for it, so whatever the database labels it is not checked
    // (no rename is counted and nothing fails closed), and a position that carries no masked column stays as it is.
    [TestCase(false, "SELECT UPPER(Name), SSN FROM dbo.Customer", new[] { "", "SSN" })]
    [TestCase(false, "SELECT UPPER(Name), SSN FROM dbo.Customer", new[] { "anything", "SSN" })]
    [TestCase(true, "SELECT upper(\"Name\"), \"SSN\" FROM public.\"Customer\"", new[] { "upper", "SSN" })]
    [TestCase(true, "SELECT upper(\"Name\"), \"SSN\" FROM public.\"Customer\"", new[] { "?column?", "SSN" })]
    public void Mask_AnUnaliasedExpression_IsNotCheckedByNameAndNotMasked(bool npgsql, string sql, string[] labels)
    {
        var rows = Rows(["ANNA", Ssn]);

        var outcome = HostResultMasker.Mask(Check(sql, npgsql), true, labels, rows);

        outcome.Should().Be(new HostMaskOutcome(false, 2, 2, 0));
        rows[0].Should().Equal("ANNA", HostResultMasker.MaskedValue);
    }

    [TestCase(false, "SELECT SSN AS x, Name AS x FROM dbo.Customer", true)]
    [TestCase(false, "SELECT Name AS x, SSN AS x FROM dbo.Customer", false)]
    [TestCase(true, "SELECT \"SSN\" AS x, \"Name\" AS x FROM public.\"Customer\"", true)]
    [TestCase(true, "SELECT \"Name\" AS x, \"SSN\" AS x FROM public.\"Customer\"", false)]
    public void Mask_DuplicatedNames_AreMaskedByPosition(bool npgsql, string sql, bool ssnFirst)
    {
        var rows = ssnFirst ? Rows([Ssn, "Anna"]) : Rows(["Anna", Ssn]);

        var outcome = HostResultMasker.Mask(Check(sql, npgsql), true, ["x", "x"], rows);

        rows[0].Should().Equal(ssnFirst ? [HostResultMasker.MaskedValue, "Anna"] : new object?[] { "Anna", HostResultMasker.MaskedValue });
        outcome.RenamedColumns.Should().Be(0);
    }

    [TestCase(false, "SELECT Name, SSN FROM dbo.Customer", new[] { "Name", "SSN" })]
    [TestCase(true, "SELECT \"Name\", \"SSN\" FROM public.\"Customer\"", new[] { "Name", "SSN" })]
    public void Mask_ReorderedColumns_MaskTheMaskedPositionOnly(bool npgsql, string sql, string[] labels)
    {
        var rows = Rows(["Anna", Ssn]);

        HostResultMasker.Mask(Check(sql, npgsql), true, labels, rows);

        rows[0].Should().Equal("Anna", HostResultMasker.MaskedValue);
    }

    [Test]
    public void Mask_Postgres_CaseVariedNames_AreTwoColumns_OnlyTheMaskedOneMasked()
    {
        var rows = Rows([Ssn, "Anna"]);

        var outcome = HostResultMasker.Mask(Check("SELECT \"SSN\" AS \"X\", \"Name\" AS x FROM public.\"Customer\"", true), true, ["X", "x"], rows);

        rows[0].Should().Equal(HostResultMasker.MaskedValue, "Anna");
        outcome.RenamedColumns.Should().Be(0);
    }

    [Test]
    public void Mask_SqlServer_NamesCompareCaseInsensitively()
    {
        var rows = Rows([Ssn, "Anna"]);

        var outcome = HostResultMasker.Mask(Check("SELECT ssn, NAME FROM dbo.Customer", false), true, ["SSN", "Name"], rows);

        rows[0].Should().Equal(HostResultMasker.MaskedValue, "Anna");
        outcome.RenamedColumns.Should().Be(0, "SQL Server identifiers are case-insensitive");
    }

    [TestCase(false, "SELECT SSN, Name FROM dbo.Customer", new[] { "SSN", "Name", "Extra" })]
    [TestCase(true, "SELECT \"SSN\", \"Name\" FROM public.\"Customer\"", new[] { "SSN" })]
    public void Mask_AColumnCountMismatch_DiscardsTheResult(bool npgsql, string sql, string[] labels)
    {
        var rows = Rows(labels.Select(_ => (object?)Ssn).ToArray());

        var outcome = HostResultMasker.Mask(Check(sql, npgsql), true, labels, rows);

        outcome.Should().Be(new HostMaskOutcome(true, 2, labels.Length, 0));
    }

    [Test]
    public void Mask_AHostResultWithoutOutputColumns_IsDiscarded()
    {
        var rows = Rows([Ssn]);

        var outcome = HostResultMasker.Mask(new HostPolicyResult(true, null, []), true, ["SSN"], rows);

        outcome.Discarded.Should().BeTrue("a host data source's result is returned only against the policy's output columns");
    }

    [Test]
    public void Mask_AnOrdinarySourcesResult_IsLeftAsItIs()
    {
        var rows = Rows([Ssn, "Anna"]);

        var outcome = HostResultMasker.Mask(Check("SELECT SSN, Name FROM dbo.Customer", false), false, ["SSN"], rows);

        outcome.Should().Be(HostMaskOutcome.Unchanged);
        rows[0].Should().Equal(Ssn, "Anna");
    }

    [Test]
    public void Mask_NoRows_HasNothingToCheck()
    {
        var outcome = HostResultMasker.Mask(Check("SELECT SSN, Name FROM dbo.Customer", false), true, [], []);

        outcome.Should().Be(HostMaskOutcome.Unchanged);
    }

    private static HostPolicyResult Check(string sql, bool npgsql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, npgsql ? PostgresPolicy : SqlServerPolicy);
        result.Allowed.Should().BeTrue(result.Error);

        return result;
    }

    private static List<object?[]> Rows(params object?[][] rows) => rows.ToList();
}
