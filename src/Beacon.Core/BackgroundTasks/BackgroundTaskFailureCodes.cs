using Beacon.Core.Models;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// Why a background task failed, as stored on it and shown to the caller. A code, never the exception's message: a message
/// can carry SQL, names or connection details (§1.11).
/// </summary>
public static class BackgroundTaskFailureCodes
{
    /// <summary>It stayed queued or running past its kind's limit and was given up when the next one started.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The host's job runner refused it.</summary>
    public const string EnqueueFailed = "enqueue_failed";

    /// <summary>No executor is registered for its kind.</summary>
    public const string NoExecutor = "no_executor";

    public const string Cancelled = "cancelled";

    /// <summary>Another run of the same work was in progress (a skipped task: that run's result is the current one).</summary>
    public const string Busy = "busy";

    public const string DataSourceDisabled = "data_source_disabled";

    public const string InsecureTransport = "insecure_transport";

    public const string NotFound = "not_found";

    /// <summary>The caller, or for a task the user who started it, may not (or may no longer) do the work.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>Anything else; the server log has the details.</summary>
    public const string Failed = "failed";

    /// <summary>The code for an exception a task failed with.</summary>
    public static string Of(Exception exception) => exception switch
    {
        OperationCanceledException => Cancelled,
        BeaconRateLimitedException => Busy,
        DataSourceDisabledException => DataSourceDisabled,
        DataSourceTransportException => InsecureTransport,
        BeaconNotFoundException => NotFound,
        BeaconForbiddenException => Forbidden,
        _ => Failed
    };
}
