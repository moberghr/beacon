using Beacon.Core.HostData;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// Bypass attempts against the host allow-list / exclusion / masking policy. The policy is the standard pilot
/// shape: dbo.Customer + lending.Loan allow-listed, AuditLog hidden, SSN / AccountNumber masked, secrets and
/// InternalNote excluded.
/// </summary>
[TestFixture]
public class HostQueryPolicyValidatorTests
{
    private static readonly HostExposurePolicy SqlServerPolicy = HostTestModelFactory.StandardPolicy();
    private static readonly HostExposurePolicy PostgresPolicy = HostTestModelFactory.StandardPolicy(npgsql: true);

    [TestCase("SELECT Id, Name FROM Customer")]
    [TestCase("SELECT c.Id, c.Name FROM dbo.Customer c")]
    [TestCase("SELECT [c].[Name] FROM [dbo].[Customer] AS [c]")]
    [TestCase("select name from CUSTOMER")]
    [TestCase("SELECT l.Amount, c.Name FROM lending.Loan l JOIN Customer c ON c.Id = l.CustomerId")]
    [TestCase("SELECT TOP (10) Name FROM Customer WITH (NOLOCK) ORDER BY Name")]
    [TestCase("SELECT DATEPART(year, GETDATE()) AS y, COUNT(*) AS n FROM Customer")]
    [TestCase("WITH big AS (SELECT CustomerId, SUM(Amount) AS total FROM lending.Loan GROUP BY CustomerId) SELECT b.total FROM big b")]
    [TestCase("SELECT Name FROM Customer WHERE Id IN (SELECT CustomerId FROM lending.Loan WHERE Amount > @minAmount)")]
    public void Validate_AllowedSqlServerQueries_Pass(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT Id FROM AuditLog")]
    [TestCase("SELECT Id FROM dbo.AuditLog")]
    [TestCase("SELECT Id FROM [AuditLog]")]
    [TestCase("SELECT Id FROM auditlog")]
    [TestCase("SELECT Id FROM lending.Customer")]
    [TestCase("SELECT Id FROM Loan")]
    [TestCase("SELECT Id FROM otherdb.dbo.Customer")]
    [TestCase("SELECT name FROM sys.tables")]
    [TestCase("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS")]
    [TestCase("SELECT c.Name FROM Customer c JOIN AuditLog a ON a.Id = c.Id")]
    [TestCase("SELECT Name FROM Customer WHERE EXISTS (SELECT 1 FROM AuditLog)")]
    [TestCase("SELECT x.Id FROM (SELECT Id FROM AuditLog) x")]
    [TestCase("WITH a AS (SELECT Id FROM AuditLog) SELECT Id FROM a")]
    [TestCase("SELECT Name FROM Customer UNION SELECT Payload FROM AuditLog")]
    [TestCase("SELECT (SELECT TOP 1 Payload FROM AuditLog) AS p FROM Customer")]
    public void Validate_NonAllowListedTable_IsRejected(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_CteNamedLikeHiddenTable_DoesNotShadowItInEarlierSibling()
    {
        // The first CTE's "AuditLog" is the real table (a later sibling is not in scope yet).
        var sql = "WITH a AS (SELECT Id FROM AuditLog), AuditLog AS (SELECT Id FROM Customer) SELECT Id FROM a";

        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_CteNameInNestedScope_DoesNotLeakToOuterQuery()
    {
        var sql = "SELECT x.Id FROM (WITH AuditLog AS (SELECT Id FROM Customer) SELECT Id FROM AuditLog) x, AuditLog";

        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT PasswordHash FROM Customer")]
    [TestCase("SELECT c.PasswordHash FROM Customer c")]
    [TestCase("SELECT [c].[passwordhash] FROM [dbo].[Customer] [c]")]
    [TestCase("SELECT dbo.Customer.Secret FROM dbo.Customer")]
    [TestCase("SELECT Name FROM Customer WHERE Secret LIKE 'a%'")]
    [TestCase("SELECT Name FROM Customer ORDER BY TokenCount")]
    [TestCase("SELECT LEN(Secret) AS n FROM Customer")]
    [TestCase("SELECT x.s FROM (SELECT Secret AS s FROM Customer) x")]
    [TestCase("WITH a AS (SELECT InternalNote AS n FROM Customer) SELECT n FROM a")]
    [TestCase("SELECT Name FROM Customer WHERE Id IN (SELECT Id FROM Customer WHERE PasswordHash IS NULL)")]
    public void Validate_ExcludedColumn_IsRejectedAnywhere(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("not exposed");
    }

    [TestCase("SELECT * FROM Customer")]
    [TestCase("SELECT c.* FROM Customer c")]
    [TestCase("SELECT x.Name FROM (SELECT * FROM Customer) x")]
    [TestCase("WITH a AS (SELECT * FROM Customer) SELECT Name FROM a")]
    [TestCase("SELECT * FROM lending.Loan")]
    public void Validate_Wildcards_AreRejectedWithColumnList(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("list the columns explicitly").And.NotContain("PasswordHash");
    }

    [TestCase("SELECT UnmappedColumn FROM Customer")]
    [TestCase("SELECT c.UnmappedColumn FROM Customer c")]
    [TestCase("SELECT Name FROM Customer WHERE Unmapped = 1")]
    [TestCase("SELECT z.Name FROM Customer c")]
    public void Validate_UnknownColumnOrQualifier_IsRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1') r")]
    [TestCase("SELECT Name FROM Customer WHERE dbo.fnSecret(Id) = 1")]
    [TestCase("SELECT x FROM dbo.tvf(1) t")]
    [TestCase("SELECT Name FROM Customer FOR JSON PATH")]
    [TestCase("SELECT Name INTO #copy FROM Customer")]
    [TestCase("DELETE FROM Customer")]
    [TestCase("SELECT Name FROM Customer; SELECT Payload FROM AuditLog")]
    [TestCase("this is not sql")]
    public void Validate_EscapeHatches_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT \"Name\" FROM \"Customer\"")]
    [TestCase("SELECT c.\"Name\" FROM public.\"Customer\" c")]
    [TestCase("SELECT l.\"Amount\" FROM lending.\"Loan\" l")]
    public void Validate_PostgresQuotedIdentifiers_Pass(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT \"Name\" FROM customer")]
    [TestCase("SELECT \"Name\" FROM \"CUSTOMER\"")]
    [TestCase("SELECT row_to_json(c) AS j FROM \"Customer\" c")]
    [TestCase("SELECT query_to_xml('select * from \"AuditLog\"', true, true, '') AS x")]
    [TestCase("SELECT pg_read_file('/etc/passwd') AS f")]
    [TestCase("SELECT \"PasswordHash\" FROM \"Customer\"")]
    public void Validate_PostgresBypasses_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_MaskedColumn_DirectProjection_IsMaskedByKey()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT c.Name, c.SSN FROM Customer c", SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().Contain("SSN");
    }

