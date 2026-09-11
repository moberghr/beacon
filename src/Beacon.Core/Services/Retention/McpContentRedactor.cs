using System.Text;
using System.Text.Json;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// Pure redaction helpers for the content lock: they rewrite entity content in place and classify free-text errors
/// into a fixed vocabulary. No I/O, no logging, no settings reads — the caller decides whether the lock applies.
/// </summary>
public static class McpContentRedactor
{
    // Member names of the structural audit-parameter form. The emitter and the strict validator below share
    // them, so the two can never drift apart.
    private const string ToolMember = "tool";
    private const string ParamsMember = "params";
    private const string TablesMember = "tables";
    private const string NameMember = "name";
    private const string BytesMember = "bytes";
    private const string InputParameterName = "input";

    public const string ClassSchema = "schema";
    public const string ClassSyntax = "syntax";
    public const string ClassPermission = "permission";
    public const string ClassTimeout = "timeout";
    public const string ClassNotFound = "not_found";
    public const string ClassValidation = "validation";
    public const string ClassExecution = "execution";
    public const string ClassCancelled = "cancelled";
    public const string ClassUnknown = "unknown";

    /// <summary>The complete error-class vocabulary; a value already in it is left untouched by the belt.</summary>
    public static IReadOnlySet<string> ErrorClasses { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ClassSchema,
        ClassSyntax,
        ClassPermission,
        ClassTimeout,
        ClassNotFound,
        ClassValidation,
        ClassExecution,
        ClassCancelled,
        ClassUnknown
    };

    // Ordered: the first matching row wins, so the more specific causes (cancellation, timeout, permission) are
    // tested before the broad schema/not-found keywords that almost every provider message also contains.
    private static readonly (string Class, string[] Keywords)[] Classifiers =
    [
        // Timeout BEFORE cancelled on purpose: PostgreSQL reports a hit statement timeout as
        // "canceling statement due to statement timeout", which the bare "cancel" keyword would otherwise
        // classify as a user abort. A real user cancel ("canceling statement due to user request") carries no
        // timeout keyword and still lands on cancelled.
        (ClassTimeout, ["timeout", "timed out", "timeout expired"]),
        (ClassCancelled, ["cancel", "aborted by user"]),
        (ClassPermission, ["permission", "denied", "not authorized", "unauthorized", "privilege", "forbidden"]),
        (ClassSyntax, ["syntax", "parse error", "unterminated", "unexpected token"]),
        (ClassSchema, ["schema", "column", "relation", "undefined"]),
        // Not-found BEFORE validation: SQL Server reports schema drift as "Invalid object name 'orders'", which the
        // validation bucket's "invalid" keyword would otherwise swallow. MySQL contracts the verb
        // ("Table 'x' doesn't exist"), so both spellings are listed (review note, 2026-09-11).
        (ClassNotFound, ["not found", "no such", "does not exist", "doesn't exist", "invalid object name", "unknown table", "missing"]),
        (ClassValidation, ["invalid", "validation", "not allowed", "read-only", "read only"]),
        (ClassExecution, ["execution", "deadlock", "constraint", "division by zero", "conversion failed"])
    ];

    /// <summary>Maps a free-text provider error onto one class. <c>null</c> stays null; a class stays itself.</summary>
    public static string? ErrorClassOf(string? message)
    {
        if (message == null)
        {
            return null;
        }

        if (message.Length == 0 || ErrorClasses.Contains(message))
        {
            return message;
        }

        foreach (var classifier in Classifiers)
        {
            foreach (var keyword in classifier.Keywords)
            {
                if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return classifier.Class;
                }
            }
        }

        return ClassUnknown;
    }

    /// <summary>Strips every content-bearing field of a learning signal, keeping structure, flags and timings.</summary>
    public static void RedactSignal(McpQuerySignal signal)
    {
        signal.Question = string.Empty;
        signal.RoutingDecision = null;
        signal.GeneratedSql = null;
        signal.CorrectedSql = null;
        signal.UserCorrectedSql = null;
        signal.FeedbackNote = null;
        signal.SchemaValidationError = ErrorClassOf(signal.SchemaValidationError);
        signal.ExecutionError = ErrorClassOf(signal.ExecutionError);
        signal.DryRunError = ErrorClassOf(signal.DryRunError);
    }

    /// <summary>
    /// The redacted form of an audit row's <c>Parameters</c>: the tool, the size of the caller's input and the
    /// tables it touched — enough to audit the call, nothing of what was asked.
    /// </summary>
    public static string StructuralAuditParameters(string tool, string? parameters, IReadOnlyList<string>? tables)
    {
        var bytes = parameters == null ? 0 : Encoding.UTF8.GetByteCount(parameters);

        return JsonSerializer.Serialize(
            new
            {
                tool,
                @params = new[] { new { name = InputParameterName, bytes } },
                tables = tables ?? []
            });
    }

    /// <summary>True when the value is already the structural JSON form (so the belt leaves a braced write alone).</summary>
    public static bool IsStructuralAuditParameters(string? parameters)
    {
        if (string.IsNullOrEmpty(parameters))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(parameters);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Strict: the belt skips a value only when it is EXACTLY the shape this class emits. A permissive
            // check would let a future write site smuggle content past the belt inside an extra member, which is
            // the very case the belt exists for (review finding N2).
            foreach (var member in root.EnumerateObject())
            {
                if (member.Name is not (ToolMember or ParamsMember or TablesMember))
                {
                    return false;
                }
            }

            if (!root.TryGetProperty(ToolMember, out var tool) || tool.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            if (!root.TryGetProperty(TablesMember, out var tables) || tables.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            if (tables.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String))
            {
                return false;
            }

            if (!root.TryGetProperty(ParamsMember, out var parameterList) || parameterList.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var parameter in parameterList.EnumerateArray())
            {
                if (parameter.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (parameter.EnumerateObject().Any(x => x.Name is not (NameMember or BytesMember)))
                {
                    return false;
                }

                if (!parameter.TryGetProperty(NameMember, out var name)
                    || name.ValueKind != JsonValueKind.String
                    || name.GetString() != InputParameterName)
                {
                    return false;
                }

                if (!parameter.TryGetProperty(BytesMember, out var bytes) || bytes.ValueKind != JsonValueKind.Number)
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Strips the generated SQL and judge text off an eval result, keeping pass/fail and the error class.</summary>
    public static void RedactEvalResult(McpEvalResult result)
    {
        result.GeneratedSql = null;
        result.ExecutionError = ErrorClassOf(result.ExecutionError);
        result.JudgeVerdict = null;
    }
}
