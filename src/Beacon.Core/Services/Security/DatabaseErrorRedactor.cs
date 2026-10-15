using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Beacon.Core.Services.Providers;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Services.Security;

/// <summary>
/// A database error as it may leave Beacon's hands — into a prompt to the LLM provider (a repair prompt, lesson
/// extraction), into a stored learning signal, or, while PII detection is on, to the caller and the audit trail. A database
/// quotes row values back in its errors (a failed conversion names the value it could not convert, a key violation the
/// key), and those must not leave with the error. The error is judged by its CLASS, read from its SQLSTATE, its error class
/// or the engine's wording:
/// <list type="bullet">
/// <item>A class that carries values — a data exception or an integrity violation (SQLSTATE 22xxx, 23xxx), SQL Server's
/// conversion, overflow, duplicate-key, truncation and NULL errors, MySQL's incorrect-value, duplicate-entry, out-of-range
/// and too-long errors, BigQuery, Databricks and Snowflake cast and parse errors — is replaced by its class, its code and a
/// generic description (<c>a value could not be converted to integer</c>): none of the message is kept.</item>
/// <item>A class that carries no values — an unknown or ambiguous name, a grouping error, a syntax, type, permission or
/// division-by-zero error — keeps its message, with quoted text, key and row details, brace records, unquoted values and
/// JSON context replaced by <see cref="Placeholder"/>. Quoted text survives only in an error about a name (a column, table
/// or function that does not exist or is ambiguous), and only when every dotted part of it is a known name.</item>
/// <item>An error of no class Beacon recognises is replaced like a value-carrying one (<c>unrecognised</c>), whatever it
/// says. So is an error the statement's own code raised — PostgreSQL's class P0 (<c>RAISE</c>), SQL Server's errors 50000
/// and above (<c>THROW</c>, <c>RAISERROR</c>), MySQL's SQLSTATE class 45 (<c>SIGNAL</c>) — whose wording is its author's,
/// so it is never read as a class Beacon knows (<see cref="TextOf"/> puts the code in front where the driver leaves it
/// out).</item>
/// <item>Beacon's own fixed texts that reach the screens beside the database's errors (a host source's failure, a read
/// stopped at the row ceiling, a refused read of a migration write target …) hold no value and are kept as they are.</item>
/// </list>
/// The whole message is redacted before it is cut to 2,000 characters, a quoted span may run across lines, and a quote
/// that never closes takes the rest of the message with it. An error that cannot be screened in time is replaced as a
/// whole (<see cref="UnscreenedMessage"/>), counted (<see cref="BeaconTelemetry.ErrorRedactionFallbacks"/>) and logged
/// without its text.
/// </summary>
public static partial class DatabaseErrorRedactor
{
    public const string Placeholder = "<value>";

    /// <summary>What replaces an error that could not be screened in time.</summary>
    public const string UnscreenedMessage = "Database error: the message is withheld because it could not be screened.";

    private const int MaxLength = 2000;

    // Longer errors are cut before they are screened, so no screen runs over an unbounded text; a quote the cut leaves open
    // still takes the rest with it.
    private const int MaxScreenedLength = 100_000;

    private const string Withheld = "The database's message is withheld because it can quote row values.";

    // Descriptions of the value-carrying classes.
    private const string Conversion = "a value could not be converted to {type}";
    private const string DateTimeValue = "a date or time value is invalid or out of range";
    private const string OutOfRange = "a numeric value is out of range for its type";
    private const string TooLong = "a value is too long for its column or type";
    private const string Duplicate = "a key value is duplicated";
    private const string NotNull = "a NULL value is not allowed in a column";
    private const string Integrity = "a value violates a constraint";
    private const string DataValue = "a data value is invalid";
    private const string NotRecognised = "not an error Beacon recognises";
    private const string ConversionPrefix = "a value could not be converted to ";
    private const string AnyTargetType = "the target type";

    // SQL Server's first user-defined error number: THROW takes 50000 and above, RAISERROR with a message text raises 50000.
    private const int FirstSqlServerUserError = 50000;

    private enum ErrorKind
    {
        // About a name of the statement: quoted names it knows are kept.
        Name,

        // Carries no row values: kept, every quoted text redacted.
        NoValue,

        // Carries row values: replaced by its class, code and description.
        Value,

        // Not recognised, or raised by the statement's own code: replaced like Value.
        Unrecognised
    }

    private sealed record ErrorClass(ErrorKind Kind, string Name, string? Code, string? Description = null);

    private sealed record Rule(Regex Pattern, ErrorKind Kind, string Name, string? Code, string? Description = null);

