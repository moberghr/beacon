using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// Bypass corpus against the host allow-list / exclusion / masking policy, for both dialects. The policy is the
/// standard pilot shape: dbo.Customer (public."Customer" on PostgreSQL) + lending.Loan allow-listed, AuditLog and the
/// LoanSummary view hidden, SSN / AccountNumber masked, PasswordHash / Secret / TokenCount (secret-like) and
/// InternalNote excluded. Every rejection case is a query an injected agent could send; every allowed case is a
/// legitimate analytics query that must keep working.
/// </summary>
[TestFixture]
public class HostQueryPolicyValidatorTests
{
    private static readonly HostExposurePolicy SqlServerPolicy = HostTestModelFactory.StandardPolicy();
    private static readonly HostExposurePolicy PostgresPolicy = HostTestModelFactory.StandardPolicy(npgsql: true);

    [TestCase("SELECT Id, Name FROM dbo.Customer")]
    [TestCase("SELECT c.Id, c.Name FROM dbo.Customer c")]
    [TestCase("SELECT [c].[Name] FROM [dbo].[Customer] AS [c]")]
    [TestCase("select name from DBO.CUSTOMER")]
    [TestCase("SELECT dbo.Customer.Name FROM dbo.Customer")]
    [TestCase("SELECT l.Amount, c.Name FROM lending.Loan l JOIN dbo.Customer c ON c.Id = l.CustomerId")]
    [TestCase("SELECT TOP (10) Name FROM dbo.Customer WITH (NOLOCK) ORDER BY Name")]
    [TestCase("SELECT Name FROM dbo.Customer WITH (INDEX(1), NOLOCK)")]
    [TestCase("SELECT DATEPART(year, GETDATE()) AS y, COUNT(*) AS n FROM dbo.Customer")]
    [TestCase("SELECT DATEADD(month, 1, GETDATE()) AS d, DATEDIFF(day, GETDATE(), GETUTCDATE()) AS n")]
    [TestCase("WITH big AS (SELECT CustomerId, SUM(Amount) AS total FROM lending.Loan GROUP BY CustomerId) SELECT b.total FROM big b ORDER BY b.total DESC")]
    [TestCase("WITH big (cid, total) AS (SELECT CustomerId, SUM(Amount) FROM lending.Loan GROUP BY CustomerId) SELECT cid, total FROM big")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE Id IN (SELECT CustomerId FROM lending.Loan WHERE Amount > @minAmount)")]
    [TestCase("SELECT c.Name FROM dbo.Customer c WHERE EXISTS (SELECT 1 FROM lending.Loan l WHERE l.CustomerId = c.Id)")]
    [TestCase("SELECT c.Name, x.total FROM dbo.Customer c CROSS APPLY (SELECT SUM(l.Amount) AS total FROM lending.Loan l WHERE l.CustomerId = c.Id) x")]
    [TestCase("SELECT c.Name, x.total FROM dbo.Customer c OUTER APPLY (SELECT TOP 1 l.Amount AS total FROM lending.Loan l WHERE l.CustomerId = c.Id ORDER BY l.Amount DESC) x")]
    [TestCase("SELECT Name AS n FROM dbo.Customer ORDER BY n")]
    [TestCase("SELECT Name, ROW_NUMBER() OVER (PARTITION BY Name ORDER BY Id) AS rn FROM dbo.Customer")]
    [TestCase("SELECT CustomerId, SUM(Amount) OVER (PARTITION BY CustomerId ORDER BY Id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS running FROM lending.Loan")]
    [TestCase("SELECT Name FROM dbo.Customer UNION ALL SELECT CAST(Amount AS nvarchar(20)) FROM lending.Loan ORDER BY 1")]
    [TestCase("SELECT TRY_CAST(l.Amount AS int) AS a, c.Name COLLATE Latin1_General_CS_AS AS n FROM lending.Loan l, dbo.Customer c")]
    [TestCase("SELECT CONVERT(nvarchar(10), Amount) AS a, IIF(Amount > 100, 'big', 'small') AS size, COALESCE(Amount, 0) AS z FROM lending.Loan")]
    [TestCase("SELECT v.x FROM (VALUES (1), (2)) AS v(x)")]
    [TestCase("SELECT x.a FROM (SELECT Name AS a FROM dbo.Customer) AS x")]
    [TestCase("SELECT x.a FROM (SELECT Name FROM dbo.Customer) AS x(a)")]
    [TestCase("SELECT COUNT(SSN) AS n FROM dbo.Customer WHERE SSN IS NOT NULL")]
    [TestCase("SELECT c.SSN, c.Name FROM dbo.Customer c WHERE c.Name LIKE 'A%'")]
    [TestCase("SELECT CASE WHEN SSN IS NULL THEN 'missing' ELSE 'present' END AS s FROM dbo.Customer")]
    [TestCase("/* totals */ SELECT Name -- the name\n FROM dbo.Customer")]
    [TestCase("WITH r (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 5) SELECT n FROM r")]
    [TestCase("SELECT c.Name FROM dbo.Customer c JOIN lending.Loan l ON l.CustomerId = c.Id WHERE l.Amount BETWEEN @p0 AND @p1")]
    [TestCase("SELECT Name, COUNT(*) AS n FROM dbo.Customer GROUP BY Name HAVING COUNT(*) > 1")]
    [TestCase("SELECT Name FROM dbo.Customer ORDER BY Name OFFSET 10 ROWS FETCH NEXT 10 ROWS ONLY")]
    [TestCase("SELECT JSON_VALUE(Name, '$.a') AS j, LEN(Name) AS n, UPPER(LEFT(Name, 1)) AS i FROM dbo.Customer")]
    public void Validate_LegitimateSqlServerQueries_Pass(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT \"Name\" FROM public.\"Customer\"")]
    [TestCase("SELECT c.\"Name\" FROM public.\"Customer\" c")]
    [TestCase("SELECT C.\"Name\" FROM \"public\".\"Customer\" AS c")]
    [TestCase("SELECT l.\"Amount\" FROM lending.\"Loan\" l")]
    [TestCase("SELECT c.\"Name\", l.\"Amount\" FROM public.\"Customer\" c JOIN lending.\"Loan\" l ON l.\"CustomerId\" = c.\"Id\" WHERE l.\"Amount\" > @p0 ORDER BY l.\"Amount\" DESC LIMIT 10")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE \"Id\" = @p0 AND \"Name\" <> @p1")]
    [TestCase("SELECT \"Id\" FROM public.\"Customer\" WHERE \"Id\" > 0 - @p0 AND \"Id\" < @p1::int")]
    [TestCase("SELECT date_trunc('month', now()) AS m, count(*) AS n FROM public.\"Customer\" GROUP BY m")]
    [TestCase("SELECT c.\"Name\", x.total FROM public.\"Customer\" c CROSS JOIN LATERAL (SELECT sum(l.\"Amount\") AS total FROM lending.\"Loan\" l WHERE l.\"CustomerId\" = c.\"Id\") x")]
    [TestCase("SELECT \"Id\", c.\"Name\" FROM public.\"Customer\" c JOIN lending.\"Loan\" l USING (\"Id\")")]
    [TestCase("WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r WHERE n < 5) SELECT n FROM r")]
    [TestCase("SELECT v.x FROM (VALUES (1), (2)) AS v(x)")]
    [TestCase("SELECT count(\"SSN\") AS n FROM public.\"Customer\" WHERE \"SSN\" IS NULL")]
    [TestCase("SELECT \"SSN\" AS s, \"Name\" FROM public.\"Customer\" ORDER BY \"Name\"")]
    [TestCase("SELECT \"Name\"::text AS n, coalesce(\"Name\", '') AS m, extract(year FROM now()) AS y FROM public.\"Customer\"")]
    [TestCase("SELECT DISTINCT ON (\"Name\") \"Name\", \"Id\" FROM public.\"Customer\" ORDER BY \"Name\", \"Id\"")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE \"Id\" = ANY(SELECT l.\"CustomerId\" FROM lending.\"Loan\" l)")]
    [TestCase("SELECT string_agg(\"Name\", ', ' ORDER BY \"Name\") AS names, percentile_cont(0.5) WITHIN GROUP (ORDER BY \"Id\") AS mid FROM public.\"Customer\"")]
    [TestCase("SELECT count(*) FILTER (WHERE \"Amount\" > 100) AS big FROM lending.\"Loan\"")]
    public void Validate_LegitimatePostgresQueries_Pass(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    // CRITICAL (review item 1): an output alias must never make an arbitrary identifier a valid column.
    [TestCase("SELECT Unmapped AS Unmapped FROM dbo.Customer")]
    [TestCase("SELECT Id AS Unmapped FROM dbo.Customer WHERE Unmapped = 'x'")]
    [TestCase("SELECT Id AS Unmapped FROM dbo.Customer ORDER BY Unmapped + ''")]
    [TestCase("SELECT Id AS x FROM dbo.Customer GROUP BY x")]
    [TestCase("SELECT Name AS Secret FROM dbo.Customer WHERE Secret LIKE 'a%'")]
    [TestCase("SELECT c AS c FROM dbo.Customer c")]
    [TestCase("SELECT x.Unmapped FROM (SELECT Id AS Unmapped FROM dbo.Customer) x WHERE Unmapped2 = 1")]
    [TestCase("SELECT Id AS Unmapped, (SELECT Unmapped FROM lending.Loan) AS u FROM dbo.Customer")]
    public void Validate_OutputAliasShadowing_IsRejected_SqlServer(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT c AS c FROM public.\"Customer\" c")]
    [TestCase("SELECT row_to_json(c) AS c FROM public.\"Customer\" c")]
    [TestCase("SELECT to_jsonb(c) AS j FROM public.\"Customer\" c")]
    [TestCase("SELECT json_agg(c) AS j FROM public.\"Customer\" c")]
    [TestCase("SELECT (c).\"PasswordHash\" AS s FROM public.\"Customer\" c")]
    [TestCase("SELECT \"Customer\" FROM public.\"Customer\"")]
    [TestCase("SELECT \"PasswordHash\"(c) AS s FROM public.\"Customer\" c")]
    [TestCase("SELECT \"Unmapped\" AS \"Unmapped\" FROM public.\"Customer\"")]
    [TestCase("SELECT \"Id\" AS unmapped FROM public.\"Customer\" WHERE unmapped = 'x'")]
    [TestCase("SELECT c.x FROM (SELECT \"Id\" AS x FROM public.\"Customer\") c WHERE c IS NOT NULL")]
    public void Validate_WholeRowAndAliasShadowing_IsRejected_Postgres(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT c AS c FROM dbo.Customer c", "whole-row")]
    [TestCase("SELECT Unmapped AS Unmapped FROM dbo.Customer", "not exposed")]
    public void Validate_ShadowingRejection_ExplainsWhy(string sql, string reason)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Error.Should().Contain(reason);
    }

    [TestCase("SELECT Id FROM AuditLog")]
    [TestCase("SELECT Id FROM dbo.AuditLog")]
    [TestCase("SELECT Id FROM [dbo].[AuditLog]")]
    [TestCase("SELECT Id FROM dbo.auditlog")]
    [TestCase("SELECT Id FROM lending.Customer")]
    [TestCase("SELECT Id FROM dbo.Loan")]
    [TestCase("SELECT CustomerId FROM dbo.LoanSummary")]
    [TestCase("SELECT Id FROM otherdb.dbo.Customer")]
    [TestCase("SELECT Id FROM [linked].otherdb.dbo.Customer")]
    [TestCase("SELECT name FROM sys.tables")]
    [TestCase("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS")]
    [TestCase("SELECT Name FROM dbo.CustomerSynonym")]
    [TestCase("SELECT c.Name FROM dbo.Customer c JOIN dbo.AuditLog a ON a.Id = c.Id")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE EXISTS (SELECT 1 FROM dbo.AuditLog)")]
    [TestCase("SELECT x.Id FROM (SELECT Id FROM dbo.AuditLog) x")]
    [TestCase("WITH a AS (SELECT Id FROM dbo.AuditLog) SELECT Id FROM a")]
    [TestCase("WITH AuditLog AS (SELECT Id FROM dbo.Customer) SELECT Id FROM dbo.AuditLog")]
    [TestCase("SELECT Name FROM dbo.Customer UNION SELECT Payload FROM dbo.AuditLog")]
    [TestCase("SELECT (SELECT TOP 1 Payload FROM dbo.AuditLog) AS p FROM dbo.Customer")]
    [TestCase("SELECT Name FROM dbo.Customer FOR SYSTEM_TIME ALL")]
    public void Validate_NonAllowListedTable_IsRejected(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeFalse();
    }

    // HIGH (review item 3): the connection's default schema / search_path may differ from the model's.
    [TestCase("SELECT Id, Name FROM Customer", "Qualify the table: dbo.Customer")]
    [TestCase("SELECT l.Amount FROM Loan l", "Qualify the table: lending.Loan")]
    [TestCase("SELECT Id FROM AuditLog", "not exposed")]
    public void Validate_UnqualifiedTable_IsRejectedWithTheQualifiedName(string sql, string message)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain(message);
    }

    [Test]
    public void Validate_UnqualifiedTable_Postgres_SuggestsTheQuotedName()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT \"Name\" FROM \"Customer\"", PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("Qualify the table: public.\"Customer\"");
    }

    // HIGH (review item 2): PostgreSQL CTE names follow identifier folding, so a differently-cased CTE never
    // shadows the real (hidden) table.
    [TestCase("WITH \"AuditLog\" AS (SELECT \"Id\" FROM public.\"Customer\") SELECT \"Id\" FROM auditlog")]
    [TestCase("WITH auditlog AS (SELECT \"Id\" FROM public.\"Customer\") SELECT \"Id\" FROM \"AuditLog\"")]
    [TestCase("WITH auditlog AS (SELECT \"Id\" FROM public.\"Customer\") SELECT \"Id\" FROM public.auditlog")]
    [TestCase("SELECT \"Id\" FROM public.\"AuditLog\"")]
    [TestCase("SELECT \"Name\" FROM public.customer")]
    [TestCase("SELECT \"Name\" FROM public.\"CUSTOMER\"")]
    [TestCase("SELECT tablename FROM pg_catalog.pg_tables")]
    [TestCase("SELECT table_name FROM information_schema.tables")]
    [TestCase("SELECT relname FROM pg_class")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" c JOIN public.\"LoanSummary\" s ON true")]
    public void Validate_PostgresTableBypasses_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_PostgresCteReference_MatchesTheFoldedName()
    {
        var sql = "WITH Big AS (SELECT \"CustomerId\" AS cid FROM lending.\"Loan\") SELECT cid FROM big";

        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeTrue();
    }

    [Test]
    public void Validate_CteNamedLikeHiddenTable_DoesNotShadowItInEarlierSibling()
    {
        // The first CTE's "AuditLog" is not yet in scope, so it cannot stand in for a real table.
        var sql = "WITH a AS (SELECT Id FROM AuditLog), AuditLog AS (SELECT Id FROM dbo.Customer) SELECT Id FROM a";

        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_CteNameInNestedScope_DoesNotLeakToOuterQuery()
    {
        var sql = "SELECT x.Id FROM (WITH AuditLog AS (SELECT Id FROM dbo.Customer) SELECT Id FROM AuditLog) x, AuditLog";

        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT PasswordHash FROM dbo.Customer")]
    [TestCase("SELECT c.PasswordHash FROM dbo.Customer c")]
    [TestCase("SELECT [c].[passwordhash] FROM [dbo].[Customer] [c]")]
    [TestCase("SELECT dbo.Customer.Secret FROM dbo.Customer")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE Secret LIKE 'a%'")]
    [TestCase("SELECT Name FROM dbo.Customer ORDER BY TokenCount")]
    [TestCase("SELECT LEN(Secret) AS n FROM dbo.Customer")]
    [TestCase("SELECT x.s FROM (SELECT Secret AS s FROM dbo.Customer) x")]
    [TestCase("WITH a AS (SELECT InternalNote AS n FROM dbo.Customer) SELECT n FROM a")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE Id IN (SELECT Id FROM dbo.Customer WHERE PasswordHash IS NULL)")]
    [TestCase("SELECT UnmappedColumn FROM dbo.Customer")]
    [TestCase("SELECT c.UnmappedColumn FROM dbo.Customer c")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE Unmapped = 1")]
    public void Validate_ExcludedOrUnknownColumn_IsRejectedAnywhere(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("not exposed");
    }

    [TestCase("SELECT \"PasswordHash\" FROM public.\"Customer\"")]
    [TestCase("SELECT c.ctid FROM public.\"Customer\" c")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE xmin::text <> ''")]
    [TestCase("SELECT name FROM public.\"Customer\"")]
    public void Validate_PostgresUnexposedColumn_IsRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    // Name resolution follows the server's scopes: unqualified names only in the SELECT's own FROM, correlated
    // references qualified, one relation per alias.
    [TestCase("SELECT z.Name FROM dbo.Customer c")]
    [TestCase("SELECT c.Name FROM dbo.Customer c WHERE EXISTS (SELECT 1 FROM lending.Loan l WHERE l.CustomerId = Name)")]
    [TestCase("SELECT c.Name FROM dbo.Customer c WHERE EXISTS (SELECT 1 FROM lending.Loan c WHERE c.Amount > 1)")]
    [TestCase("SELECT d.v FROM lending.Loan c, (SELECT c.Amount AS v) d")]
    [TestCase("SELECT Id FROM dbo.Customer c JOIN lending.Loan l ON l.CustomerId = c.Id")]
    [TestCase("SELECT x.Name FROM (SELECT Id FROM dbo.Customer) x")]
    [TestCase("SELECT x.a, x.b FROM (SELECT Name FROM dbo.Customer) AS x(a, b)")]
    public void Validate_ScopeViolations_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT * FROM dbo.Customer")]
    [TestCase("SELECT c.* FROM dbo.Customer c")]
    [TestCase("SELECT x.Name FROM (SELECT * FROM dbo.Customer) x")]
    [TestCase("WITH a AS (SELECT * FROM dbo.Customer) SELECT Name FROM a")]
    [TestCase("SELECT * FROM lending.Loan")]
    public void Validate_Wildcards_AreRejectedWithColumnList(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("list the columns explicitly").And.NotContain("PasswordHash").And.NotContain("InternalNote");
    }

    [TestCase("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1') r")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE dbo.fnSecret(Id) = 1")]
    [TestCase("SELECT x FROM dbo.tvf(1) t")]
    [TestCase("SELECT j.[key] FROM dbo.Customer c CROSS APPLY OPENJSON(c.Name) j")]
    [TestCase("SELECT c.Name.value('(/a)[1]', 'int') AS v FROM dbo.Customer c")]
    [TestCase("SELECT Name FROM dbo.Customer FOR JSON PATH")]
    [TestCase("SELECT Name INTO #copy FROM dbo.Customer")]
    [TestCase("DELETE FROM dbo.Customer")]
    [TestCase("SELECT Name FROM dbo.Customer; SELECT Payload FROM dbo.AuditLog")]
    [TestCase("SELECT Name FROM dbo.Customer; SELECT Name FROM dbo.Customer")]
    [TestCase("this is not sql")]
    [TestCase("SELECT @@VERSION AS v")]
    [TestCase("SELECT OBJECT_NAME(1) AS o")]
    [TestCase("SELECT SUSER_NAME() AS u")]
    [TestCase("SELECT customer_private_json(42) AS j")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE customer_private_json(Id) IS NOT NULL")]
    public void Validate_SqlServerEscapeHatches_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    // HIGH (review item 5): functions are allow-listed, not deny-listed.
    [TestCase("SELECT customer_private_json(42) AS j")]
    [TestCase("SELECT query_to_xml('select * from \"AuditLog\"', true, true, '') AS x")]
    [TestCase("SELECT pg_read_file('/etc/passwd') AS f")]
    [TestCase("SELECT current_setting('search_path') AS s")]
    [TestCase("SELECT version() AS v")]
    [TestCase("SELECT pg_catalog.lower(\"Name\") AS n FROM public.\"Customer\"")]
    [TestCase("SELECT \"LOWER\"(\"Name\") AS n FROM public.\"Customer\"")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE 'public.\"AuditLog\"'::regclass IS NOT NULL")]
    [TestCase("SELECT x FROM generate_series(1, 3) AS g(x)")]
    [TestCase("SELECT u.x FROM public.\"Customer\" c, unnest(ARRAY[1]) AS u(x)")]
    [TestCase("TABLE public.\"Customer\"")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" FOR UPDATE")]
    public void Validate_PostgresEscapeHatches_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_UnknownFunction_NamesTheAllowList()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT customer_private_json(42) AS j", PostgresPolicy);

        result.Error.Should().Contain("Function 'customer_private_json' is not on the host data source's function allow-list");
    }

    [Test]
    public void Validate_HostApprovedFunction_IsAllowed()
    {
        var policy = HostTestModelFactory.Read(x => x.AllowTables("Customer").AllowFunctions("customer_score"), npgsql: true).Policy;

        HostQueryPolicyValidator.Validate("SELECT customer_score(\"Id\") AS s FROM public.\"Customer\"", policy).Allowed.Should().BeTrue();
        HostQueryPolicyValidator.Validate("SELECT other_fn(\"Id\") AS s FROM public.\"Customer\"", policy).Allowed.Should().BeFalse();
    }

    [TestCase("pg_read_file")]
    [TestCase("xp_cmdshell")]
    [TestCase("row_to_json")]
    [TestCase("dbo.fn")]
    [TestCase("")]
    public void AllowFunctions_CatalogOrMalformedNames_AreRefusedAtConfiguration(string name)
    {
        var act = () => new HostDbContextOptions().AllowFunctions(name);

        act.Should().Throw<ArgumentException>();
    }

    // HIGH (review item 4): a masked column may be selected directly, COUNT()ed or IS [NOT] NULL-tested — nothing else.
    [TestCase("SELECT UPPER(SSN) AS s FROM dbo.Customer")]
    [TestCase("SELECT SSN + '' AS s FROM dbo.Customer")]
    [TestCase("SELECT CONCAT(Name, SSN) AS s FROM dbo.Customer")]
    [TestCase("SELECT MAX(SSN) AS s FROM dbo.Customer")]
    [TestCase("SELECT TRY_CAST(SSN AS int) AS s FROM dbo.Customer")]
    [TestCase("SELECT SSN COLLATE Latin1_General_BIN AS s FROM dbo.Customer")]
    [TestCase("SELECT CASE WHEN SSN LIKE '01%' THEN 1 ELSE 0 END AS s FROM dbo.Customer")]
    [TestCase("SELECT (SELECT TOP 1 SSN FROM dbo.Customer) AS s FROM lending.Loan")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE SSN LIKE '01%'")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE SSN = @p0")]
    [TestCase("SELECT COUNT(CASE WHEN SUBSTRING(SSN, 1, 1) = '1' THEN 1 END) AS n FROM dbo.Customer")]
    [TestCase("SELECT 1 / (CASE WHEN SSN LIKE '0%' THEN 0 ELSE 1 END) AS x FROM dbo.Customer")]
    [TestCase("SELECT COUNT(DISTINCT SSN) AS n FROM dbo.Customer")]
    [TestCase("SELECT Name FROM dbo.Customer ORDER BY SSN")]
    [TestCase("SELECT SSN FROM dbo.Customer ORDER BY 1")]
    [TestCase("SELECT SSN AS s FROM dbo.Customer ORDER BY s")]
    [TestCase("SELECT Name AS SSN FROM dbo.Customer ORDER BY SSN")]
    [TestCase("SELECT Name, COUNT(*) AS n FROM dbo.Customer GROUP BY SSN, Name")]
    [TestCase("SELECT Name FROM dbo.Customer GROUP BY Name HAVING MIN(SSN) > '5'")]
    [TestCase("SELECT c.Name FROM dbo.Customer c JOIN lending.Loan l ON l.AccountNumber = c.SSN")]
    [TestCase("SELECT ROW_NUMBER() OVER (ORDER BY SSN) AS r FROM dbo.Customer")]
    [TestCase("SELECT COUNT(SSN) OVER (PARTITION BY SSN) AS r FROM dbo.Customer")]
    [TestCase("SELECT DISTINCT SSN FROM dbo.Customer")]
    [TestCase("SELECT SSN FROM dbo.Customer UNION SELECT '0101302989'")]
    [TestCase("SELECT SSN FROM dbo.Customer INTERSECT SELECT '0101302989'")]
    [TestCase("SELECT Name FROM dbo.Customer WHERE '0101302989' IN (SELECT SSN FROM dbo.Customer)")]
    [TestCase("SELECT x.s FROM (SELECT SSN AS s FROM dbo.Customer) x WHERE x.s LIKE '0%'")]
    [TestCase("SELECT x.a FROM (SELECT SSN FROM dbo.Customer) x(a) WHERE x.a > '5'")]
    [TestCase("WITH a AS (SELECT SSN AS k FROM dbo.Customer) SELECT COUNT(*) AS n FROM a GROUP BY k")]
    [TestCase("SELECT c.Name FROM dbo.Customer c WHERE c.Id IN (SELECT l.CustomerId FROM lending.Loan l WHERE l.AccountNumber = @p0)")]
    [TestCase("WITH r (v, n) AS (SELECT CAST('' AS nvarchar(10)), 0 UNION ALL SELECT c.SSN, r.n + 1 FROM r JOIN dbo.Customer c ON c.Id = r.n + 1 WHERE r.n < 3) SELECT n FROM r WHERE v LIKE '0%'")]
    public void Validate_MaskedColumn_OutsideTheAllowedForms_IsRejected_SqlServer(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeFalse();
    }

    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE \"SSN\" LIKE '01%'")]
    [TestCase("SELECT count(CASE WHEN substring(\"SSN\", 1, 1) = '1' THEN 1 END) AS n FROM public.\"Customer\"")]
    [TestCase("SELECT count(DISTINCT \"SSN\") AS n FROM public.\"Customer\"")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" ORDER BY \"SSN\"")]
    [TestCase("SELECT \"SSN\" AS s FROM public.\"Customer\" GROUP BY s")]
    [TestCase("SELECT DISTINCT ON (\"SSN\") \"Name\" FROM public.\"Customer\"")]
    [TestCase("SELECT \"SSN\" FROM public.\"Customer\" EXCEPT SELECT '0101302989'")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE \"SSN\" = @p0")]
    [TestCase("SELECT \"SSN\"::text AS s FROM public.\"Customer\"")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE \"SSN\" ~ '^0'")]
    [TestCase("SELECT c.\"Name\" FROM public.\"Customer\" c NATURAL JOIN lending.\"Loan\" l")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE \"Id\" = (SELECT length(\"SSN\") FROM public.\"Customer\" LIMIT 1)")]
    public void Validate_MaskedColumn_OutsideTheAllowedForms_IsRejected_Postgres(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_MaskedPredicate_ExplainsTheAllowedForms()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT Name FROM dbo.Customer WHERE SSN LIKE '01%'", SqlServerPolicy);

        result.Error.Should().Contain("Masked column 'SSN' may only be selected directly");
    }

    [Test]
    public void Validate_MaskedColumn_DirectProjection_IsMaskedByKey()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT c.Name, c.SSN FROM dbo.Customer c", SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().BeEquivalentTo(["SSN"]);
    }

    [Test]
    public void Validate_MaskedColumn_ThroughAliasAndCte_MasksTheFinalKey()
    {
        var sql = "WITH a AS (SELECT SSN AS kt FROM dbo.Customer) SELECT kt AS final_kt FROM a";

        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().BeEquivalentTo(["final_kt"]);
    }

    [Test]
    public void Validate_MaskedColumn_ThroughDerivedColumnList_MasksTheRenamedKey()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT x.a FROM (SELECT SSN FROM dbo.Customer) x(a)", SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().BeEquivalentTo(["a"]);
    }

    [Test]
    public void Validate_MaskedColumn_InUnionAllSecondArm_MasksTheFirstArmName()
    {
        var sql = "SELECT Name FROM dbo.Customer UNION ALL SELECT AccountNumber FROM lending.Loan";

        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().BeEquivalentTo(["Name"]);
    }

    [Test]
    public void Validate_MaskedColumn_Postgres_MasksTheAliasKey()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT \"SSN\" AS s, \"Name\" FROM public.\"Customer\"", PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().BeEquivalentTo(["s"]);
    }

    [Test]
    public void Validate_MaskedColumn_InCountOrNullTest_IsAllowedAndNotMasked()
    {
        var result = HostQueryPolicyValidator.Validate(
            "SELECT COUNT(SSN) AS n FROM dbo.Customer WHERE SSN IS NOT NULL",
            SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.MaskedOutputColumns.Should().BeEmpty("a count carries no masked value");
    }

    // HIGH (review item 6): parameter placeholders are recognised per dialect and reported for binding.
    [Test]
    public void Validate_PostgresParameters_AreRecognised()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT \"Name\" FROM public.\"Customer\" WHERE \"Id\" = @p0 OR \"Id\" = @p1", PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.Parameters.Should().BeEquivalentTo(["p0", "p1"]);
        result.FindUnboundParameter(new Dictionary<string, object?> { ["p0"] = 1, ["p1"] = 2 }).Should().BeNull();
        result.FindUnboundParameter(new Dictionary<string, object?> { ["p0"] = 1 }).Should().Contain("'@p1' has no bound value");
    }

    [Test]
    public void Validate_SqlServerParameters_AreRecognised()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT Name FROM dbo.Customer WHERE Id = @p0", SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
        result.Parameters.Should().BeEquivalentTo(["p0"]);
    }

    [TestCase("SELECT @ \"Id\" AS a FROM public.\"Customer\"")]
    [TestCase("SELECT @\"Id\" AS a FROM public.\"Customer\"")]
    [TestCase("SELECT @secret AS a FROM public.\"Customer\"")]
    [TestCase("SELECT @(\"Id\") AS a FROM public.\"Customer\"")]
    [TestCase("SELECT @p0.x AS a FROM public.\"Customer\"")]
    public void Validate_PostgresAbsoluteValueOperator_IsNotMistakenForAParameter(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_DenyAllPolicy_RejectsEveryTable()
    {
        var policy = HostExposurePolicy.DenyAll(DatabaseEngineType.MSSQL);

        HostQueryPolicyValidator.Validate("SELECT Id FROM dbo.Customer", policy).Allowed.Should().BeFalse();
    }
}
