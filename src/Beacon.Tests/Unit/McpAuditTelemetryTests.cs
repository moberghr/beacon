using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Telemetry;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC4 — one <c>Beacon.Audit</c> 9100 event per audited call, identifiers and counts only, also when the save throws.
/// SC5 — the <c>beacon.*</c> attributes land on the current activity; <c>beacon.tool.input</c> only under
/// CaptureContent AND retain-content. SC6 — <c>beacon.mcp.tool.calls</c>, <c>beacon.mcp.tool.rows</c> and
/// <c>beacon.mcp.audit.write_failures</c> are recorded with the expected tags.
/// </summary>
[TestFixture]
[NonParallelizable]
public class McpAuditTelemetryTests
{
    // Const, never the static ActivitySource field: referencing that inside ShouldListenTo re-enters its static ctor.
    private const string SourceName = "Beacon.Tests.McpAuditTelemetry";
    private const string Sql = "SELECT secret_column FROM customers WHERE ssn = '123-45-6789'";
    private const string RawError = "relation \"secret_table\" does not exist";
    private const string SaveFailure = "connection refused while writing SELECT secret_column";
    private const string CallerHash = "c0ffee";

    private static readonly ActivitySource Source = new(SourceName);

    private ActivityListener _activityListener = null!;
    private MeterListener _meterListener = null!;
    private List<Measurement> _measurements = null!;
    private string _tool = null!;

