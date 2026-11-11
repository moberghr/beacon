using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Beacon.Core.Data;
using Beacon.Core.Exceptions;

namespace Beacon.Core.Helpers;

/// <summary>
/// What a log may say about a failed query or call (§1.11): the exception's type, the driver's or service's error code,
/// the types of the exceptions it wraps, the method that threw it and, for an unhandled error, the methods on its stack —
/// never a message: a server, conversion or HTTP error message can quote query text, row values or a URL with a credential
/// in it.
/// </summary>
public static partial class FailureCodes
{
    private const int MaxInnerTypes = 5;

    /// <summary>The most stack frames <see cref="StackOf"/> names, over the exception and the ones it wraps together.</summary>
    internal const int MaxStackFrames = 40;

    // A service's error code is repeated only when it is a plain token ("ThrottlingException", "INVALID_PARAMETER_VALUE").
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex CodeToken();

    [GeneratedRegex(@"^<(?<method>[^>]+)>", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex GeneratedName();

    /// <summary>
    /// The error code of <paramref name="exception"/> or of an exception it wraps: SQL Server's error number, SQLite's
    /// result code (the in-memory join), the SQLSTATE the PostgreSQL and MySQL drivers expose through
    /// <see cref="DbException"/>, a data source service's own code (<see cref="DataSourceServiceException"/>, Databricks),
    /// Google's API error code (BigQuery), AWS's error code (CloudWatch) or an HTTP status code; null when there is none.
    /// </summary>
    public static string? ErrorCodeOf(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            // A SQL Server error that cannot be read gives no code.
            var sqlServer = SqlServerErrors.Read(current);
            if (sqlServer.IsSqlException)
            {
                return sqlServer.IsReadable ? sqlServer.Number.ToString(CultureInfo.InvariantCulture) : null;
            }

            var code = current switch
            {
                SqliteException sqlite => sqlite.SqliteErrorCode.ToString(CultureInfo.InvariantCulture),
                DbException { SqlState: { Length: > 0 } sqlState } => sqlState,
                DataSourceServiceException { ErrorCode: { } serviceCode } => Token(serviceCode),
                HttpRequestException { StatusCode: { } status } => ((int)status).ToString(CultureInfo.InvariantCulture),
                _ => GoogleApiCodeOf(current) ?? AwsCodeOf(current)
            };

            if (code != null)
            {
                return code;
            }
        }

        return null;
    }

    /// <summary>
    /// The failure as a log names it: <c>PostgresException (error code 22P02, inner SocketException, at
    /// NpgsqlConnector.ReadMessage)</c> — the type, the error code (<see cref="ErrorCodeOf"/>), the types of the exceptions
    /// it wraps and the method that threw it. Never a message.
    /// </summary>
    public static string Describe(Exception exception)
    {
        var detail = $"error code {ErrorCodeOf(exception) ?? "none"}";
        var inner = InnerTypesOf(exception);
        if (inner != null)
        {
            detail += $", inner {inner}";
        }

        var method = ThrowingMethodOf(exception);
        if (method != null)
        {
            detail += $", at {method}";
        }

        return $"{exception.GetType().Name} ({detail})";
    }

    /// <summary>The types of the exceptions <paramref name="exception"/> wraps, outermost first (<c>A &gt; B</c>); null when none.</summary>
    public static string? InnerTypesOf(Exception exception)
    {
        var types = new List<string>();
        for (var current = exception.InnerException; current != null && types.Count < MaxInnerTypes; current = current.InnerException)
        {
            types.Add(current.GetType().Name);
        }

        return types.Count == 0 ? null : string.Join(" > ", types);
    }

    /// <summary>
    /// The method that threw <paramref name="exception"/> as <c>Type.Method</c> — an async method by its own name, not its
    /// state machine's — or null when it was never thrown.
    /// </summary>
    public static string? ThrowingMethodOf(Exception exception)
    {
        var method = exception.TargetSite ?? new StackTrace(exception, false).GetFrame(0)?.GetMethod();

        return method == null ? null : NameOf(method);
    }

    /// <summary>
    /// The stack of <paramref name="exception"/> and of the exceptions it wraps, as an unhandled error's log names it:
    /// <c>InvalidOperationException at Service.Run, Endpoint.Handle; inner SocketException at Socket.Connect</c> — each
    /// exception's type and the methods of its frames, innermost call first, as <c>Type.Method</c> (an async method or
    /// lambda by its own name), at most <see cref="MaxStackFrames"/> frames in all. Frames the runtime hides from stack
    /// traces are left out. Never a message, an argument value or a file path.
    /// </summary>
    public static string StackOf(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var parts = new List<string>();
        var remaining = MaxStackFrames;
        var depth = 0;
        for (var current = exception; current != null && depth <= MaxInnerTypes; current = current.InnerException, depth++)
        {
            var frames = FramesOf(current);
            var shown = frames
                .Take(remaining)
                .ToList();
            remaining -= shown.Count;

            var part = current.GetType().Name;
            if (shown.Count > 0)
            {
                part += $" at {string.Join(", ", shown)}";
            }

            if (shown.Count < frames.Count)
            {
                part += $" (+{frames.Count - shown.Count} frames)";
            }

            parts.Add(part);
        }

        return string.Join("; inner ", parts);
    }

    private static List<string> FramesOf(Exception exception)
    {
        var names = new List<string>();
        foreach (var frame in new StackTrace(exception, false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method == null || IsHiddenFromStackTraces(method))
            {
                continue;
            }

            names.Add(NameOf(method));
        }

        return names;
    }

    private static bool IsHiddenFromStackTraces(MethodBase method)
    {
        try
        {
            return method.IsDefined(typeof(StackTraceHiddenAttribute), false)
                || method.DeclaringType?.IsDefined(typeof(StackTraceHiddenAttribute), false) == true;
        }
        catch (Exception)
        {
            // Metadata that cannot be read leaves the frame in: the log line is still written.
            return false;
        }
    }

    // Type.Method, with an async method, iterator or lambda named after the method that declares it.
    private static string NameOf(MethodBase method)
    {
        var type = method.DeclaringType;
        var name = method.Name;

        // An async method or iterator runs in a generated type named <Method>d__N nested in its own type.
        if (type is { DeclaringType: not null } && GeneratedName().Match(type.Name) is { Success: true } generated)
        {
            name = generated.Groups["method"].Value;
            type = type.DeclaringType;
        }

        // A lambda runs in a generated method named <Method>b__N.
        if (GeneratedName().Match(name) is { Success: true } lambda)
        {
            name = lambda.Groups["method"].Value;
        }

        while (type is { DeclaringType: not null } && type.Name.StartsWith('<'))
        {
            type = type.DeclaringType;
        }

        return type == null ? name : $"{type.Name}.{name}";
    }

    private static string? Token(string code)
    {
        try
        {
            return CodeToken().IsMatch(code) ? code : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    // Google's API error (BigQuery), read by name: Core does not reference the Google libraries. Its numeric error code,
    // else the HTTP status.
    private static string? GoogleApiCodeOf(Exception exception)
    {
        if (!IsOrDerivesFrom(exception.GetType(), "Google.GoogleApiException"))
        {
            return null;
        }

        var error = Property(exception, "Error");
        if (error != null && Property(error, "Code") is int code and > 0)
        {
            return code.ToString(CultureInfo.InvariantCulture);
        }

        return Property(exception, "HttpStatusCode") is System.Net.HttpStatusCode status
            ? ((int)status).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    // An AWS service error (CloudWatch), read by name: its error code when it is a plain token, else the HTTP status.
    private static string? AwsCodeOf(Exception exception)
    {
        if (!IsOrDerivesFrom(exception.GetType(), "Amazon.Runtime.AmazonServiceException"))
        {
            return null;
        }

        if (Property(exception, "ErrorCode") is string { Length: > 0 } errorCode && Token(errorCode) is { } token)
        {
            return token;
        }

        return Property(exception, "StatusCode") is System.Net.HttpStatusCode status
            ? ((int)status).ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static bool IsOrDerivesFrom(Type? type, string fullName)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static object? Property(object target, string name)
    {
        try
        {
            return target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);
        }
        catch (Exception)
        {
            // A property getter that throws gives no code; the log then says "none" rather than failing the log line.
            return null;
        }
    }
}
