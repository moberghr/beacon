using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services.Security;

namespace Beacon.Tests.Unit;

[TestFixture]
public class PiiRowMaskerTests
{
    private const string RawEmail = "alice@example.com";

    private readonly QueryGuardrailService _guardrail = new();

    [Test]
    public void Mask_ResultColumnPassingIsPiiColumn_IsMaskedWithoutAnySqlMatch()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customer_email"] = RawEmail, ["name"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], true, null);

        masked.Should().ContainSingle();
        masked[0]["customer_email"].Should().Be("a***m");
        masked[0]["name"].Should().Be("Alice");
    }

    // End to end as production runs it: the SQL-text matches come from ValidateQuery ("email" — not a result key) and
    // the result key "contact" is not PII by name, so only the alias resolution can mask it.
    [TestCase("SELECT email AS contact, name FROM customers", "PostgreSQL")]
    [TestCase("SELECT c.email contact, c.name FROM customers c", "PostgreSQL")]
    [TestCase("SELECT lower(email) AS contact, name FROM customers", "PostgreSQL")]
    [TestCase("SELECT coalesce(trim(c.email), 'n/a') AS contact, name FROM customers c", "PostgreSQL")]
    [TestCase("SELECT contact = email, name FROM customers", "MSSQL")]
    [TestCase("SELECT [email] AS [contact], name FROM customers", "MSSQL")]
    [TestCase("SELECT `email` AS contact, name FROM customers", "MySQL")]
    [TestCase("WITH x AS (SELECT email AS contact, name FROM customers) SELECT contact, name FROM x", "PostgreSQL")]
    [TestCase("WITH x AS (SELECT email AS e, name FROM customers) SELECT e AS contact, name FROM x", "PostgreSQL")]
    [TestCase("SELECT d.contact, d.name FROM (SELECT email AS contact, name FROM customers) d", "PostgreSQL")]
    [TestCase("SELECT * FROM (SELECT email, name FROM customers) AS d(contact, name)", "PostgreSQL")]
    [TestCase("WITH x(contact, name) AS (SELECT email, name FROM customers) SELECT * FROM x", "PostgreSQL")]
    [TestCase("SELECT (SELECT max(email) FROM customers) AS contact, 'Alice' AS name", "PostgreSQL")]
    public void Mask_AliasedPiiColumn_IsMaskedViaTheExecutedSql(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = RawEmail, ["name"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["contact"].Should().Be("a***m");
        masked[0]["name"].Should().Be("Alice");
    }

    // A set operation names its result columns after the FIRST arm; PII in any arm at that position masks the name.
    [TestCase("SELECT name AS label FROM staff UNION ALL SELECT email AS label2 FROM customers")]
    [TestCase("SELECT name AS label FROM staff UNION SELECT email FROM customers")]
    [TestCase("SELECT email AS label FROM customers EXCEPT SELECT name FROM staff")]
    [TestCase("(SELECT name AS label FROM staff) INTERSECT (SELECT lower(email) AS l FROM customers)")]
    public void Mask_SetOperation_MasksTheFirstArmsNameAtAPiiPosition(string sql)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["label"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "PostgreSQL");

        masked[0]["label"].Should().Be("a***m");
    }

    [Test]
    public void Mask_NonPiiAliases_AreLeftUntouched()
    {
        const string sql = "SELECT name AS label, upper(city) AS town, 'email' AS kind, id AS ref, email AS contact FROM customers";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["label"] = "Alice", ["town"] = "ZAGREB", ["kind"] = "email", ["ref"] = 7, ["contact"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "PostgreSQL");

        masked[0]["label"].Should().Be("Alice");
        masked[0]["town"].Should().Be("ZAGREB");
        masked[0]["kind"].Should().Be("email", "a string literal is not a column reference");
        masked[0]["ref"].Should().Be(7);
        masked[0]["contact"].Should().Be("a***m");
    }

    [Test]
    public void Mask_AliasOverACustomPatternColumn_IsMasked()
    {
        const string sql = "SELECT iban AS account, name FROM payouts";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["account"] = "HR1210010051863000160", ["name"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], true, ["iban"], sql, "PostgreSQL");

        masked[0]["account"].Should().Be("H***0");
        masked[0]["name"].Should().Be("Alice");
    }

    // The join surfaces pass a step's PII result columns as sqlPiiColumns; the join SQL reads them back by name.
    [Test]
    public void Mask_PassedPiiColumns_SeedTheAliasResolution()
    {
        const string sql = "SELECT r1.contact AS who, lower(r1.contact) AS who_lc, r2.city FROM result1 r1 JOIN result2 r2 ON r1.address_id = r2.address_id";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["who"] = RawEmail, ["who_lc"] = RawEmail, ["city"] = "Zagreb" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, ["contact"], true, null, sql, "SQLite");

        masked[0]["who"].Should().Be("a***m");
        masked[0]["who_lc"].Should().Be("a***m");
        masked[0]["city"].Should().Be("Zagreb");
    }

    [Test]
    public void Mask_DetectionOff_ResolvesNoAliases()
    {
        const string sql = "SELECT email AS contact FROM customers";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], false, null, sql, "PostgreSQL");

        masked[0]["contact"].Should().Be(RawEmail);
    }

    // Without a parse, what a PII column is renamed to is unknown, so every result column is masked (fail closed).
    [Test]
    public void Mask_UnparseableSql_MasksEveryResultColumn()
    {
        const string sql = "SELECT email AS contact,, FROM";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = "Alice", ["customer_email"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], true, null, sql, "PostgreSQL");

        masked[0]["contact"].Should().Be("A***e");
        masked[0]["customer_email"].Should().Be("a***m");
    }

    [Test]
    public void Mask_SqlTooDeepToParse_MasksTheAliasedPiiColumn()
    {
        var sql = "SELECT email AS contact FROM customers" + string.Concat(Enumerable.Repeat(" UNION SELECT email FROM customers", 1_000));
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], true, null, sql, "PostgreSQL");

        masked[0]["contact"].Should().NotBe(RawEmail);
    }

    // A wildcard hides which position an explicit PII read lands in, so every result column is masked (fail closed).
    [TestCase("SELECT * FROM staff UNION ALL SELECT email FROM customers")]
    [TestCase("WITH x(label) AS (SELECT *, email FROM customers) SELECT label FROM x")]
    public void Mask_WildcardHidingAPiiPosition_MasksEveryResultColumn(string sql)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["label"] = RawEmail, ["city"] = "Zagreb" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "PostgreSQL");

        masked[0]["label"].Should().Be("a***m");
        masked[0]["city"].Should().Be("Z***b");
    }

    // A wildcard arm over a base table hides which position each table column lands in: any of them can be PII.
    [Test]
    public void Mask_WildcardSetOperationOverBaseTables_MasksEveryResultColumn()
    {
        const string sql = "SELECT * FROM staff UNION ALL SELECT * FROM contractors";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Alice", ["email"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "PostgreSQL");

        masked[0]["name"].Should().Be("A***e");
        masked[0]["email"].Should().Be("a***m");
    }

    // A single-arm SELECT * keeps the table's own column names as result keys, so only PII-named keys are masked.
    [Test]
    public void Mask_SingleArmWildcard_MasksOnlyPiiNamedColumns()
    {
        const string sql = "SELECT * FROM customers";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Alice", ["email"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "PostgreSQL");

        masked[0]["name"].Should().Be("Alice");
        masked[0]["email"].Should().Be("a***m");
    }

    // F-2: shapes that move a PII value to a name or position the resolver cannot follow. The result key named here
    // is the one that carries the PII value; it is not PII by its own name and is not a SQL-text match.
    [TestCase("SELECT id, x FROM customers AS c(id, x)", "PostgreSQL", "x")]
    [TestCase("SELECT x FROM customers c(id, x)", "PostgreSQL", "x")]
    [TestCase("SELECT 0 AS a, '' AS b UNION ALL SELECT * FROM customers", "PostgreSQL", "b")]
    [TestCase("SELECT * FROM staff UNION ALL SELECT * FROM customers", "PostgreSQL", "name")]
    [TestCase("SELECT id, name FROM staff UNION ALL SELECT * FROM (SELECT id, email FROM customers) z", "PostgreSQL", "name")]
    [TestCase("SELECT * FROM (SELECT * FROM customers) AS d(a, b)", "PostgreSQL", "b")]
    [TestCase("WITH x(a, b) AS (SELECT * FROM customers) SELECT * FROM x", "PostgreSQL", "b")]
    [TestCase("SELECT y FROM t CROSS JOIN LATERAL unnest(ARRAY[t.email]) AS u(y)", "PostgreSQL", "y")]
    [TestCase("SELECT y FROM t, unnest(ARRAY[t.email]) AS u(y)", "PostgreSQL", "y")]
    [TestCase("SELECT u FROM t CROSS JOIN unnest(ARRAY[t.email]) u", "PostgreSQL", "u")]
    [TestCase("SELECT kv.value FROM customers c CROSS JOIN LATERAL jsonb_each_text(to_jsonb(c)) AS kv", "PostgreSQL", "value")]
    [TestCase("SELECT row_to_json(c) AS j FROM customers c", "PostgreSQL", "j")]
    [TestCase("SELECT to_json(c.*) AS j FROM customers c", "PostgreSQL", "j")]
    [TestCase("SELECT row_to_json(customers) AS j FROM public.customers", "PostgreSQL", "j")]
    [TestCase("SELECT c FROM customers c", "PostgreSQL", "c")]
    [TestCase("SELECT OBJECT_CONSTRUCT(*) AS j FROM customers", "Snowflake", "j")]
    [TestCase("WITH a AS (SELECT id, name FROM staff), b AS (SELECT id, email FROM customers) SELECT * FROM (SELECT * FROM a JOIN b USING (id)) AS d(p, q, r)", "PostgreSQL", "r")]
    [TestCase("WITH a AS (SELECT lower(name) FROM staff) SELECT * FROM a UNION ALL SELECT email FROM customers", "PostgreSQL", "lower")]
    [TestCase("SELECT (SELECT row_to_json(x) FROM customers x LIMIT 1) AS j", "PostgreSQL", "j")]
    [TestCase("SELECT (SELECT max(x) FROM customers AS c(id, x)) AS j", "PostgreSQL", "j")]
    [TestCase("SELECT * FROM (VALUES ((SELECT email FROM customers LIMIT 1))) v", "PostgreSQL", "column1")]
    [TestCase("SELECT * RENAME (email AS contact) FROM customers", "Snowflake", "contact")]
    [TestCase("SELECT * REPLACE (email AS name) FROM customers", "BigQuery", "name")]
    [TestCase("SELECT row_to_json(c) FROM customers c", "PostgreSQL", "row_to_json")]
    [TestCase("SELECT to_json(c.*) FROM customers c", "PostgreSQL", "to_json")]
    [TestCase("SELECT c::text FROM customers c", "PostgreSQL", "c")]
    [TestCase("SELECT concat_ws(',', c.*) FROM customers c", "PostgreSQL", "concat_ws")]
    [TestCase("WITH w AS (SELECT row_to_json(c) FROM customers c) SELECT * FROM w", "PostgreSQL", "row_to_json")]
    [TestCase("SELECT lower(email) FROM customers", "PostgreSQL", "lower")]
    [TestCase("SELECT unnest(ARRAY[t.email]) FROM customers t", "PostgreSQL", "unnest")]
    [TestCase("SELECT (SELECT * FROM customers FOR JSON PATH)", "MSSQL", "Column1")]
    [TestCase("SELECT lower FROM (SELECT lower(email) FROM customers) d", "PostgreSQL", "lower")]
    [TestCase("WITH w AS (SELECT row_to_json(c) FROM customers c) SELECT row_to_json AS j FROM w", "PostgreSQL", "j")]
    [TestCase("SELECT e FROM customers LATERAL VIEW explode(emails) t AS e", "Databricks", "e")]
    [TestCase("SELECT val FROM customers UNPIVOT (val FOR col IN (email, phone)) u", "MSSQL", "val")]
    [TestCase("SELECT * FROM (SELECT kind, email FROM contacts) s PIVOT (MAX(email) FOR kind IN ([work], [home])) p", "MSSQL", "work")]
    public void Mask_RenameTheResolverCannotFollow_MasksThePiiCarryingColumn(string sql, string dialect, string piiKey)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { [piiKey] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0][piiKey].Should().Be("a***m");
    }

    // The controls: in each family above, a query the resolver can prove carries no PII stays unmasked.
    [TestCase("SELECT id, name FROM staff UNION ALL SELECT id, name FROM customers", "name")]
    [TestCase("SELECT id, name FROM staff UNION ALL VALUES (1, 'x')", "name")]
    [TestCase("WITH a AS (SELECT region, sum(total) AS total FROM sales GROUP BY region) SELECT * FROM a UNION ALL SELECT * FROM a", "region")]
    [TestCase("SELECT * FROM (SELECT id, name FROM staff) AS d(a, b)", "b")]
    [TestCase("WITH x AS (SELECT id, name FROM staff), y(a, b) AS (SELECT * FROM x) SELECT * FROM y", "b")]
    [TestCase("SELECT g FROM generate_series(1, 3) g", "g")]
    [TestCase("SELECT u FROM staff s CROSS JOIN LATERAL unnest(ARRAY[s.name]) u", "u")]
    [TestCase("SELECT row_to_json(d) AS j FROM (SELECT id, name FROM staff) d", "j")]
    [TestCase("SELECT count(*) AS n, c.name FROM customers c GROUP BY c.name", "name")]
    [TestCase("SELECT count(*) FILTER (WHERE status = 'x') AS n FROM orders", "n")]
    [TestCase("SELECT count(*) FROM customers", "count")]
    [TestCase("SELECT lower(name) FROM staff", "lower")]
    [TestCase("SELECT upper(c.name) FROM customers c", "upper")]
    [TestCase("SELECT s.name, (SELECT count(*) FROM customers) FROM staff s", "name")]
    [TestCase("WITH a AS (SELECT id, name FROM staff), b AS (SELECT id, name FROM staff) SELECT * FROM (SELECT * FROM a JOIN b USING (id)) AS d(p, q, r)", "r")]
    public void Mask_ShapeTheResolverCanProveCarriesNoPii_StaysUnmasked(string sql, string key)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { [key] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "PostgreSQL");

        masked[0][key].Should().Be("Alice");
    }

    // A CTE is visible only inside its own query: a same-named relation outside it is a base table again.
    [Test]
    public void Mask_CteNameReusedOutsideItsScope_IsABaseTableThere()
    {
        const string sql = "SELECT * FROM (WITH customers AS (SELECT 'x' AS n) SELECT * FROM customers) d UNION ALL SELECT * FROM customers";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["n"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "PostgreSQL");

        masked[0]["n"].Should().Be("a***m");
    }

    // A JSON document or map read by a literal PII key carries that value under the alias.
    [TestCase("SELECT profile ->> 'email' AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT profile -> 'email' AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT profile #>> '{work,email}' AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT profile['email'] AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT jsonb_extract_path_text(profile, 'contact', 'email') AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT JSON_VALUE(profile, '$.email') AS contact FROM users", "MSSQL")]
    [TestCase("SELECT JSON_QUERY(profile, '$.home_address') AS contact FROM users", "MSSQL")]
    [TestCase("SELECT profile ->> '$.email' AS contact FROM users", "MySQL")]
    [TestCase("SELECT JSON_UNQUOTE(JSON_EXTRACT(profile, '$.phone')) AS contact FROM users", "MySQL")]
    public void Mask_JsonReadByALiteralPiiKey_IsMasked(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = RawEmail, ["name"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["contact"].Should().Be("a***m");
        masked[0]["name"].Should().Be("Alice");
    }

    // A key that is not PII, or a key computed by an expression, is not followed.
    [TestCase("SELECT profile ->> 'nickname' AS label FROM users", "PostgreSQL")]
    [TestCase("SELECT profile ->> k.name AS label FROM users, keys k", "PostgreSQL")]
    [TestCase("SELECT profile ->> 'address_id' AS label FROM users", "PostgreSQL")]
    [TestCase("SELECT JSON_VALUE(profile, '$.nickname') AS label FROM users", "MSSQL")]
    [TestCase("SELECT profile ->> '$.nickname' AS label FROM users", "MySQL")]
    public void Mask_JsonReadByANonPiiOrComputedKey_StaysUnmasked(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["label"] = "Ally" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["label"].Should().Be("Ally");
    }

    // EXISTS / NOT EXISTS / IN (subquery) yield a boolean: no value of the subquery reaches the output.
    [TestCase("SELECT EXISTS (SELECT * FROM users u WHERE u.email = 'x') AS has_orders FROM orders", "PostgreSQL")]
    [TestCase("SELECT NOT EXISTS (SELECT email FROM users) AS has_orders", "PostgreSQL")]
    [TestCase("SELECT o.id IN (SELECT u.email FROM users u) AS has_orders FROM orders o", "PostgreSQL")]
    [TestCase("SELECT CASE WHEN EXISTS (SELECT * FROM users u WHERE u.email = o.ref) THEN 1 ELSE 0 END AS has_orders FROM orders o", "MSSQL")]
    [TestCase("SELECT CASE WHEN o.id IN (SELECT u.phone FROM users u) THEN 1 ELSE 0 END AS has_orders FROM orders o", "MSSQL")]
    public void Mask_ExistsOrInSubquery_DoesNotMaskTheBoolean(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["has_orders"] = "True" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["has_orders"].Should().Be("True");
    }

    // A scalar subquery's value is its own output, and the operand of an IN test is checked as before.
    [TestCase("SELECT (SELECT email FROM users LIMIT 1) AS has_orders", "PostgreSQL")]
    [TestCase("SELECT u.email IN (SELECT ref FROM orders) AS has_orders FROM users u", "PostgreSQL")]
    [TestCase("SELECT (SELECT TOP 1 email FROM users) AS has_orders", "MSSQL")]
    public void Mask_ScalarSubqueryOrPiiOperand_IsStillMasked(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["has_orders"] = "True" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["has_orders"].Should().Be("T***e");
    }

    // A column list on a built-in generator renames what its literal arguments produce, never table data.
    [TestCase("SELECT n FROM generate_series(1, 10) AS g(n)", "PostgreSQL")]
    [TestCase("SELECT n FROM unnest(ARRAY['a', 'b']) AS u(n)", "PostgreSQL")]
    [TestCase("SELECT n FROM GENERATE_SERIES(1, 10) AS g(n)", "MSSQL")]
    [TestCase("SELECT n FROM STRING_SPLIT('a,b', ',') AS s(n)", "MSSQL")]
    public void Mask_ColumnListOnABuiltInGenerator_MasksNothing(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["n"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["n"].Should().Be("Alice");
    }

    // An unknown function, or a generator fed from PII, keeps failing closed.
    [TestCase("SELECT n FROM my_rows(1) AS g(n)", "PostgreSQL")]
    [TestCase("SELECT n FROM users u CROSS JOIN LATERAL unnest(ARRAY[u.email]) AS x(n)", "PostgreSQL")]
    [TestCase("SELECT n FROM dbo.my_rows(1) AS g(n)", "MSSQL")]
    [TestCase("SELECT n FROM users u CROSS APPLY STRING_SPLIT(u.email, '@') AS s(n)", "MSSQL")]
    public void Mask_ColumnListOnAnUnknownOrPiiFedFunction_MasksEveryColumn(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["n"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["n"].Should().Be("A***e");
    }

    // A bare name equal to the table's name is a column where the engine reads it as one.
    [TestCase("SELECT tag FROM tag", "MSSQL")]
    [TestCase("SELECT tag FROM tag", "MySQL")]
    [TestCase("SELECT tag FROM tag", "SQLite")]
    [TestCase("WITH tag AS (SELECT name AS tag, email FROM users) SELECT tag FROM tag", "PostgreSQL")]
    [TestCase("SELECT tag FROM (SELECT name AS tag, email FROM users) AS tag", "PostgreSQL")]
    public void Mask_BareNameThatIsAColumn_IsNotAWholeRow(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["tag"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["tag"].Should().Be("Alice");
    }

    // PostgreSQL reads a bare relation name as its whole row when no column of that name is known.
    [TestCase("SELECT tag FROM tag", "PostgreSQL")]
    [TestCase("SELECT c FROM customers c", "PostgreSQL")]
    [TestCase("WITH tag AS (SELECT email FROM users) SELECT tag FROM tag", "PostgreSQL")]
    [TestCase("WITH tag AS (SELECT email AS tag FROM users) SELECT tag FROM tag", "PostgreSQL")]
    public void Mask_BareNameThatIsAWholeRow_IsMasked(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["tag"] = "Alice", ["c"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0].Values.Should().Contain("A***e");
    }

    // A plain derived table or a CTE body does not see a sibling FROM item, so a sibling's column of the same name does
    // not turn the relation's whole row into a column.
    [TestCase("SELECT j FROM (SELECT 1 AS c) x, (SELECT to_json(c) AS j FROM customers c) y", "PostgreSQL")]
    [TestCase("SELECT j FROM (SELECT 1 AS c) x, (SELECT row_to_json(c) AS j FROM customers c) y", "PostgreSQL")]
    [TestCase("WITH x AS (SELECT 1 AS c) SELECT j FROM x, (SELECT to_json(c) AS j FROM customers c) y", "PostgreSQL")]
    [TestCase("SELECT j FROM (SELECT 1 AS c) x, (WITH y AS (SELECT to_json(c) AS j FROM customers c) SELECT j FROM y) z", "PostgreSQL")]
    [TestCase("SELECT (SELECT j FROM (SELECT to_json(c) AS j FROM customers c) y) AS j FROM (SELECT 1 AS c) x", "PostgreSQL")]
    [TestCase("SELECT j FROM (SELECT 1 AS c) x, (SELECT OBJECT_CONSTRUCT(c) AS j FROM customers c) y", "Snowflake")]
    public void Mask_WholeRowBesideASiblingColumnOfTheSameName_IsMasked(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["j"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["j"].Should().Be("A***e");
    }

    // A LATERAL derived table or a scalar subquery sees the enclosing FROM items: the visible column wins on PostgreSQL.
    [TestCase("SELECT j FROM (SELECT 1 AS c) x, LATERAL (SELECT to_json(c) AS j FROM customers c) y")]
    [TestCase("SELECT (SELECT to_json(c) FROM customers c LIMIT 1) AS j FROM (SELECT 1 AS c) x")]
    public void Mask_BareNameThatIsAVisibleCorrelatedColumn_IsNotAWholeRow(string sql)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["j"] = "1" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "PostgreSQL");

        masked[0]["j"].Should().Be("1");
    }

    // Outside PostgreSQL and DuckDB the order between a column and a relation name is not relied on: a collision is read
    // as a whole row.
    [TestCase("BigQuery")]
    [TestCase("Databricks")]
    [TestCase("Snowflake")]
    [TestCase(null)]
    public void Mask_BareNameCollidingWithAColumn_IsAWholeRowOutsidePostgreSql(string? dialect)
    {
        const string sql = "WITH tag AS (SELECT name AS tag, email FROM users) SELECT tag FROM tag";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["tag"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["tag"].Should().Be("A***e");
    }

    [Test]
    public void Mask_BareNameCollidingWithAColumn_IsTheColumnOnDuckDb()
    {
        const string sql = "WITH tag AS (SELECT name AS tag, email FROM users) SELECT tag FROM tag";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["tag"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "DuckDB");

        masked[0]["tag"].Should().Be("Alice");
    }

    // A generator over a column renames table values: its column list fails closed like any other.
    [TestCase("SELECT u.x FROM customers c, UNNEST(c.contacts) AS u(x)", "PostgreSQL")]
    [TestCase("SELECT x FROM customers c CROSS JOIN LATERAL unnest(contacts) AS u(x)", "PostgreSQL")]
    [TestCase("SELECT n FROM customers c CROSS JOIN LATERAL generate_series(1, c.visits) AS g(n)", "PostgreSQL")]
    [TestCase("SELECT n FROM generate_series(1, (SELECT max(visits) FROM customers)) AS g(n)", "PostgreSQL")]
    [TestCase("SELECT s.n FROM customers c CROSS APPLY STRING_SPLIT(c.tags, ',') AS s(n)", "MSSQL")]
    public void Mask_ColumnListOnAGeneratorReadingAColumn_MasksEveryColumn(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["x"] = "Alice", ["n"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0].Values.Should().OnlyContain(x => (string)x! == "A***e");
    }

    // Literals, parameters and calls of them read no table: the generator's column list stays exempt.
    [TestCase("SELECT n FROM generate_series(1, $1) AS g(n)", "PostgreSQL")]
    [TestCase("SELECT n FROM unnest(ARRAY[lower('A'), 'b']) AS u(n)", "PostgreSQL")]
    [TestCase("SELECT n FROM generate_series(1, abs(-3)) AS g(n)", "PostgreSQL")]
    public void Mask_ColumnListOnAGeneratorOfLiterals_MasksNothing(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["n"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["n"].Should().Be("Alice");
    }

    // A base table that happens to carry a generator's name, read without arguments, is a base table.
    [TestCase("SELECT contact FROM string_split AS s(contact, n)", "MSSQL")]
    [TestCase("SELECT contact FROM generate_series AS g(contact, n)", "PostgreSQL")]
    public void Mask_ColumnListOnABaseTableNamedLikeAGenerator_MasksEveryColumn(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["contact"].Should().Be("A***e");
    }

    // Key forms through arrays, casts and parentheses, and the per-engine JSON and map readers.
    [TestCase("SELECT profile #> ARRAY['email'] AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT profile #>> ARRAY['contact', 'email'] AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT profile #>> '{email}'::text[] AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT profile ->> 'email'::text AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT profile ->> ('email') AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT jsonb_path_query_array(profile, '$.email') AS contact FROM users", "PostgreSQL")]
    [TestCase("SELECT JSON_EXTRACT_ARRAY(profile, '$.email') AS contact FROM users", "BigQuery")]
    [TestCase("SELECT JSON_VALUE_ARRAY(profile, '$.email') AS contact FROM users", "BigQuery")]
    [TestCase("SELECT JSON_QUERY_ARRAY(profile, '$.email') AS contact FROM users", "BigQuery")]
    [TestCase("SELECT JSON_EXTRACT_STRING_ARRAY(profile, '$.email') AS contact FROM users", "BigQuery")]
    [TestCase("SELECT profile['email'] AS contact FROM users", "BigQuery")]
    [TestCase("SELECT json_extract_string(profile, '$.email') AS contact FROM users", "DuckDB")]
    [TestCase("SELECT GET(profile, 'email') AS contact FROM users", "Snowflake")]
    [TestCase("SELECT GET_IGNORE_CASE(profile, 'email') AS contact FROM users", "Snowflake")]
    [TestCase("SELECT profile:email AS contact FROM users", "Snowflake")]
    [TestCase("SELECT profile:contact.email AS contact FROM users", "Snowflake")]
    [TestCase("SELECT profile['email'] AS contact FROM users", "Snowflake")]
    [TestCase("SELECT profile['email'] AS contact FROM users", "Databricks")]
    [TestCase("SELECT t.contact FROM users LATERAL VIEW json_tuple(profile, 'name', 'email') t AS nick, contact", "Databricks")]
    public void Mask_JsonOrMapReadByALiteralPiiKeyInAnyForm_IsMasked(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["contact"].Should().Be("a***m");
    }

    [TestCase("SELECT profile #>> ARRAY['nickname'] AS label FROM users", "PostgreSQL")]
    [TestCase("SELECT profile ->> 'nickname'::text AS label FROM users", "PostgreSQL")]
    [TestCase("SELECT GET(profile, 'nickname') AS label FROM users", "Snowflake")]
    [TestCase("SELECT profile:nickname AS label FROM users", "Snowflake")]
    [TestCase("SELECT profile['nickname'] AS label FROM users", "Databricks")]
    public void Mask_JsonOrMapReadByANonPiiKeyInAnyForm_StaysUnmasked(string sql, string dialect)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["label"] = "Ally" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, dialect);

        masked[0]["label"].Should().Be("Ally");
    }

    // The Databricks colon path does not parse: the statement masks every result column.
    [Test]
    public void Mask_DatabricksColonPath_MasksEveryResultColumn()
    {
        const string sql = "SELECT profile:nickname AS label FROM users";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["label"] = "Ally" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], true, null, sql, "Databricks");

        masked[0]["label"].Should().Be("A***y");
    }

    // FOR JSON / FOR XML folds every column of every row into one column the engine names.
    [TestCase("SELECT * FROM users FOR JSON AUTO", "JSON_F52E2B61-18A1-11d1-B105-00805F49916B")]
    [TestCase("SELECT id, name FROM users FOR JSON PATH", "JSON_F52E2B61-18A1-11d1-B105-00805F49916B")]
    [TestCase("SELECT name FROM users FOR XML RAW", "XML_F52E2B61-18A1-11d1-B105-00805F49916B")]
    [TestCase("SELECT d.x FROM (SELECT name AS n, email AS e FROM users FOR XML PATH('r')) AS d(x)", "x")]
    public void Mask_ForJsonOrForXml_MasksTheFoldedColumn(string sql, string key)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { [key] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "MSSQL");

        masked[0][key].Should().Be("a***m");
    }

    // FOR XML PATH('') string aggregation over a column that is not PII stays as it is.
    [Test]
    public void Mask_ForXmlInASubqueryOverNonPiiColumns_StaysUnmasked()
    {
        const string sql = "SELECT STUFF((SELECT ',' + name FROM tags FOR XML PATH('')), 1, 1, '') AS label";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["label"] = "a,b" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, SqlTextMatches(sql), true, null, sql, "MSSQL");

        masked[0]["label"].Should().Be("a,b");
    }

    [Test]
    public void ResolvePiiColumns_ReturnsThePassedColumnsPlusThePiiResultColumns()
    {
        const string sql = "SELECT address_id, email AS contact, city FROM customers";

        var columns = PiiRowMasker.ResolvePiiColumns(_guardrail, ["address_id", "contact", "city"], ["email"], true, null, sql, "PostgreSQL");

        columns.Should().BeEquivalentTo(new[] { "email", "contact" });
    }

    // "reach" is a step's PII column whose name no pattern matches: only the lineage through the select list masks it.
    [TestCase("SELECT a.reach, b.city FROM [result1] a JOIN [result2] b ON a.id = b.id", "reach")]
    [TestCase("SELECT a.reach AS label, b.city FROM [result1] a JOIN [result2] b ON a.id = b.id", "label")]
    [TestCase("SELECT UPPER(a.reach) AS label, b.city FROM [result1] a JOIN [result2] b ON a.id = b.id", "label")]
    [TestCase("SELECT d.label, d.city FROM (SELECT reach AS label, city FROM [result1]) d", "label")]
    [TestCase("WITH x AS (SELECT reach AS label, city FROM [result1]) SELECT label, city FROM x", "label")]
    [TestCase("SELECT * FROM [result1]", "reach")]
    public void ResolveLineage_AResultColumnFilledFromASourcePiiColumn_IsInTheLineage(string sql, string key)
    {
        var lineage = PiiRowMasker.ResolveLineage([key, "city"], ["reach"], sql, "SQLite");

        lineage.Should().BeEquivalentTo(new[] { key });
    }

    // Lineage only: a result column is not PII here by its own name, so with detection off it is delivered as read.
    [Test]
    public void ResolveLineage_DoesNotDetectByColumnName()
    {
        const string sql = "SELECT a.email, a.kt AS label FROM [result1] a";

        var lineage = PiiRowMasker.ResolveLineage(["email", "label"], ["kt"], sql, "SQLite");

        lineage.Should().BeEquivalentTo(new[] { "label" });
    }

    // Where the select list cannot be traced, every result column is in the lineage once a source carries PII.
    [TestCase("SELECT UPPER(a.reach), b.city FROM [result1] a JOIN [result2] b ON a.id = b.id")]
    [TestCase("SELECT a.reach AS label, b.city FROM [result1] a JOIN [result2] b ON")]
    public void ResolveLineage_AnUntraceableSelectList_PutsEveryColumnInTheLineage(string sql)
    {
        var lineage = PiiRowMasker.ResolveLineage(["label", "city"], ["reach"], sql, "SQLite");

        lineage.Should().BeEquivalentTo(new[] { "label", "city" });
    }

    [Test]
    public void ResolveLineage_NoSourcePiiColumns_IsEmpty_EvenForSqlThatCannotBeParsed()
    {
        var lineage = PiiRowMasker.ResolveLineage(["label", "city"], [], "SELECT FROM WHERE", "SQLite");

        lineage.Should().BeEmpty();
    }

    [Test]
    public void Mask_DetectionOff_MasksOnlyThePassedHostColumns()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["customer_email"] = RawEmail, ["ref_code"] = "123456789", ["name"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, ["ref_code"], false, null);

        masked[0]["customer_email"].Should().Be(RawEmail);
        masked[0]["ref_code"].Should().Be("1***9");
        masked[0]["name"].Should().Be("Alice");
    }

    // A host-masked column is masked in full, never partially, whatever the detection setting; a value the provider
    // already masked stays exactly as it was.
    [TestCase(true)]
    [TestCase(false)]
    public void Mask_HostMaskedColumn_IsMaskedInFullOnce(bool detectByColumnName)
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["ref_code"] = "123456789", ["customer_email"] = "***", ["note"] = null, ["name"] = "Alice" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, ["ref_code", "customer_email", "note"], detectByColumnName, null, hostMaskedColumns: ["ref_code", "customer_email", "note"]);

        masked[0]["ref_code"].Should().Be("***");
        masked[0]["customer_email"].Should().Be("***");
        masked[0]["note"].Should().BeNull();
        masked[0]["name"].Should().Be("Alice");
        rows[0]["ref_code"].Should().Be("123456789", "the input rows are not changed");
    }

    // Detector-found PII beside a host-masked column keeps its partial mask.
    [Test]
    public void Mask_DetectorFoundPiiBesideAHostMaskedColumn_KeepsThePartialMask()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["ref_code"] = "123456789", ["customer_email"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, ["ref_code"], true, null, hostMaskedColumns: ["ref_code"]);

        masked[0]["ref_code"].Should().Be("***");
        masked[0]["customer_email"].Should().Be("a***m");
    }

    [Test]
    public void Mask_RowsWithoutPiiKeys_AreReturnedUntouched()
    {
        var row = new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" };

        var masked = PiiRowMasker.Mask(_guardrail, [row], [], true, null);

        masked.Should().ContainSingle();
        masked[0].Should().BeSameAs(row);
    }

    [Test]
    public void Mask_KeysAreTheUnionOfAllRows()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Alice" },
            new() { ["name"] = "Bob", ["email"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], true, null);

        masked[0]["name"].Should().Be("Alice");
        masked[1]["email"].Should().Be("a***m");
        masked[1]["name"].Should().Be("Bob");
    }

    [Test]
    public void Mask_BrokenCustomPattern_FailsClosedAndMasksEveryResultColumn()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["name"] = "Alice", ["city"] = "Zagreb" }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], true, ["("]);

        masked[0]["name"].Should().Be("A***e");
        masked[0]["city"].Should().Be("Z***b");
    }

    [Test]
    public void Mask_DoesNotMutateTheInputRows()
    {
        var row = new Dictionary<string, object?> { ["email"] = RawEmail };

        PiiRowMasker.Mask(_guardrail, [row], [], true, null);

        row["email"].Should().Be(RawEmail);
    }

    private List<string> SqlTextMatches(string sql)
    {
        return _guardrail.ValidateQuery(sql, new QueryGuardrailOptions { ReadOnly = false, DetectPii = true }).PiiColumns ?? [];
    }
}
