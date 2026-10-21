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
    [TestCase("SELECT date_trunc('month', now()) AS m, count(*) AS n FROM public.\"Customer\" GROUP BY 1")]
    [TestCase("SELECT date_trunc('month', now()) AS m, count(*) AS n FROM public.\"Customer\" GROUP BY date_trunc('month', now())")]
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

    // PostgreSQL reads a bare GROUP BY name as a column of the tables read first and only then as an output alias, so an
    // alias over a table could stand for a column the policy excludes or the model does not map.
    [TestCase("SELECT count(*) AS \"InternalNote\", array_agg(\"Id\") AS ids FROM public.\"Customer\" GROUP BY \"InternalNote\"")]
    [TestCase("SELECT count(*) AS \"PasswordHash\" FROM public.\"Customer\" GROUP BY \"PasswordHash\"")]
    [TestCase("SELECT count(*) AS legacy_code, array_agg(\"Id\") AS ids FROM public.\"Customer\" GROUP BY legacy_code")]
    [TestCase("SELECT date_trunc('month', now()) AS m, count(*) AS n FROM public.\"Customer\" GROUP BY m")]
    [TestCase("SELECT count(*) AS k FROM public.\"Customer\" c JOIN lending.\"Loan\" l ON l.\"CustomerId\" = c.\"Id\" GROUP BY k")]
    [TestCase("SELECT count(*) AS k FROM public.\"Customer\" c, (SELECT 1 AS z) d GROUP BY k")]
    [TestCase("SELECT x.\"Name\", count(*) AS \"InternalNote\" FROM public.\"Customer\" x GROUP BY x.\"Name\", \"InternalNote\"")]
    public void Validate_PostgresGroupByOutputAliasOverATable_IsRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_PostgresGroupByOutputAliasOverATable_ExplainsTheAlternatives()
    {
        var result = HostQueryPolicyValidator.Validate(
            "SELECT date_trunc('month', now()) AS m, count(*) AS n FROM public.\"Customer\" GROUP BY m",
            PostgresPolicy);

        result.Error.Should().Contain("GROUP BY m").And.Contain("GROUP BY 1");
    }

    [TestCase("SELECT \"Name\", count(*) AS n FROM public.\"Customer\" GROUP BY \"Name\"")]
    [TestCase("SELECT upper(x.m) AS u, count(*) AS n FROM (SELECT \"Name\" AS m FROM public.\"Customer\") x GROUP BY u")]
    [TestCase("WITH a AS (SELECT \"Name\" AS nm FROM public.\"Customer\") SELECT lower(nm) AS l, count(*) AS n FROM a GROUP BY l")]
    [TestCase("SELECT 1 AS one GROUP BY one")]
    public void Validate_PostgresGroupByOutputAlias_WhenNoTableCouldHoldTheName_Passes(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE regclass 'public.\"AuditLog\"' IS NOT NULL")]
    [TestCase("SELECT regclass 'pg_authid' AS o")]
    [TestCase("SELECT CAST('postgres' AS regrole) AS o")]
    [TestCase("SELECT 'pg_catalog'::regnamespace AS o")]
    [TestCase("SELECT '{pg_authid}'::regclass[] AS o")]
    public void Validate_PostgresCatalogTypeLiterals_AreRejected(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("catalog types");
    }

    // The parser reads name 'text' as a column with a string alias; PostgreSQL reads it as a typed literal of that name.
    [TestCase("SELECT regrole 'postgres' FROM public.\"Customer\"")]
    [TestCase("SELECT regproc 'now'")]
    [TestCase("SELECT regnamespace 'pg_catalog'")]
    [TestCase("SELECT regtype 'int4'")]
    [TestCase("SELECT \"Name\" 'n' FROM public.\"Customer\"")]
    [TestCase("SELECT c.\"Name\" FROM public.\"Customer\" 'c'")]
    public void Validate_PostgresStringWrittenAsAName_IsRejected(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("typed literal");
    }

    [Test]
    public void Validate_SqlServerStringAlias_StillPasses()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT Name 'n' FROM dbo.Customer", SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT date '2024-01-01' AS d")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE now() - interval '30 days' < timestamp '2024-01-01 00:00'")]
    [TestCase("SELECT '42'::int AS n, CAST('2024-01-01' AS date) AS d")]
    public void Validate_PostgresTypedLiterals_OfOrdinaryTypes_Pass(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT repeat('x', 200000000) AS c FROM public.\"Customer\"")]
    [TestCase("SELECT lpad(\"Name\", 100000) AS c FROM public.\"Customer\"")]
    [TestCase("SELECT rpad(\"Name\", 100000, '*') AS c FROM public.\"Customer\"")]
    [TestCase("SELECT repeat('x', 1e9) AS c")]
    [TestCase("SELECT lpad('x', (100000)) AS c")]
    [TestCase("SELECT lpad('x', '100000'::int) AS c")]
    [TestCase("SELECT repeat('x', \"Id\") AS c FROM public.\"Customer\"")]
    [TestCase("SELECT repeat('x', @p0) AS c FROM public.\"Customer\"")]
    [TestCase("SELECT repeat('x', 100 * 1000) AS c")]
    [TestCase("SELECT format('%100000000s', 'x') AS c")]
    [TestCase("SELECT format('%1$-200000s', \"Name\") AS c FROM public.\"Customer\"")]
    [TestCase("SELECT format('%*s', 100000, 'x') AS c")]
    [TestCase("SELECT format(\"Name\", 1) AS c FROM public.\"Customer\"")]
    public void Validate_PostgresLengthArguments_AboveTheBound_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_LengthArgumentAboveTheBound_NamesTheBound()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT repeat('x', 200000000) AS c", PostgresPolicy);

        result.Error.Should().Contain("repeat").And.Contain("10000");
    }

    [TestCase("SELECT lpad(\"Id\"::text, 8, '0') AS c FROM public.\"Customer\"")]
    [TestCase("SELECT repeat('-', 20) AS c, rpad(\"Name\", 30) AS d FROM public.\"Customer\"")]
    [TestCase("SELECT lpad('x', 10000) AS c, repeat('x', -1) AS d")]
    [TestCase("SELECT format('%s-%s', \"Name\", \"Id\") AS c FROM public.\"Customer\"")]
    [TestCase("SELECT format('%-10s|%2$s %%', \"Name\", \"Id\") AS c FROM public.\"Customer\"")]
    [TestCase("SELECT regexp_replace(\"Name\", '\\s+', ' ', 'g') AS c FROM public.\"Customer\"")]
    public void Validate_PostgresLengthArguments_WithinTheBound_Pass(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT REPLICATE(CAST('x' AS varchar(max)), 200000000) AS c")]
    [TestCase("SELECT REPLICATE(Name, Id) AS c FROM dbo.Customer")]
    public void Validate_SqlServerLengthArguments_AboveTheBound_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [Test]
    public void Validate_SqlServerLengthArgument_WithinTheBound_Passes()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT REPLICATE('0', 5) + Name AS c FROM dbo.Customer", SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT count(*) AS k FROM public.\"Customer\" c JOIN lending.\"Loan\" l ON l.\"CustomerId\" = c.\"Id\" GROUP BY k")]
    [TestCase("SELECT count(*) AS k FROM public.\"Customer\" c CROSS JOIN lending.\"Loan\" l GROUP BY k")]
    [TestCase("SELECT d.z AS k, count(*) AS n FROM public.\"Customer\" c, (SELECT 1 AS z) d GROUP BY k")]
    public void Validate_PostgresGroupByOutputAliasOverJoinedTables_NamesTheAlias(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("GROUP BY k names an output column");
    }

    // The model maps every column the check knows, but the database can still hold columns the model does not map, so
    // an output alias over a table is refused whatever the policy excludes.
    [Test]
    public void Validate_PostgresGroupByOutputAliasOverATable_IsRejectedWithoutExcludedColumns()
    {
        var policy = HostTestModelFactory.Read(x => x.AllowTables("lending.Loan"), npgsql: true).Policy;

        var result = HostQueryPolicyValidator.Validate(
            "SELECT date_trunc('month', now()) AS m, count(*) AS n FROM lending.\"Loan\" GROUP BY m",
            policy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("GROUP BY m names an output column");
    }

    [Test]
    public void Validate_PostgresGroupByName_ThatIsAnExposedInputColumn_Passes()
    {
        var result = HostQueryPolicyValidator.Validate(
            "SELECT upper(\"Name\") AS \"Name\", count(*) AS n FROM public.\"Customer\" GROUP BY \"Name\"",
            PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT x.s AS k, count(*) AS n FROM (SELECT \"SSN\" AS s FROM public.\"Customer\") x GROUP BY k")]
    [TestCase("WITH a AS (SELECT \"SSN\" AS s FROM public.\"Customer\") SELECT s AS k, count(*) AS n FROM a GROUP BY k")]
    public void Validate_PostgresGroupByMaskedOutputAlias_OverDerivedRelations_IsRejected(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("Masked column 'k'");
    }

    [TestCase("SELECT CAST('x' AS _regclass) AS o")]
    [TestCase("SELECT CAST('x' AS \"regclass\") AS o")]
    [TestCase("SELECT CAST('x' AS pg_catalog.\"_regrole\") AS o")]
    [TestCase("SELECT CAST('x' AS pg_catalog.regclass) AS o")]
    [TestCase("SELECT ARRAY['x']::_regtype AS o")]
    [TestCase("SELECT REGCLASS 'pg_authid' AS o")]
    [TestCase("SELECT 'now'::regprocedure AS o")]
    [TestCase("SELECT '+'::regoper AS o")]
    [TestCase("SELECT 'english'::regconfig AS o")]
    [TestCase("SELECT 'simple'::regdictionary AS o")]
    [TestCase("SELECT 'C'::regcollation AS o")]
    [TestCase("SELECT 'pg_authid'::regclass::oid AS o")]
    public void Validate_PostgresCatalogTypes_InEveryForm_AreRejected(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeFalse();
        result.Error.Should().Contain("catalog types");
    }

    [Test]
    public void Validate_PostgresCastToATypeThatOnlyStartsLikeACatalogType_Passes()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT 'a'::regular_kind AS k", PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [Test]
    public void Validate_PostgresStringAliasAfterAs_IsRejected()
    {
        HostQueryPolicyValidator.Validate("SELECT \"Name\" AS 'n' FROM public.\"Customer\"", PostgresPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT repeat('x', 10001) AS c")]
    [TestCase("SELECT rpad('x', 10001) AS c")]
    [TestCase("SELECT lpad('x', 10001, '0') AS c")]
    [TestCase("SELECT repeat('x', 10000.5) AS c")]
    [TestCase("SELECT repeat('x', 1e100) AS c")]
    [TestCase("SELECT repeat('x', '100000') AS c")]
    [TestCase("SELECT repeat('x', \"Id\"::int) AS c FROM public.\"Customer\"")]
    [TestCase("SELECT REPEAT('x', 10001) AS c")]
    [TestCase("SELECT \"Name\" FROM public.\"Customer\" WHERE \"Id\" IN (SELECT length(repeat('x', 100000)))")]
    [TestCase("WITH a AS (SELECT repeat('x', 100000) AS c) SELECT c FROM a")]
    [TestCase("SELECT repeat(repeat('a', 10000), 10000) AS c")]
    [TestCase("SELECT repeat(\"Name\", 10) AS c FROM public.\"Customer\"")]
    [TestCase("SELECT repeat('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 100) AS c")]
    [TestCase("SELECT format('%10000s%10000s', 'a', 'b') AS c")]
    [TestCase("SELECT format('%10001s', 'x') AS c")]
    [TestCase("SELECT format('%-*s', 5, 'x') AS c")]
    [TestCase("SELECT format('%1$*2$s', 'x', 5) AS c")]
    [TestCase("SELECT format(E'%s\\n', 'x') AS c")]
    [TestCase("SELECT format($$%s$$, 'x') AS c")]
    [TestCase("SELECT format(NULL, 'x') AS c")]
    public void Validate_PostgresStringBuilders_OutsideTheBounds_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT repeat('x', 10000) AS c, rpad('x', 10000) AS d")]
    [TestCase("SELECT repeat('x', 5.5) AS c")]
    [TestCase("SELECT repeat('x', 1e2) AS c")]
    [TestCase("SELECT repeat('x', '5') AS c")]
    [TestCase("SELECT repeat('x', -5) AS c, lpad('x', -5) AS d, rpad('x', -5) AS e")]
    [TestCase("SELECT REPEAT('x', 5) AS c")]
    [TestCase("SELECT repeat('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 100) AS c")]
    [TestCase("SELECT lpad(\"Name\", 20, '.') AS c FROM public.\"Customer\"")]
    [TestCase("SELECT format('%10000s', 'x') AS c")]
    [TestCase("SELECT format('%5000s%5000s', 'a', 'b') AS c")]
    [TestCase("SELECT format('%0s', 'x') AS c")]
    public void Validate_PostgresStringBuilders_WithinTheBounds_Pass(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, PostgresPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [TestCase("SELECT format('%10000s%10000s', 'a', 'b') AS c", "add up to no more than 10000")]
    [TestCase("SELECT format('%*s', 5, 'x') AS c", "add up to no more than 10000")]
    [TestCase("SELECT format(\"Name\", 1) AS c FROM public.\"Customer\"", "format string of format() must be a plain string literal")]
    [TestCase("SELECT repeat(\"Name\", 10) AS c FROM public.\"Customer\"", "repeat() repeats must be a plain string literal of at most 100 characters")]
    public void Validate_PostgresStringBuilderRejection_ExplainsTheBound(string sql, string reason)
    {
        HostQueryPolicyValidator.Validate(sql, PostgresPolicy).Error.Should().Contain(reason);
    }

    [TestCase("SELECT REPLICATE('x', 10001) AS c")]
    [TestCase("SELECT REPLICATE('x', @p0) AS c")]
    [TestCase("SELECT REPLICATE('x', CAST(Id AS int)) AS c FROM dbo.Customer")]
    [TestCase("SELECT REPLICATE('x', '100000') AS c")]
    [TestCase("SELECT REPLICATE(REPLICATE(CAST('a' AS varchar(max)), 10000), 10000) AS c")]
    [TestCase("SELECT REPLICATE(Name, 5) AS c FROM dbo.Customer")]
    [TestCase("SELECT FORMAT(Id, 'N999999999') AS c FROM dbo.Customer")]
    [TestCase("SELECT FORMAT(Id, 'D1000') AS c FROM dbo.Customer")]
    [TestCase("SELECT FORMAT(Id, Name) AS c FROM dbo.Customer")]
    public void Validate_SqlServerStringBuilders_OutsideTheBounds_AreRejected(string sql)
    {
        HostQueryPolicyValidator.Validate(sql, SqlServerPolicy).Allowed.Should().BeFalse();
    }

    [TestCase("SELECT REPLICATE('x', 10000) AS c")]
    [TestCase("SELECT REPLICATE('x', -5) AS c, REPLICATE('x', '5') AS d, REPLICATE('x', CAST(5 AS int)) AS e")]
    [TestCase("SELECT REPLICATE(N'ab', 3) AS c")]
    [TestCase("SELECT FORMAT(GETDATE(), 'yyyy-MM-dd') AS c")]
    [TestCase("SELECT FORMAT(Id, 'N2') AS c, FORMAT(Id, 'D999') AS d, FORMAT(Id, '#,##0.0000') AS e FROM dbo.Customer")]
    public void Validate_SqlServerStringBuilders_WithinTheBounds_Pass(string sql)
    {
        var result = HostQueryPolicyValidator.Validate(sql, SqlServerPolicy);

        result.Allowed.Should().BeTrue(result.Error);
    }

    [Test]
    public void Validate_SqlServerFormatPrecisionAboveTheBound_NamesTheBound()
    {
        var result = HostQueryPolicyValidator.Validate("SELECT FORMAT(Id, 'N999999999') AS c FROM dbo.Customer", SqlServerPolicy);

        result.Error.Should().Contain("no number above 999");
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
