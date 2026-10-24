using System.Reflection;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Validation;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Pins the output of every identifier-quoting call site against the one shared helper
/// (<see cref="SqlIdentifierGuard.Quote"/>), including where a call site deliberately differs.
/// </summary>
[TestFixture]
public class SqlQuotingConsolidationTests
{
    private static readonly string LongName = new('x', 130);

    [TestCase("MSSQL", "orders", "[orders]")]
    [TestCase("AzureSynapse", "orders", "[orders]")]
    [TestCase("MSSQL", "a]b", "[a]]b]")]
    [TestCase("MSSQL", "a\"b", "[a\"b]")]
    [TestCase("MSSQL", "a`b", "[a`b]")]
    [TestCase("MSSQL", "a[b", "[a[b]")]
    [TestCase("MSSQL", "", "[]")]
    [TestCase("MSSQL", "ünï col", "[ünï col]")]
    [TestCase("MySQL", "a`b", "`a``b`")]
    [TestCase("MySQL", "a\"b", "`a\"b`")]
    [TestCase("MariaDB", "orders", "`orders`")]
    [TestCase("Databricks", "a`b", "`a``b`")]
    [TestCase("PostgreSQL", "Orders", "\"Orders\"")]
    [TestCase("PostgreSQL", "a\"b", "\"a\"\"b\"")]
    [TestCase("PostgreSQL", "a]b", "\"a]b\"")]
    [TestCase("PostgreSQL", "a;b", "\"a;b\"")]
    [TestCase("PostgreSQL", "", "\"\"")]
    [TestCase("PostgreSQL", "ünï", "\"ünï\"")]
    [TestCase("Snowflake", "a\"b", "\"a\"\"b\"")]
    [TestCase("SQLite", "a\"b", "\"a\"\"b\"")]
    [TestCase(null, "a\"b", "\"a\"\"b\"")]
    public void Quote_PerDialect(string? dialect, string name, string expected)
    {
        SqlIdentifierGuard.Quote(name, dialect).Should().Be(expected);
    }

    [Test]
    public void Quote_LongName_IsWrappedWhole()
    {
        SqlIdentifierGuard.Quote(LongName, "PostgreSQL").Should().Be("\"" + LongName + "\"");
        SqlIdentifierGuard.Quote(LongName, "MSSQL").Should().Be("[" + LongName + "]");
    }

    // The sort-column quoting of the page rewriter.
    [TestCase("MSSQL", "a]b", "[a]]b]")]
    [TestCase("AzureSynapse", "orders", "[orders]")]
    [TestCase("MySQL", "a`b", "`a``b`")]
    [TestCase("MariaDB", "orders", "`orders`")]
    [TestCase("PostgreSQL", "a\"b", "\"a\"\"b\"")]
    [TestCase("Snowflake", "a\"b", "\"a\"\"b\"")]
    [TestCase("SQLite", "ünï", "\"ünï\"")]
    [TestCase(null, "a\"b", "\"a\"\"b\"")]
    // Databricks was the one dialect this call site quoted differently from the shared helper (double quotes, which
    // Databricks reads as a string literal); it now takes backticks like every other Databricks SQL Beacon builds.
    [TestCase("Databricks", "orders", "`orders`")]
    public void PageRewriterSortColumn_QuotingPerDialect(string? dialect, string name, string expected)
    {
        SqlIdentifierGuard.Quote(name, dialect).Should().Be(expected);
    }

    // The suggestion text of the host query policy and the project brief: a name is quoted only when PostgreSQL
    // would fold it, and a quote inside it is doubled.
    [TestCase("orders", "orders")]
    [TestCase("order_items_2", "order_items_2")]
    [TestCase("Orders", "\"Orders\"")]
    [TestCase("a b", "\"a b\"")]
    [TestCase("", "")]
    [TestCase("ünï", "\"ünï\"")]
    [TestCase("a\"b", "\"a\"\"b\"")]
    public void QuotePostgresIfNeeded_QuotesOnlyWhatFolds(string name, string expected)
    {
        SqlIdentifierGuard.QuotePostgresIfNeeded(name).Should().Be(expected);
    }

    [TestCase(DatabaseEngineType.PostgreSQL, "col", "\"col\"")]
    [TestCase(DatabaseEngineType.MSSQL, "col", "[col]")]
    [TestCase(DatabaseEngineType.MySQL, "col", "`col`")]
    [TestCase(DatabaseEngineType.SQLite, "col", "col")]
    [TestCase(DatabaseEngineType.AzureSynapse, "col", "col")]
    [TestCase(DatabaseEngineType.Snowflake, "col", "col")]
    public void DataQualityColumn_QuotingPerEngine(DatabaseEngineType engine, string column, string expected)
    {
        Invoke<string>(typeof(DataQualitySqlGenerator), "QuoteColumn", column, engine).Should().Be(expected);
    }

    [TestCase("a\"b")]
    [TestCase("a]b")]
    [TestCase("a;b")]
    [TestCase("")]
    [TestCase("a b")]
    public void DataQualityColumn_HostileName_IsRefused(string column)
    {
        var act = () => Invoke<string>(typeof(DataQualitySqlGenerator), "QuoteColumn", column, DatabaseEngineType.PostgreSQL);

        act.Should().Throw<InvalidOperationException>();
    }

    [TestCase("t", "\"s\".\"t\"", DatabaseEngineType.PostgreSQL)]
    [TestCase("t", "[s].[t]", DatabaseEngineType.MSSQL)]
    [TestCase("t", "`s`.`t`", DatabaseEngineType.MySQL)]
    [TestCase("t", "s.t", DatabaseEngineType.AzureSynapse)]
    public void DataQualityTable_QuotingPerEngine(string table, string expected, DatabaseEngineType engine)
    {
        Invoke<string>(typeof(DataQualitySqlGenerator), "QualifyTable", "s", table, engine).Should().Be(expected);
    }

    [TestCase("orders", "\"orders\"")]
    [TestCase("a\"b", "\"a\"\"b\"")]
    [TestCase("a]b", "\"a]b\"")]
    [TestCase("a`b", "\"a`b\"")]
    [TestCase("ünï", "\"ünï\"")]
    [TestCase("", "\"\"")]
    public void InMemoryIdentifier_IsDoubleQuotedWithQuotesDoubled(string name, string expected)
    {
        Invoke<string>(typeof(InMemoryDatabaseManager), "QuoteIdentifier", name).Should().Be(expected);
    }

    [Test]
    public void InMemoryIdentifier_WithNul_IsRefused()
    {
        var act = () => Invoke<string>(typeof(InMemoryDatabaseManager), "QuoteIdentifier", "a\0b");

        act.Should().Throw<InvalidOperationException>();
    }

    private static T Invoke<T>(Type type, string method, params object[] args)
    {
        var info = type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        info.Should().NotBeNull();
        try
        {
            return (T)info!.Invoke(null, args)!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }
    }
}
