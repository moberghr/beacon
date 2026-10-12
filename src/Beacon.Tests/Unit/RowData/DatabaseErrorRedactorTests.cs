using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Models.Ai;
using Beacon.Core.Services.Security;
using Beacon.Core.Telemetry;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// A database error as it leaves Beacon, judged by its class: an error that carries values keeps only its class, its code
/// and a generic description; an error about a name keeps the names it knows; any other error keeps its text with every
/// quoted span, key and row detail, record, unquoted value and JSON context redacted. Per engine, and for every shape a
/// value can hide in. And the egress log of row data sent to the LLM provider: ids and categories, never a value.
/// </summary>
[TestFixture]
public class DatabaseErrorRedactorTests
{
    private const string Sql = "SELECT o.totl, c.name, c.city FROM public.orders o JOIN customers c ON c.id = o.customer_id WHERE c.name = 'Ivan Horvat' AND o.placed > '2024-13-01'";

    private const string Withheld = "The database's message is withheld because it can quote row values.";

    private static readonly Dictionary<string, HashSet<string>> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orders"] = ["id", "total", "customer_id", "placed"],
        ["public.orders"] = ["id", "total", "customer_id", "placed"],
        ["customers"] = ["id", "name", "city"]
    };

    private static IReadOnlySet<string> Known => DatabaseErrorRedactor.KnownNames(Sql, Catalog);

    [TestCase("42703: column \"o.totl\" does not exist\n\nPOSITION: 8")]
    [TestCase("42P01: relation \"public.orders\" does not exist")]
    [TestCase("42803: column \"c.name\" must appear in the GROUP BY clause")]
    [TestCase("operator does not exist: integer = text")]
    [TestCase("22012: division by zero")]
    [TestCase("Invalid column name 'totl'.")]
    [TestCase("Ambiguous column name 'name'.")]
    [TestCase("Table 'public.orders' doesn't exist")]
    [TestCase("no such column: city")]
    [TestCase("Unrecognized name: totl at [1:8]")]
    public void AnErrorAboutNamesItKnows_IsKeptAsItIs(string error)
    {
        DatabaseErrorRedactor.Redact(error, Known).Should().Be(error);
    }

    [Test]
    public void AnErrorAboutANameItDoesNotKnow_LosesThatQuotedText()
    {
        DatabaseErrorRedactor.Redact("Unknown column 'c.name' in 'field list'", Known)
            .Should().Be("Unknown column 'c.name' in '<value>'");
    }

    // A quoted catalog name survives only in an error about a name; anywhere else it is redacted like any other quote.
    [TestCase("42703: column \"city\" does not exist", "42703: column \"city\" does not exist")]
    [TestCase("Invalid column name 'city'.", "Invalid column name 'city'.")]
    [TestCase("42601: syntax error at or near \"city\"", "42601: syntax error at or near \"<value>\"")]
    [TestCase("Incorrect syntax near 'city'.", "Incorrect syntax near '<value>'.")]
    [TestCase("42501: permission denied for table \"orders\"", "42501: permission denied for table \"<value>\"")]
    public void AKnownName_SurvivesOnlyInAnErrorAboutAName(string error, string redacted)
    {
        DatabaseErrorRedactor.Redact(error, Known).Should().Be(redacted);
    }

    [TestCase("22P02: invalid input syntax for type integer: \"city\"")]
    [TestCase("Conversion failed when converting the varchar value 'city' to data type int.")]
    public void AKnownNameQuotedAsAValue_IsNotKept(string error)
    {
        DatabaseErrorRedactor.Redact(error, Known).Should().NotContain("city");
    }

    // PostgreSQL / any engine reporting a SQLSTATE.
    [TestCase("22P02: invalid input syntax for type integer: \"Ivan Horvat\"", "22P02 (data exception): a value could not be converted to integer")]
    [TestCase("22P02: invalid input syntax for type integer: \"Ivan\nHorvat\"\n\nPOSITION: 40", "22P02 (data exception): a value could not be converted to integer")]
    [TestCase("22P02: invalid input syntax for type timestamp with time zone: \"Ivan Horvat\"", "22P02 (data exception): a value could not be converted to timestamp with time zone")]
    [TestCase("22008: date/time field value out of range: \"Ivan Horvat\"", "22008 (data exception): a date or time value is invalid or out of range")]
    [TestCase("22003: value \"Horvat\" is out of range for type integer", "22003 (data exception): a numeric value is out of range for its type")]
    [TestCase("22P02: invalid input syntax for type json\n\nDETAIL: Token \"Ivan\" is invalid.\n\nCONTEXT:  JSON data, line 1: {\"name\": \"Ivan Horvat\"...", "22P02 (data exception): a value could not be converted to json")]
    [TestCase("23505: duplicate key value violates unique constraint \"ux_email\"\n\nDETAIL: Key (email)=(Ivan.Horvat@bank.example) already exists.", "23505 (integrity constraint violation): a key value is duplicated")]
    [TestCase("23502: null value in column \"total\" of relation \"orders\" violates not-null constraint\n\nDETAIL: Failing row contains (5, Ivan Horvat, null).", "23502 (integrity constraint violation): a NULL value is not allowed in a column")]
    // SQL Server
    [TestCase("Conversion failed when converting the nvarchar value 'Ivan Horvat' to data type int.", "245 (conversion error): a value could not be converted to int")]
    [TestCase("Conversion failed when converting the varchar value 'Ivan O'Horvat' to data type int.", "245 (conversion error): a value could not be converted to int")]
    [TestCase("Error converting data type nvarchar to bigint.", "8114 (conversion error): a value could not be converted to bigint")]
    [TestCase("Arithmetic overflow error converting expression to data type int.", "8115 (arithmetic overflow): a numeric value is out of range for its type")]
    [TestCase("Violation of PRIMARY KEY constraint 'PK_customers'. Cannot insert duplicate key in object 'dbo.customers'. The duplicate key value is (Ivan Horvat, 5).", "2627 (duplicate key): a key value is duplicated")]
    [TestCase("Cannot insert duplicate key row in object 'dbo.customers' with unique index 'ix_email'. The duplicate key value is (Ivan.Horvat@bank.example).", "2601 (duplicate key): a key value is duplicated")]
    [TestCase("String or binary data would be truncated in table 'shop.dbo.customers', column 'name'. Truncated value: 'Ivan Horv'.", "2628 (string truncation): a value is too long for its column or type")]
    [TestCase("Cannot insert the value NULL into column 'email', table 'shop.dbo.customers'; column does not allow nulls. INSERT fails.", "515 (null not allowed): a NULL value is not allowed in a column")]
    // MySQL
    [TestCase("Incorrect integer value: 'Ivan Horvat' for column 'total' at row 1", "1366 (incorrect value): a value could not be converted to integer")]
    [TestCase("Incorrect datetime value: 'Ivan Horvat' for column 'placed' at row 1", "1292 (incorrect value): a date or time value is invalid or out of range")]
    [TestCase("Truncated incorrect DOUBLE value: 'Ivan Horvat'", "1292 (incorrect value): a value could not be converted to DOUBLE")]
    [TestCase("Duplicate entry 'Ivan.Horvat@bank.example' for key 'customers.ux_email'", "1062 (duplicate entry): a key value is duplicated")]
    [TestCase("Out of range value for column 'total' at row 1", "1264 (out-of-range value): a numeric value is out of range for its type")]
    [TestCase("Data too long for column 'name' at row 1", "1406 (data too long): a value is too long for its column or type")]
    // BigQuery
    [TestCase("Bad int64 value: Ivan Horvat", "(invalid value): a value could not be converted to int64")]
    [TestCase("Bad double value: Ivan Horvat; error in parsing", "(invalid value): a value could not be converted to double")]
    [TestCase("Could not cast literal \"Ivan Horvat\" to type INT64 at [1:8]", "(invalid cast): a value could not be converted to INT64")]
    [TestCase("Invalid cast of \"Ivan Horvat\" to INT64", "(invalid cast): a value could not be converted to INT64")]
    [TestCase("Invalid timestamp: 'Ivan Horvat'", "(invalid value): a date or time value is invalid or out of range")]
    // Databricks
    [TestCase("[CAST_INVALID_INPUT] The value 'Ivan Horvat' of the type \"STRING\" cannot be cast to \"INT\" because it is malformed. Correct the value as per the syntax, or change its target type. SQLSTATE: 22018", "22018 (CAST_INVALID_INPUT): a value could not be converted to INT")]
    [TestCase("[CAST_INVALID_INPUT] The value 'Ivan Horvat' of the type \"STRING\" cannot be cast to \"DATE\" because it is malformed.", "(CAST_INVALID_INPUT): a value could not be converted to DATE")]
    // Snowflake
    [TestCase("Numeric value 'Ivan Horvat' is not recognized", "100038 (invalid value): a value could not be converted to Numeric")]
    [TestCase("Error: Timestamp 'Ivan Horvat' is not recognized SqlState: 22007, VendorCode: 100035, QueryId: 01b2", "22007 (data exception): a date or time value is invalid or out of range")]
    // Any engine
    [TestCase("Cannot parse Ivan Horvat as integer", "(conversion error): a value could not be converted to integer")]
    public void AnErrorThatCarriesValues_KeepsOnlyItsClassCodeAndDescription(string error, string described)
    {
        var redacted = DatabaseErrorRedactor.Redact(error, Known);

        redacted.Should().Be($"Database error {described}. {Withheld}");
        redacted.Should().NotContain("Ivan").And.NotContain("Horvat").And.NotContain("Horv");
    }

    // An error redacted where it was read and screened again later (a repair prompt, a stored signal) keeps what the first
    // screen left: Beacon's own class, code and description pass unchanged, and nothing else can pass as them.
    [TestCase("22P02: invalid input syntax for type integer: \"Ivan Horvat\"")]
    [TestCase("[CAST_INVALID_INPUT] The value 'Ivan Horvat' of the type \"STRING\" cannot be cast to \"INT\" because it is malformed. SQLSTATE: 22018")]
    [TestCase("Something odd happened with 'Ivan Horvat'")]
    [TestCase("42703: column \"o.totl\" does not exist")]
    [TestCase("Incorrect syntax near 'Ivan Horvat'.")]
    public void RedactingTwice_KeepsTheFirstResult(string error)
    {
        var once = DatabaseErrorRedactor.Redact(error, Known);

        DatabaseErrorRedactor.Redact(once, Known).Should().Be(once);
        DatabaseErrorRedactor.ClassOnly(once).Should().Be(DatabaseErrorRedactor.ClassOnly(error));
    }

    [TestCase("Database error 5 (data exception): 'Ivan Horvat'.")]
    [TestCase("Database error 22P02 (data exception): Ivan Horvat. The database's message is withheld because it can quote row values.")]
    public void TextShapedLikeBeaconsOwn_IsStillScreened(string error)
    {
        DatabaseErrorRedactor.Redact(error, Known).Should().NotContain("Horvat");
    }

    // A bracketed word is named as the error's class only when Beacon recognises it as one.
    [Test]
    public void ABracketedValue_IsNotNamedAsTheClass()
    {
        DatabaseErrorRedactor.Redact("22P02: [IVAN_HORVAT] is not a number", Known)
            .Should().Be($"Database error 22P02 (data exception): a value could not be converted to the target type. {Withheld}");
    }

    // A conversion's description repeats only a word Beacon knows as a type name.
    [Test]
    public void AConversionDescription_NamesOnlyAKnownTypeName()
    {
        DatabaseErrorRedactor.Redact("Cannot convert Ivan to Horvat", Known)
            .Should().Be($"Database error (conversion error): a value could not be converted to the target type. {Withheld}");
    }

    [TestCase("Something odd happened with 'Ivan Horvat'")]
    [TestCase("Something odd happened with \"Ivan Horvat\"")]
    [TestCase("Value: Ivan Horvat; row 1")]
    [TestCase("the value Ivan Horvat was refused")]
    public void AnUnrecognisedErrorWithAQuoteOrAValue_IsWithheld(string error)
    {
        DatabaseErrorRedactor.Redact(error, Known)
            .Should().Be($"Database error (unrecognised): not an error Beacon recognises. {Withheld}");
    }

    [TestCase("The query timed out.")]
    [TestCase("Step execution timed out")]
    [TestCase(Beacon.Core.Services.Providers.DatabaseProvider.HostResultMismatchMessage)]
    [TestCase("Query failed on the host database.")]
    public void AnErrorWithoutQuotesOrValues_IsKeptAsItIs(string error)
    {
        DatabaseErrorRedactor.Redact(error, Known).Should().Be(error);
    }

    // The shapes a value takes inside an error that is kept.
    [TestCase("syntax error at or near \"Ivan\nHorvat\" here", "syntax error at or near \"<value>\" here")]
    [TestCase("syntax error near 'Ivan O'Horvat' here", "syntax error near '<value>' here")]
    [TestCase("syntax error near 'it''s Horvat' here", "syntax error near '<value>' here")]
    [TestCase("syntax error near 'Ivan Horvat", "syntax error near '<value>")]
    [TestCase("syntax error near \"Ivan Horvat", "syntax error near \"<value>")]
    [TestCase("Key (name)=(Ivan (x) Horvat) already exists.", "Key (name)=(<value>) already exists.")]
    [TestCase("Failing row contains (5, Ivan (Horvat), null).", "Failing row contains (<value>).")]
    [TestCase("Failing row contains (5, Ivan Horvat", "Failing row contains (<value>")]
    [TestCase("The duplicate key value is (Ivan Horvat, 5).", "The duplicate key value is (<value>).")]
    [TestCase("rejected because value = Ivan Horvat", "rejected because value = <value>")]
    [TestCase("rejected because value = 256", "rejected because value = <value>")]
    [TestCase("Value: Ivan Horvat; row 1", "Value: <value>; row 1")]
    [TestCase("Cannot parse Ivan Horvat as integer", "Cannot parse <value> as integer")]
    [TestCase("invalid input\nCONTEXT:  JSON data, line 1: {\"name\": \"Ivan Horvat\"\nmore", "invalid input\nCONTEXT:  JSON data, line 1: <value>\nmore")]
    [TestCase("Malformed records are detected in record parsing: {name: Ivan Horvat, age: 5}.", "Malformed records are detected in record parsing: {<value>}.")]
    [TestCase("Table 'orders' doesn't exist, it can't be read", "Table '<value>' doesn't exist, it can't be read")]
    public void AValueInAKeptError_IsRedactedWhateverItsShape(string text, string redacted)
    {
        DatabaseErrorRedactor.RedactText(text, null).Should().Be(redacted);
    }

    [Test]
    public void TheNamesOfTheStatement_LeaveItsLiteralsOut()
    {
        var names = DatabaseErrorRedactor.KnownNames(Sql, null);

        names.Should().Contain(["o", "totl", "public", "orders", "customers", "c", "name", "customer_id", "placed", "SELECT"]);
        names.Should().NotContain(["Ivan", "Horvat", "Ivan Horvat", "2024-13-01"]);
    }

    [Test]
    public void ALongError_IsCut()
    {
        DatabaseErrorRedactor.Redact(new string('x', 5000), []).Should().HaveLength(2000);
    }

    // The value sits across the 2,000-character cut: the error is redacted first, so no part of it survives the cut.
    [Test]
    public void ALongError_IsRedactedBeforeItIsCut()
    {
        var error = "42601: syntax error " + new string('x', 1970) + " near 'Ivan Horvat'";

        var redacted = DatabaseErrorRedactor.Redact(error, Known);

        redacted.Should().HaveLength(2000);
        redacted.Should().NotContain("Ivan").And.NotContain("Horvat");
    }

    [Test]
    public void ForCaller_RedactsOnlyWhileDetectionIsOn_KnowingTheStatementsNamesOnly()
    {
        const string error = "Invalid column name 'city'. Did you mean 'Ivan Horvat'?";

        DatabaseErrorRedactor.ForCaller(error, Sql, redact: false).Should().Be(error);
        DatabaseErrorRedactor.ForCaller(error, Sql, redact: true).Should().Be("Invalid column name 'city'. Did you mean '<value>'?");
        DatabaseErrorRedactor.ForCaller(error, "SELECT 1", redact: true).Should().Be("Invalid column name '<value>'. Did you mean '<value>'?");
    }

    [TestCase("42703: column \"o.totl\" does not exist", "Database error 42703 (undefined column).")]
    [TestCase("Conversion failed when converting the nvarchar value 'Ivan Horvat' to data type int.", "Database error 245 (conversion error).")]
    [TestCase("Value: Ivan Horvat", "Database error (unrecognised).")]
    public void ClassOnly_KeepsNoneOfTheText(string error, string classOnly)
    {
        DatabaseErrorRedactor.ClassOnly(error).Should().Be(classOnly);
    }

    [Test]
    public void AnErrorThatCannotBeScreenedInTime_IsReplacedCountedAndLoggedWithoutItsText()
    {
        var log = new LogRecorder();
        long fallbacks = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == BeaconTelemetry.MeterName && instrument.Name == BeaconTelemetry.ErrorRedactionFallbacksInstrument)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => Interlocked.Add(ref fallbacks, measurement));
        listener.Start();

        var result = DatabaseErrorRedactor.Screened(
            () => throw new RegexMatchTimeoutException("Ivan Horvat", "x", TimeSpan.FromSeconds(1)),
            log.For<DatabaseErrorRedactorTests>());

        result.Should().Be(DatabaseErrorRedactor.UnscreenedMessage);
        Interlocked.Read(ref fallbacks).Should().BeGreaterThanOrEqualTo(1);
        log.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Exception == null);
        log.Contains("Horvat").Should().BeFalse();
    }

    [Test]
    public async Task TheEgressLog_NamesIdsAndCategories_AndSendsThroughTheProviderHandedIn()
    {
        var log = new LogRecorder();
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "ok" });
        var egress = TestRowDataEgress.Logging(log.For<LlmRowDataEgress>());

        var tracked = egress.Track(inner.Object, new LlmRowDataScope(3, [5, 6], [LlmRowDataCategories.SampleValues, LlmRowDataCategories.ResultRows]));
        var untracked = egress.Track(inner.Object, new LlmRowDataScope(3, [5], []));
        await tracked.CompleteAsync(new LlmRequest { Messages = [new ChatMessage(Beacon.Core.Data.Enums.ConversationRole.User, "Examples: [Ivan Horvat]")] });
        var named = egress.Track(inner.Object, new LlmRowDataScope(3, [5], [LlmRowDataCategories.ColumnValues]) { CallerId = "9" });
        await named.CompleteAsync(new LlmRequest());

        untracked.Should().BeSameAs(inner.Object, "a prompt without row data is sent untouched");
        inner.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        log.Entries.Should().HaveCount(2);
        log.Entries.Should().OnlyContain(x => x.EventId == ProjectAccessEvents.RowDataSentToLlm && x.Level == LogLevel.Information);
        log.Entries[0].Message.Should().Be($"An LLM request carrying sample-values, result-rows read from data sources 5, 6 of project 3 was handed to the provider for caller {TestRowDataEgress.CallerId}");
        log.Entries[1].Message.Should().Be("An LLM request carrying column-values read from data sources 5 of project 3 was handed to the provider for caller 9");
        log.Contains("Horvat").Should().BeFalse();
    }
}