    // The engines' wording, checked in order after the SQLSTATE and the error class.
    private static readonly Rule[] Rules =
    [
        // SQL Server
        new(Wording("Conversion failed when converting date and/or time"), ErrorKind.Value, "conversion error", "241", DateTimeValue),
        new(Wording("Conversion failed when converting"), ErrorKind.Value, "conversion error", "245", Conversion),
        new(Wording("Error converting data type"), ErrorKind.Value, "conversion error", "8114", Conversion),
        new(Wording("Arithmetic overflow error"), ErrorKind.Value, "arithmetic overflow", "8115", OutOfRange),
        new(Wording("resulted in an out-of-range value"), ErrorKind.Value, "out-of-range value", "242", DateTimeValue),
        new(Wording("Violation of (?:PRIMARY|UNIQUE) KEY constraint"), ErrorKind.Value, "duplicate key", "2627", Duplicate),
        new(Wording("Cannot insert duplicate key"), ErrorKind.Value, "duplicate key", "2601", Duplicate),
        new(Wording("String or binary data would be truncated in table"), ErrorKind.Value, "string truncation", "2628", TooLong),
        new(Wording("String or binary data would be truncated"), ErrorKind.Value, "string truncation", "8152", TooLong),
        new(Wording("Cannot insert the value NULL into column"), ErrorKind.Value, "null not allowed", "515", NotNull),
        new(Wording("conflicted with the (?:FOREIGN KEY|CHECK|REFERENCE) constraint"), ErrorKind.Value, "constraint violation", "547", Integrity),
        new(Wording("Invalid column name"), ErrorKind.Name, "undefined column", "207"),
        new(Wording("Invalid object name"), ErrorKind.Name, "undefined table", "208"),
        new(Wording("Ambiguous column name"), ErrorKind.Name, "ambiguous column", "209"),
        new(Wording("The multi-part identifier .{1,300}? could not be bound"), ErrorKind.Name, "unbound identifier", "4104"),
        new(Wording("is invalid in the select list because it is not contained in"), ErrorKind.Name, "grouping error", "8120"),
        new(Wording("is not a recognized built-in function name"), ErrorKind.Name, "undefined function", "195"),
        new(Wording("Incorrect syntax near"), ErrorKind.NoValue, "syntax error", "102"),
        new(Wording("Divide by zero error"), ErrorKind.NoValue, "division by zero", "8134"),
        new(Wording("Operand type clash"), ErrorKind.NoValue, "type mismatch", "206"),
        new(Wording("permission was denied on the object"), ErrorKind.NoValue, "permission denied", "229"),

        // MySQL
        new(Wording(@"Incorrect (?:date|datetime|time|timestamp) value"), ErrorKind.Value, "incorrect value", "1292", DateTimeValue),
        new(Wording(@"Truncated incorrect \w+ value"), ErrorKind.Value, "incorrect value", "1292", Conversion),
        new(Wording(@"Incorrect \w+ value"), ErrorKind.Value, "incorrect value", "1366", Conversion),
        new(Wording("Duplicate entry"), ErrorKind.Value, "duplicate entry", "1062", Duplicate),
        new(Wording("Out of range value"), ErrorKind.Value, "out-of-range value", "1264", OutOfRange),
        new(Wording("Data too long for column"), ErrorKind.Value, "data too long", "1406", TooLong),
        new(Wording("Column '[^']{1,300}' cannot be null"), ErrorKind.Value, "null not allowed", "1048", NotNull),
        new(Wording("Unknown column"), ErrorKind.Name, "undefined column", "1054"),
        new(Wording("Table '[^']{1,300}' doesn't exist"), ErrorKind.Name, "undefined table", "1146"),
        new(Wording("Column '[^']{1,300}' in .{1,100}? is ambiguous"), ErrorKind.Name, "ambiguous column", "1052"),
        new(Wording(@"FUNCTION \S{1,300} does not exist"), ErrorKind.Name, "undefined function", "1305"),
        new(Wording("is not in GROUP BY clause|isn't in GROUP BY"), ErrorKind.Name, "grouping error", "1055"),
        new(Wording("You have an error in your SQL syntax"), ErrorKind.NoValue, "syntax error", "1064"),
        new(Wording("command denied to user"), ErrorKind.NoValue, "permission denied", "1142"),

        // BigQuery
        new(Wording(@"Bad \w+ value"), ErrorKind.Value, "invalid value", null, Conversion),
        new(Wording("Could not cast"), ErrorKind.Value, "invalid cast", null, Conversion),
        new(Wording("Invalid cast"), ErrorKind.Value, "invalid cast", null, Conversion),
        new(Wording(@"Invalid (?:date|datetime|timestamp|time)\b"), ErrorKind.Value, "invalid value", null, DateTimeValue),
        new(Wording("Invalid (?:NUMERIC|BIGNUMERIC|INTERVAL) value"), ErrorKind.Value, "invalid value", null, Conversion),
        new(Wording("Unrecognized name"), ErrorKind.Name, "undefined column", null),
        new(Wording("Function not found"), ErrorKind.Name, "undefined function", null),
        new(Wording("Not found: (?:Table|Dataset)|Table .{1,300}? was not found"), ErrorKind.Name, "undefined table", null),
        new(Wording("which is neither grouped nor aggregated"), ErrorKind.Name, "grouping error", null),
        new(Wording("No matching signature for"), ErrorKind.NoValue, "type mismatch", null),

        // Snowflake
        new(Wording("(?:Numeric|Boolean) value .{0,300}? is not recognized"), ErrorKind.Value, "invalid value", "100038", Conversion),
        new(Wording("(?:Date|Timestamp|Time) .{0,300}? is not recognized"), ErrorKind.Value, "invalid value", "100035", DateTimeValue),
        new(Wording("invalid identifier"), ErrorKind.Name, "undefined column", "000904"),
        new(Wording("does not exist or not authorized"), ErrorKind.Name, "undefined object", "002003"),

        // SQLite (the in-memory join)
        new(Wording("no such (?:column|table|function)"), ErrorKind.Name, "undefined name", null),
        new(Wording("ambiguous column name"), ErrorKind.Name, "ambiguous column", null),

        // Any engine
        new(Wording("division by zero|divide by zero"), ErrorKind.NoValue, "division by zero", null),
        new(Wording(@"\b(?:cannot|could not|can't|unable to|failed to) (?:parse|convert|cast)\b"), ErrorKind.Value, "conversion error", null, Conversion),
        new(Wording("invalid input syntax|invalid input value"), ErrorKind.Value, "invalid input", null, Conversion),
        new(Wording("out of range"), ErrorKind.Value, "out-of-range value", null, OutOfRange),
        new(Wording("duplicate key|violates (?:unique|not-null|foreign key|check|exclusion) constraint"), ErrorKind.Value, "constraint violation", null, Integrity),
        new(Wording("null value in column"), ErrorKind.Value, "null not allowed", null, NotNull),
        new(Wording("(?:column|relation|function|table|schema) .{1,300}? does not exist"), ErrorKind.Name, "undefined name", null),
        new(Wording("is ambiguous"), ErrorKind.Name, "ambiguous name", null),
        new(Wording("must appear in the GROUP BY clause|missing FROM-clause entry"), ErrorKind.Name, "grouping error", null),
        new(Wording("syntax error|mismatched input"), ErrorKind.NoValue, "syntax error", null),
        new(Wording("operator does not exist|but expression is of type"), ErrorKind.NoValue, "type mismatch", null),
        new(Wording("permission denied|access denied"), ErrorKind.NoValue, "permission denied", null),
        new(Wording("statement timeout|timed out|was cancell?ed"), ErrorKind.NoValue, "query cancelled", null),
        new(Wording("read-only transaction"), ErrorKind.NoValue, "read-only transaction", null)
    ];

