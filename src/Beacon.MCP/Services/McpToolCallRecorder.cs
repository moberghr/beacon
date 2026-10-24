using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Beacon.Core.Services.Security;

namespace Beacon.MCP.Services;

/// <summary>
/// The scoped record of one MCP tool call (§1.7, §9.5): the tool, the caller's Beacon user, its input, project and data
/// source, the outcome (row count or error, the tables it read) and, for the SQL-carrying tools only, the learning signal.
/// A tool fills it in as the call proceeds; <see cref="McpAuditCallToolFilter"/> writes it after the tool returned or
/// threw — the audit row synchronously through <see cref="McpAuditService"/> (so the fail-closed audit sees whether it was
/// written), then the signal when there is one. The playground's calls go through the same path.
/// <para>
/// A tool that still writes its own audit row (<see cref="McpAuditService.LogToolCallAsync"/>) leaves the recorder
/// unused; when that row was attempted, <see cref="WriteAsync"/> writes nothing, so a call never gets two rows.
/// </para>
/// </summary>
internal sealed class McpToolCallRecorder(
    McpAuditService auditService,
    McpSignalService signalService,
    McpAuditOutcome outcome,
    ILogger<McpToolCallRecorder> logger)
{
    /// <summary>The tools that carry SQL and so record a learning signal (§9.5); every other tool is audit-only.</summary>
    public static readonly IReadOnlySet<string> SignalTools = new HashSet<string>(StringComparer.Ordinal) { "ask", "query", "dry_run" };

    private readonly Stopwatch _stopwatch = new();
    private McpSignalBuilder? _signal;
    private string? _signalSql;
    private string? _signalError;
    private bool _signalSucceeded;
    private bool _written;
    private int? _signalId;

    public string? Tool { get; private set; }

    public int? UserId { get; private set; }

    public string? Parameters { get; private set; }

    public int? ProjectId { get; private set; }

    public int? DataSourceId { get; private set; }

    public int? ResultRowCount { get; private set; }

    public string? ErrorMessage { get; private set; }

    public IReadOnlyList<string>? Tables { get; private set; }

    public int ExecutionTimeMs { get; private set; }

    /// <summary>A call was started on this recorder.</summary>
    public bool HasCall => Tool != null;

    /// <summary>The call has its outcome: <see cref="Succeed"/> or <see cref="Fail"/> was called.</summary>
    public bool IsCompleted { get; private set; }

    /// <summary>Starts the call's record and its clock. One call per scope: a second start is a programming error.</summary>
    public McpToolCallRecorder Start(string tool, int? userId, string? parameters = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        if (HasCall)
        {
            throw new InvalidOperationException($"The tool call {Tool} is already being recorded in this scope.");
        }

        Tool = tool;
        UserId = userId;
        Parameters = parameters;
        _stopwatch.Restart();

        return this;
    }

    public McpToolCallRecorder WithParameters(string? parameters)
    {
        Parameters = parameters;

        return this;
    }

    public McpToolCallRecorder WithProject(int? projectId)
    {
        ProjectId = projectId;

        return this;
    }

    public McpToolCallRecorder WithDataSource(int? dataSourceId)
    {
        DataSourceId = dataSourceId;

        return this;
    }

    /// <summary>
    /// Attaches the call's learning signal. Only the SQL-carrying tools record one (§9.5); <paramref name="sql"/> is the
    /// caller's SQL, used to keep known names readable when an exception's text is redacted for the signal.
    /// </summary>
    public McpToolCallRecorder WithSignal(McpSignalBuilder signal, string? sql = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (Tool == null || !SignalTools.Contains(Tool))
        {
            throw new InvalidOperationException($"The {Tool ?? "unstarted"} tool is audit-only and records no learning signal.");
        }

        _signal = signal;
        _signalSql = sql;

        return this;
    }

    /// <summary>
    /// The call succeeded. <paramref name="signalSucceeded"/> is the signal's verdict when it differs from the call's
    /// (a dry run that found the SQL invalid, an answer whose SQL failed): the audit row has no error either way.
    /// </summary>
    public void Succeed(int? resultRowCount = null, IReadOnlyList<string>? tables = null, bool signalSucceeded = true)
    {
        Complete(resultRowCount, null, tables);
        _signalSucceeded = signalSucceeded;
    }

    /// <summary>
    /// The call failed or was refused with <paramref name="error"/>. The signal records <paramref name="signalError"/>
    /// when given (an error whose raw text must not be stored), else the same error.
    /// </summary>
    public void Fail(string error, IReadOnlyList<string>? tables = null, string? signalError = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(error);
        Complete(null, error, tables);
        _signalError = signalError ?? error;
        _signalSucceeded = false;
    }

    /// <summary>
    /// The call threw <paramref name="exception"/>: the audit row keeps its message (redacted there as the project's
    /// retention decides), the signal never its raw text (it can quote row values).
    /// </summary>
    internal void FailWith(Exception exception)
    {
        Fail(
            string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message,
            Tables,
            DatabaseErrorRedactor.ForCaller(exception.Message, _signal?.GeneratedSql ?? _signalSql, redact: true, logger));
    }

    /// <summary>
    /// Writes the audit row, then the signal when there is one, once; a second call returns the first one's signal id.
    /// Returns the persisted signal's id (for a tool that surfaces it), or null. Writes nothing when this scope's audit
    /// row was already attempted directly by the tool. A call not completed is written as failed.
    /// </summary>
    public async Task<int?> WriteAsync(CancellationToken cancellationToken)
    {
        if (!HasCall || _written)
        {
            return _signalId;
        }

        _written = true;
        if (outcome.Written || outcome.Failed)
        {
            logger.LogWarning(
                "MCP tool {Tool} wrote its audit row directly; the recorded call is not written a second time",
                McpAuditService.BoundedToolName(Tool));

            return null;
        }

        if (!IsCompleted)
        {
            Fail(McpAuditCallToolFilter.IncompleteCallMessage);
        }

        await auditService.LogToolCallAsync(
            UserId,
            Tool!,
            Parameters,
            DataSourceId,
            ProjectId,
            ExecutionTimeMs,
            ResultRowCount,
            ErrorMessage,
            Tables,
            cancellationToken);

        if (_signal == null)
        {
            return null;
        }

        if (_signalError != null)
        {
            _signal.SetExecutionFailed(_signalError);
        }

        _signal.SetResult(ResultRowCount, ExecutionTimeMs, _signalSucceeded);
        _signalId = await signalService.RecordSignalAsync(_signal.Build(), cancellationToken);

        return _signalId;
    }

    private void Complete(int? resultRowCount, string? error, IReadOnlyList<string>? tables)
    {
        if (!HasCall)
        {
            throw new InvalidOperationException("No tool call was started on this recorder.");
        }

        _stopwatch.Stop();
        ExecutionTimeMs = (int)_stopwatch.ElapsedMilliseconds;
        ResultRowCount = resultRowCount;
        ErrorMessage = error;
        Tables = tables ?? Tables;
        IsCompleted = true;
    }
}
