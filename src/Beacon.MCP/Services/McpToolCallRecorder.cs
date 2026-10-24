using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Beacon.Core.Services.Security;

namespace Beacon.MCP.Services;

/// <summary>
/// The scoped record of one MCP tool call (§1.7, §9.5): the tool, the caller's Beacon user, its input, project and data
/// source, the outcome (row count or error, the tables it read) and, for the SQL-carrying tools only, the learning signal.
/// A tool fills it in as the call proceeds; <see cref="McpAuditCallToolFilter"/> writes it after the tool returned or
/// threw — the audit row synchronously through <see cref="McpAuditService"/> (so the fail-closed audit sees whether it was
/// written), then the signal when there is one. The playground's calls go through the same path.
/// <para>
/// The record is written only after the tool returned or threw, never by the tool itself, so the row always holds the
/// call's final outcome. A tool that still writes its own audit row (<see cref="McpAuditService.LogToolCallAsync"/>) and
/// leaves the recorder unused gets no second row once its row was written; when its write failed,
/// <see cref="WriteAsync"/> writes the row again.
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
    private bool _signalCountsRows = true;
    private bool _written;
    private bool _inInvocation;
    private int? _signalId;
    private Func<int?, CallToolResult>? _answerWithSignalId;

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

    /// <summary>
    /// The tool recorded its failure and rethrew the exception it failed with (the caller's own cancellation): the record
    /// keeps the tool's outcome and <see cref="McpAuditCallToolFilter"/> lets the exception propagate after writing it.
    /// </summary>
    internal bool RethrowsRecordedFailure { get; private set; }

    /// <summary>The SQL whose known names stay readable when a database error is redacted.</summary>
    internal string? RedactionSql => _signal?.GeneratedSql ?? _signalSql;

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
    /// (a dry run that found the SQL invalid, an answer whose SQL failed): the audit row has no error either way. With
    /// <paramref name="signalCountsRows"/> false the signal stores no row count (ask's signal never did), the audit row
    /// still does.
    /// </summary>
    public void Succeed(int? resultRowCount = null, IReadOnlyList<string>? tables = null, bool signalSucceeded = true, bool signalCountsRows = true)
    {
        Complete(resultRowCount, null, tables);
        _signalSucceeded = signalSucceeded;
        _signalCountsRows = signalCountsRows;
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
    /// The caller cancelled the call: recorded as failed with <paramref name="error"/>, and the tool rethrows the
    /// cancellation, which reaches the caller unchanged once the record is written.
    /// </summary>
    public void FailCancelled(string error)
    {
        Fail(error, Tables);
        RethrowsRecordedFailure = true;
    }

    /// <summary>
    /// The successful call's answer names the learning signal written for it (ask's <c>signal_id</c>, which
    /// <c>feedback</c> rates). The signal is written after the tool returned, so the answer is built twice: returned now
    /// without an id (what the call answers when no signal was persisted), and rebuilt by
    /// <see cref="McpAuditCallToolFilter"/> with the persisted signal's id.
    /// </summary>
    public CallToolResult AnswerWithSignalId(Func<int?, CallToolResult> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        _answerWithSignalId = answer;

        return answer(null);
    }

    /// <summary>The answer rebuilt with the persisted signal's id, or null when the tool's own answer stands.</summary>
    internal CallToolResult? AnswerFor(int? signalId) =>
        signalId != null && ErrorMessage == null ? _answerWithSignalId?.Invoke(signalId) : null;

    /// <summary>
    /// The call threw <paramref name="exception"/>: the audit row keeps its message (redacted there as the project's
    /// retention decides), the signal never its raw text (it can quote row values).
    /// </summary>
    internal void FailWith(Exception exception)
    {
        Fail(
            string.IsNullOrEmpty(exception.Message) ? exception.GetType().Name : exception.Message,
            Tables,
            DatabaseErrorRedactor.ForCaller(exception.Message, RedactionSql, redact: true, logger));
    }

    // The tool is running: the record cannot be written until it returned or threw (McpAuditCallToolFilter).
    internal void BeginInvocation() => _inInvocation = true;

    internal void EndInvocation() => _inInvocation = false;

    /// <summary>
    /// Writes the audit row, then the signal when there is one, once; a second call returns the first one's signal id.
    /// Returns the persisted signal's id, or null. Called by <see cref="McpAuditCallToolFilter"/> after the tool returned
    /// or threw (and for a refusal decided before any tool ran); a call from inside the running tool is refused, so an
    /// early write can never leave a row whose outcome the call later changed. When this scope's audit row was already
    /// written directly by the tool only the signal is written; a direct write that failed is written again. A call not
    /// completed is written as failed.
    /// </summary>
    public async Task<int?> WriteAsync(CancellationToken cancellationToken)
    {
        if (_inInvocation)
        {
            throw new InvalidOperationException("A recorded tool call is written after the tool returns, never by the tool.");
        }

        if (!HasCall || _written)
        {
            return _signalId;
        }

        _written = true;
        if (!IsCompleted)
        {
            Fail(McpAuditCallToolFilter.IncompleteCallMessage);
        }

        if (outcome.Written)
        {
            logger.LogWarning(
                "MCP tool {Tool} wrote its audit row directly; the recorded call is not written a second time",
                McpAuditService.BoundedToolName(Tool));
        }
        else
        {
            await outcome.WriteAsync(() => auditService.LogToolCallAsync(
                UserId,
                Tool!,
                Parameters,
                DataSourceId,
                ProjectId,
                ExecutionTimeMs,
                ResultRowCount,
                ErrorMessage,
                Tables,
                cancellationToken));
        }

        if (_signal == null)
        {
            return null;
        }

        if (_signalError != null)
        {
            _signal.SetExecutionFailed(_signalError);
        }

        _signal.SetResult(_signalCountsRows ? ResultRowCount : null, ExecutionTimeMs, _signalSucceeded);
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