    // Every class name Beacon writes itself, so its own output is recognised when it is screened again.
    private static readonly HashSet<string> OwnClassNames = new(
        Rules
            .Select(x => x.Name)
            .Concat(
            [
                "data exception", "integrity constraint violation", "division by zero", "undefined column", "undefined table",
                "undefined function", "undefined object", "ambiguous column", "ambiguous function", "ambiguous alias",
                "duplicate alias", "grouping error", "invalid column reference", "syntax error", "permission denied",
                "type mismatch", "read-only transaction", "query cancelled", "syntax or access rule violation", "unrecognised"
            ]),
        StringComparer.Ordinal);

    // Beacon's own fixed texts that reach the screens beside the database's errors: they hold no value, so they are kept.
    private static readonly HashSet<string> BeaconMessages = new(StringComparer.Ordinal)
    {
        DatabaseProvider.HostQueryFailedMessage,
        DatabaseProvider.HostResultMismatchMessage,
        DatabaseProvider.MySqlReadOnlyNotClosedMessage,
        DatabaseProvider.SqlServerTransactionEndedMessage,
        MigrationWriteTargets.ReadRefusedMessage
    };

    // The target types a conversion's description may name: only a word Beacon knows as a type name is ever repeated.
    private static readonly HashSet<string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "integer", "int2", "int4", "int8", "smallint", "bigint", "tinyint", "mediumint", "int64", "int32",
        "numeric", "decimal", "number", "bignumeric", "real", "float", "float4", "float8", "float64", "double",
        "double precision", "money", "smallmoney", "bool", "boolean", "bit", "date", "time", "timestamp", "timestamptz",
        "timestamp with time zone", "timestamp without time zone", "time with time zone", "time without time zone",
        "datetime", "datetime2", "datetimeoffset", "smalldatetime", "interval", "uuid", "uniqueidentifier", "json", "jsonb",
        "xml", "string", "varchar", "nvarchar", "char", "nchar", "character", "character varying", "text", "bytea",
        "binary", "varbinary", "inet", "cidr", "macaddr", "year", "enum", "array", "struct", "map", "geography", "geometry"
    };

    // The detail leads whose parenthesised text is a key or a row: PostgreSQL's Key (col)=(…) and Failing row contains (…),
    // SQL Server's duplicate key value is (…).
    private static readonly string[] RowDetailLeads = [")=(", "contains (", "value is ("];

    [GeneratedRegex(@"^\s*(?<code>[0-9A-Z]{5}):\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingSqlState();

    [GeneratedRegex(@"\bSQL\s?STATE\s*[:=]?\s*(?<code>[0-9A-Z]{5})\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedSqlState();

    [GeneratedRegex(@"^The query returned more than (?<ceiling>[0-9]{1,9}) rows,", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MoreRowsThanLead();

    // The lead TextOf writes for a SQL Server error, in the form SQL Server's own tools print.
    [GeneratedRegex(@"^\s*Msg (?<number>[0-9]{1,10}), Level [0-9]{1,3}, State [0-9]{1,3}:\s", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlServerMessageLead();

    // A Databricks / Spark error class: upper-case words joined by underscores, optionally with a sub-class.
    [GeneratedRegex(@"\[(?<class>[A-Z]+(?:_[A-Z]+)+(?:\.[A-Z]+(?:_[A-Z]+)*)?)\]", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ErrorClassName();

    [GeneratedRegex(@"^(?<class>Database error(?: [0-9A-Z]{1,6})? \((?<name>[A-Za-z_. -]{1,80})\))(?:: (?<description>[^\r\n]{1,120}?))?\.(?: The database's message is withheld because it can quote row values\.)?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex OwnOutput();

    [GeneratedRegex(
        @"\bfor type (?<type>[A-Za-z][A-Za-z0-9 ]{0,40}?)\s*(?::|$)|\bto (?:data )?type ""?(?<type>[A-Za-z][A-Za-z0-9_ ]{0,40}?)""?(?=[\s.,;:)]|$)|\b(?:cast|converted|convert) to ""?(?<type>[A-Za-z][A-Za-z0-9_]{0,40})""?|\b(?:to|as) (?<type>[A-Za-z][A-Za-z0-9_]{0,40})\.?[ \t]*$|\bIncorrect (?<type>\w{1,40}) value|\bTruncated incorrect (?<type>\w{1,40}) value|\bBad (?<type>\w{1,40}) value|\b(?<type>Numeric|Boolean) value",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex TargetType();

    [GeneratedRegex(@"^(?<lead>[ \t]*CONTEXT:[ \t]*JSON data, line \d+:)[^\r\n]*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex JsonContext();

    [GeneratedRegex(@"(?<lead>\b(?:cannot|could not|can't|unable to|failed to) (?:parse|convert|cast) )(?![""'`<])(?<value>[^\r\n]{1,300}?)(?<tail> (?:as|to|into) )", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnquotedConversion();

    [GeneratedRegex(@"(?<lead>\bvalues?[ \t]*[:=][ \t]*)(?![""'`<\s])[^;\r\n]+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UnquotedValue();

    [GeneratedRegex(@"'(?:[^']|'')*'", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlStringLiteral();

    [GeneratedRegex(@"""(?<name>[^""]+)""|`(?<name>[^`]+)`|\[(?<name>[^\]]+)\]|(?<name>[\p{L}_][\p{L}\p{N}_$#@]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SqlName();

    /// <summary>
    /// <paramref name="error"/> fit to leave Beacon (see the type): a value-carrying class reduced to its class, code and a
    /// generic description; any other error with its quoted text (unless every dotted part of it is one of
    /// <paramref name="knownNames"/> in an error about a name), its key and row details, brace records, unquoted values and
    /// JSON context replaced with <see cref="Placeholder"/>, then cut to 2,000 characters. An error that cannot be screened
    /// in time is replaced by <see cref="UnscreenedMessage"/>, counted and logged to <paramref name="logger"/> without its text.
    /// </summary>
    public static string Redact(string error, IReadOnlyCollection<string> knownNames, ILogger? logger = null)
    {
        return Screened(() => RedactCore(error, knownNames), logger);
    }

    /// <summary>
    /// The error text a caller, the audit trail or a stored record gets for a failed statement: redacted
    /// (<see cref="Redact"/>, with the names of <paramref name="sql"/> known) while <paramref name="redact"/> — the effective
    /// PII detection — is on, and as it is otherwise.
    /// </summary>
    public static string ForCaller(string error, string? sql, bool redact, ILogger? logger = null)
    {
        return redact
            ? Redact(error, KnownNames(sql ?? "", null), logger)
            : error;
    }

    /// <summary>
    /// Only the class and code of <paramref name="error"/>, none of its text — what may go to the LLM provider while row data
    /// may not (<c>Beacon:Mcp:ForbidRowDataToLlm</c>), for example <c>Database error 42703 (undefined column).</c>
    /// </summary>
    public static string ClassOnly(string error, ILogger? logger = null)
    {
        return Screened(
            () =>
            {
                var text = Bounded(error);
                if (text == UnscreenedMessage)
                {
                    return text;
                }

                return IsOwnOutput(text, out var classPart)
                    ? $"{classPart}."
                    : Describe(Classify(text), withDescription: false);
            },
            logger);
    }

    /// <summary>
    /// The text Beacon passes on for a statement that failed with <paramref name="exception"/>: its message, led by the
    /// error's code when the statement's own code raised the error and the message does not name it — SQL Server's errors
    /// 50000 and above as <c>Msg 50000, Level 16, State 1:</c>, a MySQL SQLSTATE of class 45 as <c>45000:</c> (PostgreSQL's
    /// message already starts with its SQLSTATE) — so the error is withheld whatever its author wrote into it.
    /// </summary>
    public static string TextOf(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is SqlException sqlServer)
            {
                var raised = sqlServer.Errors
                    .Cast<SqlError>()
                    .Where(x => x.Number >= FirstSqlServerUserError)
                    .FirstOrDefault();

                return raised == null
                    ? exception.Message
                    : $"Msg {raised.Number}, Level {raised.Class}, State {raised.State}: {exception.Message}";
            }

            if (current is DbException { SqlState: { Length: > 0 } sqlState })
            {
                return !IsRaisedSqlState(sqlState) || exception.Message.TrimStart().StartsWith(sqlState, StringComparison.Ordinal)
                    ? exception.Message
                    : $"{sqlState}: {exception.Message}";
            }
        }

        return exception.Message;
    }

    /// <summary>
    /// The names an error about <paramref name="sql"/> may keep: the words of the statement outside its string literals (its
    /// tables, columns, aliases, functions and keywords, quoted or not) and the table, <c>schema.table</c> and column names
    /// of <paramref name="catalog"/>. A literal's content is not a name.
    /// </summary>
    public static IReadOnlySet<string> KnownNames(string sql, IReadOnlyDictionary<string, HashSet<string>>? catalog)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var withoutLiterals = SqlStringLiteral().Replace(sql, " ");
            foreach (Match name in SqlName().Matches(withoutLiterals))
            {
                foreach (var part in name.Groups["name"].Value.Split('.'))
                {
                    names.Add(part.Trim());
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // No names from the statement: the error keeps only the catalog's names.
        }

        foreach (var (table, columns) in catalog ?? new Dictionary<string, HashSet<string>>())
        {
            names.UnionWith(table.Split('.'));
            names.UnionWith(columns);
        }

        return names;
    }

    /// <summary>
    /// Runs <paramref name="screen"/>; when a screen runs out of time the error is replaced as a whole, counted and logged
    /// without its text. Internal so the fallback can be tested without a slow input.
    /// </summary>
    internal static string Screened(Func<string> screen, ILogger? logger)
    {
        try
        {
            return screen();
        }
        catch (RegexMatchTimeoutException)
        {
            BeaconTelemetry.ErrorRedactionFallbacks.Add(1);
            logger?.LogWarning("A database error could not be screened in time; it was replaced as a whole");

            return UnscreenedMessage;
        }
    }

    /// <summary>
    /// <paramref name="text"/> with JSON context lines, key and row details, quoted text (unless <paramref name="known"/>
    /// holds every dotted part of it), brace records and unquoted values replaced with <see cref="Placeholder"/> — the
    /// redaction a value-free error gets, without the class rule or the cut. Internal for the shape tests.
    /// </summary>
    internal static string RedactText(string text, IReadOnlySet<string>? known)
    {
        text = JsonContext().Replace(text, x => $"{x.Groups["lead"].Value} {Placeholder}");
        text = RedactRowDetails(text);
        text = RedactQuoted(text, known);
        text = RedactBraceRecords(text);
        text = UnquotedConversion().Replace(text, x => $"{x.Groups["lead"].Value}{Placeholder}{x.Groups["tail"].Value}");

        return UnquotedValue().Replace(text, x => $"{x.Groups["lead"].Value}{Placeholder}");
    }

    private static string RedactCore(string error, IReadOnlyCollection<string> knownNames)
    {
        var text = Bounded(error);
        if (text == UnscreenedMessage || IsOwnOutput(text, out _) || IsBeaconMessage(text))
        {
            return text;
        }

        var errorClass = Classify(text);

        if (errorClass.Kind is ErrorKind.Value or ErrorKind.Unrecognised)
        {
            return $"{Describe(errorClass, withDescription: true)} {Withheld}";
        }

        var known = errorClass.Kind == ErrorKind.Name
            ? new HashSet<string>(knownNames, StringComparer.OrdinalIgnoreCase)
            : null;
        var redacted = RedactText(text, known);

        return redacted.Length > MaxLength ? redacted[..MaxLength] : redacted;
    }

    private static string Bounded(string error)
    {
        return error.Length > MaxScreenedLength ? error[..MaxScreenedLength] : error;
    }

    private static ErrorClass Classify(string text)
    {
        var sqlState = LeadingSqlState().Match(text) is { Success: true } leading
            ? leading.Groups["code"].Value
            : NamedSqlState().Match(text) is { Success: true } named
                ? named.Groups["code"].Value
                : null;

        // An error the statement's own code raised says whatever its author wrote: its wording is never read as a class.
        var raisedCode = RaisedErrorCode(text, sqlState);
        if (raisedCode != null)
        {
            return new ErrorClass(ErrorKind.Unrecognised, "unrecognised", raisedCode);
        }

        var className = ErrorClassName().Match(text) is { Success: true } bracketed
            ? bracketed.Groups["class"].Value
            : null;

        // An error class is named in the result only when Beacon recognises it, so a bracketed value is never repeated.
        var byClassName = className == null ? null : FromClassName(className, text);
        var bySqlState = sqlState == null ? null : FromSqlState(sqlState, text);
        if (bySqlState != null)
        {
            return byClassName == null ? bySqlState : bySqlState with { Name = byClassName.Name };
        }

        if (byClassName != null)
        {
            return byClassName;
        }

        foreach (var rule in Rules)
        {
            if (rule.Pattern.IsMatch(text))
            {
                return new ErrorClass(rule.Kind, rule.Name, sqlState ?? rule.Code, Described(rule.Description, text));
            }
        }

        return new ErrorClass(ErrorKind.Unrecognised, "unrecognised", sqlState);
    }

    // The code of an error the statement's own code raised: a SQLSTATE of class P0 (PostgreSQL's RAISE, Databricks'
    // raise_error) or of MySQL's class 45 (SIGNAL), or a SQL Server error number of 50000 and above as TextOf leads it.
    private static string? RaisedErrorCode(string text, string? sqlState)
    {
        if (sqlState != null && IsRaisedSqlState(sqlState))
        {
            return sqlState;
        }

        return SqlServerMessageLead().Match(text) is { Success: true } lead
            && long.TryParse(lead.Groups["number"].Value, out var number)
            && number >= FirstSqlServerUserError
                ? lead.Groups["number"].Value
                : null;
    }

    private static bool IsRaisedSqlState(string sqlState)
    {
        return sqlState.StartsWith("P0", StringComparison.OrdinalIgnoreCase) || sqlState.StartsWith("45", StringComparison.Ordinal);
    }

    // Every engine that reports a SQLSTATE: classes 22 and 23 carry values (division by zero excepted); the name errors of
    // class 42 keep their known names; the rest of 42 and the cancellation and read-only codes carry none. Other codes are
    // left to the wording rules.
    private static ErrorClass? FromSqlState(string sqlState, string text)
    {
        if (sqlState == "22012")
        {
            return new ErrorClass(ErrorKind.NoValue, "division by zero", sqlState);
        }

        if (sqlState.StartsWith("22", StringComparison.Ordinal))
        {
            var description = sqlState switch
            {
                "22P02" or "22018" or "22P03" or "22P04" or "22P05" or "2200G" => Conversion,
                "22007" or "22008" or "22009" or "2200N" => DateTimeValue,
                "22003" => OutOfRange,
                "22001" => TooLong,
                "22004" or "22002" => NotNull,
                _ => DataValue
            };

            return new ErrorClass(ErrorKind.Value, "data exception", sqlState, Described(description, text));
        }

        if (sqlState.StartsWith("23", StringComparison.Ordinal))
        {
            var description = sqlState switch
            {
                "23505" => Duplicate,
                "23502" => NotNull,
                _ => Integrity
            };

            return new ErrorClass(ErrorKind.Value, "integrity constraint violation", sqlState, description);
        }

        return sqlState switch
        {
            "42703" => new ErrorClass(ErrorKind.Name, "undefined column", sqlState),
            "42P01" => new ErrorClass(ErrorKind.Name, "undefined table", sqlState),
            "42883" => new ErrorClass(ErrorKind.Name, "undefined function", sqlState),
            "42704" => new ErrorClass(ErrorKind.Name, "undefined object", sqlState),
            "42702" => new ErrorClass(ErrorKind.Name, "ambiguous column", sqlState),
            "42725" => new ErrorClass(ErrorKind.Name, "ambiguous function", sqlState),
            "42P09" => new ErrorClass(ErrorKind.Name, "ambiguous alias", sqlState),
            "42712" => new ErrorClass(ErrorKind.Name, "duplicate alias", sqlState),
            "42803" => new ErrorClass(ErrorKind.Name, "grouping error", sqlState),
            "42P10" => new ErrorClass(ErrorKind.Name, "invalid column reference", sqlState),
            "42601" => new ErrorClass(ErrorKind.NoValue, "syntax error", sqlState),
            "42501" => new ErrorClass(ErrorKind.NoValue, "permission denied", sqlState),
            "42804" or "42846" or "42P18" => new ErrorClass(ErrorKind.NoValue, "type mismatch", sqlState),
            "25006" => new ErrorClass(ErrorKind.NoValue, "read-only transaction", sqlState),
            "57014" => new ErrorClass(ErrorKind.NoValue, "query cancelled", sqlState),
            _ when sqlState.StartsWith("42", StringComparison.Ordinal) => new ErrorClass(ErrorKind.NoValue, "syntax or access rule violation", sqlState),
            _ => null
        };
    }

    // Databricks / Spark error classes that came without a SQLSTATE.
    private static ErrorClass? FromClassName(string className, string text)
    {
        if (className.Contains("DIVIDE_BY_ZERO", StringComparison.Ordinal) || className.Contains("SYNTAX", StringComparison.Ordinal))
        {
            return new ErrorClass(ErrorKind.NoValue, className, null);
        }

        if (className.Contains("UNRESOLVED", StringComparison.Ordinal)
            || className.Contains("NOT_FOUND", StringComparison.Ordinal)
            || className.Contains("AMBIGUOUS", StringComparison.Ordinal)
            || className.Contains("MISSING_AGGREGATION", StringComparison.Ordinal))
        {
            return new ErrorClass(ErrorKind.Name, className, null);
        }

        if (className.Contains("CAST", StringComparison.Ordinal)
            || className.Contains("PARSE", StringComparison.Ordinal)
            || className.Contains("FORMAT", StringComparison.Ordinal)
            || className.Contains("MALFORMED", StringComparison.Ordinal))
        {
            return new ErrorClass(ErrorKind.Value, className, null, Described(Conversion, text));
        }

        if (className.Contains("OVERFLOW", StringComparison.Ordinal) || className.Contains("OUT_OF_RANGE", StringComparison.Ordinal))
        {
            return new ErrorClass(ErrorKind.Value, className, null, OutOfRange);
        }

        return className.Contains("DATETIME", StringComparison.Ordinal)
            ? new ErrorClass(ErrorKind.Value, className, null, DateTimeValue)
            : null;
    }

    // A conversion's description names its target type when the error names one Beacon knows as a type name — never
    // anything else the error says.
    private static string? Described(string? description, string text)
    {
        if (description != Conversion)
        {
            return description;
        }

        foreach (Match match in TargetType().Matches(text))
        {
            var type = match.Groups["type"].Value.Trim();
            if (TypeNames.Contains(type))
            {
                return Conversion.Replace("{type}", type, StringComparison.Ordinal);
            }
        }

        return Conversion.Replace("{type}", AnyTargetType, StringComparison.Ordinal);
    }

    // Text this type wrote itself — a class, a code and one of its own descriptions — passes a second screen unchanged, so
    // an error redacted where it was read keeps its description when a later step screens it again. Every part is checked
    // against what Beacon writes, so no value can pass as one.
    private static bool IsOwnOutput(string text, out string classPart)
    {
        classPart = "";
        if (!text.StartsWith("Database error", StringComparison.Ordinal))
        {
            return false;
        }

        var match = OwnOutput().Match(text);
        if (!match.Success)
        {
            return false;
        }

        var name = match.Groups["name"].Value;
        var ownName = OwnClassNames.Contains(name) || (ErrorClassName().IsMatch($"[{name}]") && FromClassName(name, "") != null);
        var description = match.Groups["description"];
        if (!ownName || (description.Success && !IsOwnDescription(description.Value)))
        {
            return false;
        }

        classPart = match.Groups["class"].Value;

        return true;
    }

    private static bool IsBeaconMessage(string text)
    {
        if (BeaconMessages.Contains(text))
        {
            return true;
        }

        return MoreRowsThanLead().Match(text) is { Success: true } lead
            && int.TryParse(lead.Groups["ceiling"].Value, out var ceiling)
            && text == DatabaseProvider.MoreRowsThanMessage(ceiling);
    }

    private static bool IsOwnDescription(string description)
    {
        if (description is DateTimeValue or OutOfRange or TooLong or Duplicate or NotNull or Integrity or DataValue or NotRecognised)
        {
            return true;
        }

        if (!description.StartsWith(ConversionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var type = description[ConversionPrefix.Length..];

        return type == AnyTargetType || TypeNames.Contains(type);
    }

    private static string Describe(ErrorClass errorClass, bool withDescription)
    {
        var code = errorClass.Code == null ? "" : $" {errorClass.Code}";
        var description = withDescription
            ? errorClass.Description ?? (errorClass.Kind == ErrorKind.Unrecognised ? NotRecognised : null)
            : null;

        return description == null
            ? $"Database error{code} ({errorClass.Name})."
            : $"Database error{code} ({errorClass.Name}): {description}.";
    }

    // Quoted text, scanned rather than matched so a span may run across lines and hold quote characters: double quotes and
    // backticks close at the next one; a single quote opens a span only outside a word (doesn't, result's are apostrophes)
    // and closes at the next one that is neither doubled ('') nor between two letters (O'Brien). A span that never closes
    // takes the rest of the text.
    private static string RedactQuoted(string text, IReadOnlySet<string>? known)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var opens = c is '"' or '`' || (c == '\'' && OpensSingleQuote(text, i));
            if (!opens)
            {
                builder.Append(c);
                i++;
                continue;
            }

            var close = ClosingQuote(text, i, c);
            if (close < 0)
            {
                return builder.Append(c).Append(Placeholder).ToString();
            }

            var inner = text.Substring(i + 1, close - i - 1);
            if (known != null && IsKnown(inner, known))
            {
                builder.Append(text, i, close - i + 1);
            }
            else
            {
                builder.Append(c).Append(Placeholder).Append(c);
            }

            i = close + 1;
        }

        return builder.ToString();
    }

    private static bool OpensSingleQuote(string text, int index)
    {
        return index == 0 || !char.IsLetterOrDigit(text[index - 1]);
    }

    private static int ClosingQuote(string text, int open, char quote)
    {
        for (var j = open + 1; j < text.Length; j++)
        {
            if (text[j] != quote)
            {
                continue;
            }

            if (quote == '\'' && j + 1 < text.Length)
            {
                if (text[j + 1] == '\'')
                {
                    j++;
                    continue;
                }

                if (char.IsLetter(text[j - 1]) && char.IsLetter(text[j + 1]))
                {
                    continue;
                }
            }

            return j;
        }

        return -1;
    }

    // Key and row details: the parenthesised text after each lead, matched by depth so a value holding parentheses goes
    // whole; one that never closes takes the rest of the text.
    private static string RedactRowDetails(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var lead = NextLead(text, i, out var leadLength);
            if (lead < 0)
            {
                builder.Append(text, i, text.Length - i);
                break;
            }

            var open = lead + leadLength - 1;
            builder.Append(text, i, open - i + 1);
            var close = Closing(text, open, '(', ')');
            if (close < 0)
            {
                return builder.Append(Placeholder).ToString();
            }

            builder.Append(Placeholder).Append(')');
            i = close + 1;
        }

        return builder.ToString();
    }

    // A record printed in braces with a field in it ({name: Ivan, age: 5}) goes whole; one that never closes takes the rest.
    private static string RedactBraceRecords(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '{')
            {
                builder.Append(text[i]);
                i++;
                continue;
            }

            var close = Closing(text, i, '{', '}');
            if (close < 0)
            {
                return builder.Append('{').Append(Placeholder).ToString();
            }

            if (text.AsSpan(i, close - i).Contains(':'))
            {
                builder.Append('{').Append(Placeholder).Append('}');
            }
            else
            {
                builder.Append(text, i, close - i + 1);
            }

            i = close + 1;
        }

        return builder.ToString();
    }

    private static int NextLead(string text, int from, out int leadLength)
    {
        var best = -1;
        leadLength = 0;
        foreach (var lead in RowDetailLeads)
        {
            var index = text.IndexOf(lead, from, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (best < 0 || index < best))
            {
                best = index;
                leadLength = lead.Length;
            }
        }

        return best;
    }

    private static int Closing(string text, int open, char opening, char closing)
    {
        var depth = 0;
        for (var j = open; j < text.Length; j++)
        {
            if (text[j] == opening)
            {
                depth++;
            }
            else if (text[j] == closing && --depth == 0)
            {
                return j;
            }
        }

        return -1;
    }

    // Quoted text is kept when it is empty or every dotted part of it is a known name.
    private static bool IsKnown(string inner, IReadOnlySet<string> known)
    {
        if (inner.Length == 0)
        {
            return true;
        }

        return inner
            .Split('.')
            .Select(x => x.Trim().Trim('"', '`', '[', ']'))
            .All(x => x.Length > 0 && known.Contains(x));
    }

    private static Regex Wording(string pattern)
    {
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    }
}