    [Test]
    public void Validate_MaskedColumn_ThroughAliasAndCte_PropagatesTheMask()
    {
        var sql = "WITH a AS (SELECT SSN AS kt FROM Customer) SELECT kt AS final_kt FROM a";

        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().Contain(["SSN", "kt", "final_kt"]);
    }

    [Test]
    public void Validate_MaskedColumn_InUnionSecondArm_MasksTheFirstArmName()
    {
        var sql = "SELECT Name FROM Customer UNION ALL SELECT AccountNumber FROM lending.Loan";

        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().Contain("Name");
    }

    [TestCase("SELECT UPPER(SSN) AS s FROM Customer")]
    [TestCase("SELECT SSN + '' AS s FROM Customer")]
    [TestCase("SELECT CONCAT(Name, SSN) AS s FROM Customer")]
    [TestCase("SELECT MAX(SSN) AS s FROM Customer")]
    [TestCase("SELECT CASE WHEN SSN LIKE '01%' THEN 1 ELSE 0 END AS s FROM Customer")]
    [TestCase("SELECT (SELECT TOP 1 SSN FROM Customer) AS s FROM lending.Loan")]
    [TestCase("SELECT x.a FROM (SELECT SSN FROM Customer) x(a)")]
    public void Validate_MaskedColumn_InsideExpression_IsRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_MaskedColumn_InCountOrPredicate_IsAllowed()
    {
        var result = HostQueryPolicyValidator.Validate(
            "SELECT COUNT(DISTINCT SSN) AS n FROM Customer WHERE SSN IS NOT NULL",
            SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [Test]
    public void Validate_DenyAllPolicy_RejectsEveryTable()
    {
        var policy = HostExposurePolicy.DenyAll(Beacon.Core.Data.Enums.DatabaseEngineType.MSSQL);

        HostQueryPolicyValidator.Validate("SELECT Id FROM Customer", policy).Allowed.Should().BeFalse();
    }
}