    [SetUp]
    public void SetUp()
    {
        // A per-test tool name isolates this fixture's measurements from any other audit call in the process.
        _tool = "telemetry_test_" + Guid.NewGuid().ToString("N");
        _measurements = [];

        _activityListener = new ActivityListener
        {
            ShouldListenTo = x => x.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_activityListener);

        _meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == BeaconTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var tagMap = new Dictionary<string, object?>();
            foreach (var tag in tags)
            {
                tagMap[tag.Key] = tag.Value;
            }

            lock (_measurements)
            {
                _measurements.Add(new Measurement(instrument.Name, value, tagMap));
            }
        });
        _meterListener.Start();
    }

    [TearDown]
    public void TearDown()
    {
        _meterListener.Dispose();
        _activityListener.Dispose();
        Activity.Current = null;
    }

    [Test]
    public async Task SuccessfulSave_EmitsOneAuditEventWithIdentifiersOnly()
    {
        var run = await RunAsync(retainContent: true, errorMessage: null);

        var auditEvent = run.Logs.AuditEvents().Should().ContainSingle().Subject;
        auditEvent.Level.Should().Be(LogLevel.Information);
        auditEvent.EventId.Id.Should().Be(9100);
        auditEvent.EventId.Name.Should().Be("McpToolAudited");
        auditEvent.State.Keys.Should().BeEquivalentTo(
            "Tool", "AuditId", "Persisted", "ProjectId", "DataSourceId", "UserId", "ApiKeyId", "CallerKind",
            "CallerHash", "DurationMs", "Rows", "ErrorClass", "TraceId", "McpSessionId", "UpstreamRequestId",
            "{OriginalFormat}");
        auditEvent.State["Tool"].Should().Be(_tool);
        auditEvent.State["Persisted"].Should().Be(true);
        auditEvent.State["AuditId"].Should().Be(0, "the stubbed context assigns no key, but a persisted row reports its id");
        auditEvent.State["ProjectId"].Should().Be(42);
        auditEvent.State["DataSourceId"].Should().Be(7);
        auditEvent.State["UserId"].Should().Be(1);
        auditEvent.State["ApiKeyId"].Should().Be(17);
        auditEvent.State["CallerKind"].Should().Be("user");
        auditEvent.State["CallerHash"].Should().Be(CallerHash);
        auditEvent.State["DurationMs"].Should().Be(12);
        auditEvent.State["Rows"].Should().Be(3);
        auditEvent.State["ErrorClass"].Should().BeNull();
        run.Logs.ShouldCarryNoContent();
        run.Outcome.Written.Should().BeTrue();
        run.Outcome.Failed.Should().BeFalse();
        run.Rows.Should().ContainSingle().Which.Parameters.Should().Be(Sql, "retain-content keeps the SQL in the table");
    }

    [Test]
    public async Task FailedSave_StillEmitsTheAuditEventWithPersistedFalse()
    {
        var run = await RunAsync(retainContent: true, errorMessage: RawError, saveThrows: true);

        var auditEvent = run.Logs.AuditEvents().Should().ContainSingle().Subject;
        auditEvent.EventId.Id.Should().Be(9100);
        auditEvent.State["Persisted"].Should().Be(false);
        auditEvent.State["AuditId"].Should().BeNull();
        auditEvent.State["Tool"].Should().Be(_tool);
        auditEvent.State["ErrorClass"].Should().Be(Beacon.Core.Services.Retention.McpContentRedactor.ErrorClassOf(RawError));
        auditEvent.State["ErrorClass"].Should().NotBe(RawError);
        run.Logs.ShouldCarryNoContent();
        run.Outcome.Failed.Should().BeTrue();
        run.Outcome.Written.Should().BeFalse();
    }

    [Test]
    public async Task ErrorCall_LogsOnlyTheErrorClass()
    {
        var run = await RunAsync(retainContent: true, errorMessage: RawError);

        var auditEvent = run.Logs.AuditEvents().Should().ContainSingle().Subject;
        auditEvent.State["ErrorClass"].Should().Be(Beacon.Core.Services.Retention.McpContentRedactor.ErrorClassOf(RawError));
        run.Logs.ShouldCarryNoContent();
    }

    [Test]
    public async Task CurrentActivity_GetsTheBeaconAttributes()
    {
        using var activity = Source.StartActivity("tools/call");
        activity.Should().NotBeNull();

        await RunAsync(retainContent: true, errorMessage: RawError);

        activity!.GetTagItem(BeaconTelemetry.ProjectIdTag).Should().Be(42);
        activity.GetTagItem(BeaconTelemetry.DataSourceIdTag).Should().Be(7);
        activity.GetTagItem(BeaconTelemetry.CallerKindTag).Should().Be("user");
        activity.GetTagItem(BeaconTelemetry.CallerHashTag).Should().Be(CallerHash);
        activity.GetTagItem(BeaconTelemetry.ResultRowsTag).Should().Be(3);
        activity.GetTagItem(BeaconTelemetry.ErrorClassTag).Should().Be(Beacon.Core.Services.Retention.McpContentRedactor.ErrorClassOf(RawError));
        activity.GetTagItem(BeaconTelemetry.AuditPersistedTag).Should().Be(true);
    }

    [Test]
    public async Task FailedSave_TagsAuditPersistedFalse()
    {
        using var activity = Source.StartActivity("tools/call");

        await RunAsync(retainContent: true, errorMessage: null, saveThrows: true);

        activity!.GetTagItem(BeaconTelemetry.AuditPersistedTag).Should().Be(false);
    }

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, false)]
    public async Task ToolInput_IsOnTheSpanOnlyWithCaptureContentAndRetainContent(bool captureContent, bool retainContent, bool expected)
    {
        using var activity = Source.StartActivity("tools/call");

        await RunAsync(retainContent, errorMessage: null, captureContent: captureContent);

        var input = activity!.GetTagItem(BeaconTelemetry.ToolInputTag);
        if (expected)
        {
            input.Should().Be(Sql);
        }
        else
        {
            input.Should().BeNull();
            activity.Tags.Should().NotContain(x => x.Value != null && x.Value.Contains("secret_column"));
        }
    }

    [Test]
    public async Task ToolInput_IsTruncatedTo4000Chars()
    {
        using var activity = Source.StartActivity("tools/call");
        var longSql = "SELECT " + new string('x', 5000);

        await RunAsync(retainContent: true, errorMessage: null, captureContent: true, parameters: longSql);

        activity!.GetTagItem(BeaconTelemetry.ToolInputTag).Should().Be(longSql[..4000]);
    }

    [Test]
    public async Task NoCurrentActivity_StillLogsAndCounts()
    {
        Activity.Current = null;

        var run = await RunAsync(retainContent: true, errorMessage: null);

        run.Logs.AuditEvents().Should().ContainSingle();
        Measurements(BeaconTelemetry.ToolCallsInstrument).Should().ContainSingle();
    }

    [Test]
    public async Task SuccessfulCall_RecordsToolCallAndRows()
    {
        await RunAsync(retainContent: true, errorMessage: null);

        var call = Measurements(BeaconTelemetry.ToolCallsInstrument).Should().ContainSingle().Subject;
        call.Value.Should().Be(1);
        call.Tags.Should().BeEquivalentTo(new Dictionary<string, object?>
        {
            // §F1 — _tool is an unrecognised, non-prefixed name, so it collapses to the closed-set "<other>" bucket.
            [BeaconTelemetry.ToolNameTag] = McpAuditService.OtherToolMetricTag,
            [BeaconTelemetry.OutcomeTag] = BeaconTelemetry.OutcomeSuccess,
            [BeaconTelemetry.CallerKindTag] = "user"
        });

        var rows = Measurements(BeaconTelemetry.ToolRowsInstrument).Should().ContainSingle().Subject;
        rows.Value.Should().Be(3);
        rows.Tags.Should().BeEquivalentTo(new Dictionary<string, object?> { [BeaconTelemetry.ToolNameTag] = McpAuditService.OtherToolMetricTag });
        Measurements(BeaconTelemetry.AuditWriteFailuresInstrument).Should().BeEmpty();
    }

    [Test]
    public async Task ErrorCall_TagsOutcomeErrorAndErrorClass()
    {
        await RunAsync(retainContent: true, errorMessage: RawError);

        var call = Measurements(BeaconTelemetry.ToolCallsInstrument).Should().ContainSingle().Subject;
        call.Tags[BeaconTelemetry.OutcomeTag].Should().Be(BeaconTelemetry.OutcomeError);
        call.Tags[BeaconTelemetry.ErrorTypeTag].Should().Be(Beacon.Core.Services.Retention.McpContentRedactor.ErrorClassOf(RawError));
        call.Tags.Values.Should().NotContain(x => x != null && x.ToString()!.Contains("secret_table"));
    }

    [Test]
    public async Task FailedSave_IncrementsWriteFailures()
    {
        await RunAsync(retainContent: true, errorMessage: null, saveThrows: true);

        var failure = Measurements(BeaconTelemetry.AuditWriteFailuresInstrument).Should().ContainSingle().Subject;
        failure.Value.Should().Be(1);
        failure.Tags.Should().BeEquivalentTo(new Dictionary<string, object?> { [BeaconTelemetry.ToolNameTag] = McpAuditService.OtherToolMetricTag });
        Measurements(BeaconTelemetry.ToolCallsInstrument).Should().ContainSingle("the call is still counted");
    }

    [Test]
    public async Task NoRowCount_RecordsNoRowsMeasurement()
    {
        await RunAsync(retainContent: true, errorMessage: null, resultRowCount: null);

        Measurements(BeaconTelemetry.ToolRowsInstrument).Should().BeEmpty();
    }

    [Test]
    public async Task CallerChosenToolName_IsBoundedInTheLogAndBucketedInTheMetricTag_ButKeptOnTheRow()
    {
        var injected = "q_" + _tool + "\r\nFAKE LOG LINE Tool=admin\n" + new string('a', 10 * 1024);

        var run = await RunAsync(retainContent: true, errorMessage: null, tool: injected);

        var auditEvent = run.Logs.AuditEvents().Should().ContainSingle().Subject;
        auditEvent.State["Tool"].Should().Be(McpAuditService.InvalidToolName);
        auditEvent.Message.Should().NotContain("FAKE LOG LINE").And.NotContain("\n");

        List<Measurement> bucketed;
        lock (_measurements)
        {
            _measurements.Should().NotContain(x => Equals(x.Tags.GetValueOrDefault(BeaconTelemetry.ToolNameTag), injected));
            // The metric tag never sees the raw injected name either: an unknown q_-prefixed name collapses to the
            // fixed "q_*" bucket (§F1), independent of the log bound (which separately marks it "<invalid>").
            bucketed = _measurements
                .Where(x => x.Instrument == BeaconTelemetry.ToolCallsInstrument)
                .Where(x => Equals(x.Tags.GetValueOrDefault(BeaconTelemetry.ToolNameTag), McpAuditService.SavedQueryToolMetricTag))
                .ToList();
        }

        bucketed.Should().NotBeEmpty("the call is still counted, under the closed-set metric bucket");
        run.Rows.Should().ContainSingle().Which.Tool.Should().Be(injected, "the DB row keeps its existing behaviour");
    }

    [Test]
    public async Task UnknownSavedQueryNames_CollapseToOneMetricTagBucket()
    {
        for (var i = 1; i <= 50; i++)
        {
            await RunAsync(retainContent: true, errorMessage: null, tool: $"q_{i}");
        }

        List<Measurement> calls;
        lock (_measurements)
        {
            calls = _measurements.Where(x => x.Instrument == BeaconTelemetry.ToolCallsInstrument).ToList();
        }

        calls.Should().HaveCount(50);
        calls.Select(x => x.Tags.GetValueOrDefault(BeaconTelemetry.ToolNameTag))
            .Distinct()
            .Should().ContainSingle().Which.Should().Be(McpAuditService.SavedQueryToolMetricTag);
    }

    [Test]
    public async Task UnknownHostEndpointNames_CollapseToOneMetricTagBucket()
    {
        for (var i = 1; i <= 10; i++)
        {
            await RunAsync(retainContent: true, errorMessage: null, tool: $"api_{i}");
        }

        List<Measurement> calls;
        lock (_measurements)
        {
            calls = _measurements.Where(x => x.Instrument == BeaconTelemetry.ToolCallsInstrument).ToList();
        }

        calls.Should().HaveCount(10);
        calls.Select(x => x.Tags.GetValueOrDefault(BeaconTelemetry.ToolNameTag))
            .Distinct()
            .Should().ContainSingle().Which.Should().Be(McpAuditService.HostEndpointToolMetricTag);
    }

    [TestCase("get_context")]
    [TestCase("search_saved_queries")]
    [TestCase("run_saved_query")]
    [TestCase("search_api")]
    [TestCase("call_api")]
    public async Task BuiltInToolName_KeepsItsOwnMetricTagValue(string builtInTool)
    {
        await RunAsync(retainContent: true, errorMessage: null, tool: builtInTool);

        List<Measurement> calls;
        lock (_measurements)
        {
            calls = _measurements.Where(x => x.Instrument == BeaconTelemetry.ToolCallsInstrument).ToList();
        }

        calls.Should().ContainSingle().Which.Tags.GetValueOrDefault(BeaconTelemetry.ToolNameTag).Should().Be(builtInTool);
    }

    [TestCase("project_query")]
    [TestCase("q_monthly-revenue.v2:eu")]
    public void BoundedToolName_KeepsWellFormedNames(string tool)
    {
        McpAuditService.BoundedToolName(tool).Should().Be(tool);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("q_bad name")]
    [TestCase("q_bad\nname")]
    public void BoundedToolName_ReplacesMalformedNames(string? tool)
    {
        McpAuditService.BoundedToolName(tool).Should().Be(McpAuditService.InvalidToolName);
    }

    [Test]
    public void BoundedToolName_RejectsNamesOver200Chars()
    {
        McpAuditService.BoundedToolName(new string('a', 200)).Should().Be(new string('a', 200));
        McpAuditService.BoundedToolName(new string('a', 201)).Should().Be(McpAuditService.InvalidToolName);
    }

    // _tool (a guid-suffixed name, e.g. "telemetry_test_...") is never a built-in name nor q_/api_ prefixed, so
    // its metric tag always collapses to the closed-set "<other>" bucket (§F1) — the log/DB row keep the raw name.
    private List<Measurement> Measurements(string instrument)
    {
        lock (_measurements)
        {
            return _measurements
                .Where(x => x.Instrument == instrument)
                .Where(x => Equals(x.Tags.GetValueOrDefault(BeaconTelemetry.ToolNameTag), McpAuditService.OtherToolMetricTag))
                .ToList();
        }
    }

    private async Task<AuditRun> RunAsync(bool retainContent, string? errorMessage, bool saveThrows = false,
        bool captureContent = false, string parameters = Sql, int? resultRowCount = 3, string? tool = null)
    {
        var rows = new List<McpAuditLog>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCapturingContext(rows, saveThrows));

        var logs = new CapturingLoggerProvider();
        using var loggerFactory = new LoggerFactory([logs]);
        var outcome = new McpAuditOutcome();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim(McpCallerClaimTypes.CallerKind, "user"),
                    new Claim(McpCallerClaimTypes.CallerHash, CallerHash),
                    new Claim(McpCallerClaimTypes.ApiKeyId, "17")
                ],
                "ApiKey"))
        };

        var service = new McpAuditService(
            factory.Object,
            SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = retainContent }).Object,
            new HttpContextAccessor { HttpContext = httpContext },
            Options.Create(new McpDeploymentOptions()),
            loggerFactory.CreateLogger<McpAuditService>(),
            outcome,
            Options.Create(new BeaconTelemetryOptions { CaptureContent = captureContent }),
            loggerFactory);

        await service.LogToolCallAsync(
            sessionId: null,
            userId: 1,
            tool: tool ?? _tool,
            parameters: parameters,
            dataSourceId: 7,
            projectId: 42,
            executionTimeMs: 12,
            resultRowCount: resultRowCount,
            errorMessage: errorMessage,
            ct: CancellationToken.None);

        return new AuditRun(logs, outcome, rows);
    }

    private sealed record AuditRun(CapturingLoggerProvider Logs, McpAuditOutcome Outcome, List<McpAuditLog> Rows);

    private sealed record Measurement(string Instrument, long Value, Dictionary<string, object?> Tags);

    private sealed record LogEntry(string Category, LogLevel Level, EventId EventId, Dictionary<string, object?> State, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public List<LogEntry> AuditEvents()
        {
            lock (_entries)
            {
                return _entries
                    .Where(x => x.Category == BeaconTelemetry.AuditLogCategory)
                    .ToList();
            }
        }

        /// <summary>§1.11 — no parameter, SQL, question or raw error text in any Beacon.Audit event.</summary>
        public void ShouldCarryNoContent()
        {
            foreach (var entry in AuditEvents())
            {
                var texts = entry.State
                    .Where(x => x.Key != "{OriginalFormat}")
                    .Select(x => x.Value?.ToString() ?? string.Empty)
                    .Append(entry.Message)
                    .ToList();

                foreach (var forbidden in new[] { "secret_column", "ssn", "123-45-6789", "secret_table", "connection refused" })
                {
                    texts.Should().NotContain(x => x.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"'{forbidden}' is content and must never reach the Beacon.Audit stream");
                }
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(string category, List<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IEnumerable<KeyValuePair<string, object?>> ?? [];
            var entry = new LogEntry(category, logLevel, eventId, values.ToDictionary(x => x.Key, x => x.Value), formatter(state, exception));

            lock (entries)
            {
                entries.Add(entry);
            }
        }
    }

    /// <summary>Captures the audit rows the service adds and optionally fails the save; no database (§4.7).</summary>
    private sealed class AuditCapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditCapturingContext> ContextOptions =
            new DbContextOptionsBuilder<AuditCapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();
        private readonly bool _saveThrows;

        public AuditCapturingContext(List<McpAuditLog> rows, bool saveThrows) : base(ContextOptions, "beacon")
        {
            _saveThrows = saveThrows;
            _set.Setup(x => x.Add(It.IsAny<McpAuditLog>())).Callback<McpAuditLog>(rows.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_set.Object;
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            _saveThrows
                ? throw new InvalidOperationException(SaveFailure)
                : Task.FromResult(0);
    }
}
