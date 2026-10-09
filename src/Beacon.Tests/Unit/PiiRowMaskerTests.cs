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

    [Test]
    public void Mask_UnparseableSql_AddsNoAliases_ButStillMasksPiiResultColumns()
    {
        const string sql = "SELECT email AS contact,, FROM";
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["contact"] = "Alice", ["customer_email"] = RawEmail }
        };

        var masked = PiiRowMasker.Mask(_guardrail, rows, [], true, null, sql, "PostgreSQL");

        masked[0]["contact"].Should().Be("Alice");
        masked[0]["customer_email"].Should().Be("a***m");
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

    [Test]
    public void ResolvePiiColumns_ReturnsThePassedColumnsPlusThePiiResultColumns()
    {
        const string sql = "SELECT address_id, email AS contact, city FROM customers";

        var columns = PiiRowMasker.ResolvePiiColumns(_guardrail, ["address_id", "contact", "city"], ["email"], true, null, sql, "PostgreSQL");

        columns.Should().BeEquivalentTo(new[] { "email", "address_id", "contact" });
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
