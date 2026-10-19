using System.Data.Common;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// An error the statement's own code raised — PostgreSQL's RAISE (SQLSTATE P0001), SQL Server's THROW and RAISERROR (error
/// 50000 and above), MySQL's SIGNAL (SQLSTATE 45000) — says whatever its author wrote, values unquoted. The database
/// provider passes it on led by its code, and every screen withholds it whatever it says, also when its wording reads like
/// a value-free class: the caller's error and the stored one (signals, ad-hoc history), and a repair prompt, get its class
/// and code, and under the row-data lock a prompt gets the class and code only. An error the database wrote about a name
/// keeps the names it knows.
/// </summary>
[TestFixture]
public class RaisedDatabaseErrorTests
{
    private const string Sql = "SELECT id, name FROM customers";

    private const string Withheld = "The database's message is withheld because it can quote row values.";

    private static readonly Dictionary<string, HashSet<string>> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["customers"] = ["id", "name", "email"]
    };

    // A value of each kind the author may write into the error, unquoted, in a wording no rule knows and in one that reads
    // like a permission error (a value-free class, were it the database's own).
    private static IEnumerable<TestCaseData> RaisedErrors()
    {
        (string Kind, string Value)[] values =
        [
            ("Email", "alice.horvat@bank.example"),
            ("Iban", "HR1210010051863000160"),
            ("Credential", TestCredentials.ProviderSecretKey),
            ("PersonName", "Ivan Horvat")
        ];

        foreach (var (kind, value) in values)
        {
            foreach (var (shape, wording) in new[] { ("Plain", $"Customer {value} is blocked"), ("PermissionShaped", $"permission denied for customer {value}") })
            {
                yield return new TestCaseData(new PostgresException(wording, "ERROR", "ERROR", "P0001"), "P0001", value).SetName($"PostgreSql_{kind}_{shape}");
                yield return new TestCaseData(new FakeDbException(wording, "45000"), "45000", value).SetName($"MySql_{kind}_{shape}");
                yield return new TestCaseData(TestSqlServerExceptions.Real(50000, wording), "50000", value).SetName($"SqlServer_{kind}_{shape}");
            }
        }
    }

    [Test]
    public async Task TheProvider_LeadsARaisedErrorWithItsCode_WhereTheDriverLeavesItOut()
    {
        const string wording = "Customer blocked";

        (await ProviderText(new PostgresException(wording, "ERROR", "ERROR", "P0001"))).Should().Be($"P0001: {wording}");
        (await ProviderText(new FakeDbException(wording, "45000"))).Should().Be($"45000: {wording}");
        (await ProviderText(TestSqlServerExceptions.Real(50001, wording))).Should().Be($"Msg 50001, Level 16, State 1: {wording}");
    }

    [Test]
    public async Task TheProvider_PassesAnErrorTheDatabaseWroteOnAsItIs()
    {
        (await ProviderText(TestSqlServerExceptions.Real(207, "Invalid column name 'nme'."))).Should().Be("Invalid column name 'nme'.");
        (await ProviderText(new FakeDbException("Unknown column 'nme' in 'field list'", "42S22"))).Should().Be("Unknown column 'nme' in 'field list'");
        (await ProviderText(new PostgresException("column \"nme\" does not exist", "ERROR", "ERROR", "42703"))).Should().Be("42703: column \"nme\" does not exist");
    }

    [TestCaseSource(nameof(RaisedErrors))]
    public async Task ARaisedError_IsWithheldFromCallersPromptsSignalsAndHistory(Exception raised, string code, string value)
    {
        var text = await ProviderText(raised);
        var withheld = $"Database error {code} (unrecognised): not an error Beacon recognises. {Withheld}";

        // The caller's error while PII detection is on, the learning signal and the ad-hoc history.
        DatabaseErrorRedactor.ForCaller(text, Sql, redact: true).Should().Be(withheld);

        // A repair prompt and lesson extraction.
        DatabaseErrorRedactor.Redact(text, DatabaseErrorRedactor.KnownNames(Sql, Catalog)).Should().Be(withheld);

        // A prompt under the row-data lock.
        DatabaseErrorRedactor.ClassOnly(text).Should().Be($"Database error {code} (unrecognised).").And.NotContain(value);
    }

    // A SQL Server error Beacon cannot read may be one the statement raised: the provider passes on the generic text, never
    // the message, and every screen keeps that text as it is.
    [TestCase(50000)]
    [TestCase(207)]
    public async Task AnUnreadableSqlServerError_IsWithheldEverywhere_AsTheGenericText(int number)
    {
        const string value = "alice.horvat@bank.example";
        var unreadable = TestSqlServerExceptions.ErrorsThrow($"Customer {value} is blocked", number);

        var text = await ProviderText(unreadable);

        text.Should().Be(DatabaseErrorRedactor.UnreadableSqlServerMessage).And.NotContain(value);
        DatabaseErrorRedactor.ForCaller(text, Sql, redact: true).Should().Be(DatabaseErrorRedactor.UnreadableSqlServerMessage);
        DatabaseErrorRedactor.Redact(text, DatabaseErrorRedactor.KnownNames(Sql, Catalog)).Should().Be(DatabaseErrorRedactor.UnreadableSqlServerMessage);
        DatabaseErrorRedactor.ClassOnly(text).Should().Be("Database error (unrecognised).");
        (await ProviderText(TestSqlServerExceptions.Unbindable($"Customer {value} is blocked"))).Should().Be(DatabaseErrorRedactor.UnreadableSqlServerMessage);
    }

    // The application-defined classes are told by their code wherever the engine reports it.
    [TestCase("P0001: permission denied for customer Ivan Horvat", "P0001")]
    [TestCase("P0004: assertion failed for Ivan Horvat", "P0004")]
    [TestCase("45000: Invalid column name for Ivan Horvat", "45000")]
    [TestCase("45001: syntax error near Ivan Horvat", "45001")]
    [TestCase("[USER_RAISED_EXCEPTION] permission denied for Ivan Horvat SQLSTATE: P0001", "P0001")]
    [TestCase("Error: Uncaught exception of type 'STATEMENT_ERROR': Ivan Horvat is blocked SqlState: P0001, VendorCode: 100132", "P0001")]
    [TestCase("Msg 50000, Level 16, State 1: Incorrect syntax near Ivan Horvat", "50000")]
    [TestCase("Msg 2147483647, Level 16, State 1: Ivan Horvat", "2147483647")]
    public void AnApplicationDefinedClass_IsWithheld_WhateverItsWordingReadsLike(string error, string code)
    {
        DatabaseErrorRedactor.Redact(error, DatabaseErrorRedactor.KnownNames(Sql, Catalog))
            .Should().Be($"Database error {code} (unrecognised): not an error Beacon recognises. {Withheld}");
    }

    // Repair quality: an error the database wrote about a name keeps the names it knows, on every engine.
    [TestCase("42703: column \"name\" does not exist")]
    [TestCase("42P01: relation \"customers\" does not exist")]
    [TestCase("42601: syntax error at end of input")]
    [TestCase("Invalid column name 'name'.")]
    [TestCase("Table 'customers' doesn't exist")]
    public void ANameOrSyntaxError_KeepsTheNamesItKnows(string error)
    {
        DatabaseErrorRedactor.Redact(error, DatabaseErrorRedactor.KnownNames(Sql, Catalog)).Should().Be(error);
        DatabaseErrorRedactor.ForCaller(error, Sql, redact: true).Should().Be(error);
    }

    // The text the database provider hands on for a statement that failed with failure.
    private static async Task<string> ProviderText(Exception failure)
    {
        var resolver = new Mock<IDataSourceConnectionResolver>();
        resolver
            .Setup(x => x.GetConnectionString(It.IsAny<DataSource>()))
            .Throws(failure);
        var provider = new DatabaseProvider(
            resolver.Object,
            new SqlReadOnlyAstValidator(NullLogger<SqlReadOnlyAstValidator>.Instance),
            new HostDataSourceGuard(new TestHostDataSources()),
            new ConfiguredDatabaseResolver(resolver.Object, NullLogger<ConfiguredDatabaseResolver>.Instance),
            NullLogger<DatabaseProvider>.Instance,
            TestQueryServices.QueryOptions);
        var source = new DataSource
        {
            Id = 7,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "unused",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };

        var result = await provider.ExecuteQueryAsync(source, Sql, [], CancellationToken.None);

        result.Success.Should().BeFalse();
        return result.ErrorMessage!;
    }

    // A driver exception carrying a SQLSTATE and only the server's message, as MySQL's driver reports one.
    private sealed class FakeDbException(string message, string sqlState) : DbException(message)
    {
        public override string SqlState => sqlState;
    }
}
