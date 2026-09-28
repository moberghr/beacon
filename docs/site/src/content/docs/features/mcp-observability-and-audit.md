---
title: MCP observability and audit
description: Every MCP tool call is auditable, correlated to a W3C trace, and optionally exported through OpenTelemetry — without Beacon choosing your telemetry backend.
---

Every call through the [MCP server](/features/mcp-server/) that reaches execution — direct tool calls, saved-query
tools, and [host endpoint tools](/features/host-endpoint-tools/) — writes one `McpAuditLog` row and emits one
structured log event, visible (when the host opts in) on your own tracing backend. Some calls write none: a request
that fails an early validation check (an unresolvable `project_id`, a bad `format`) returns before the tool ever
calls `McpAuditService.LogToolCallAsync`, so no row and no 9100 event are produced for it. With
`Beacon:Mcp:Audit:Required = true`, a **successful** result for which no row was written is withheld rather than
returned instead of leaving a silent gap in the trail — see "Fail-closed audit" below. Beacon never picks an
exporter or a backend itself; it only tags the MCP SDK's own span and records its own metrics, and a host wires
those into whatever it already uses (OTLP → an ADOT collector, Phoenix, or your own backend).

## Configuration

```jsonc
{
  "Beacon": {
    "Mcp": {
      "Audit": {
        "Required": false,               // fail closed: withhold a result whose audit row could not be written
        "RetentionDays": null,           // null = keep forever (default); > 0 = purge older rows daily
        "RequestIdHeader": "X-Request-Id" // upstream correlation header; null/empty = read none
      }
    },
    "Telemetry": {
      "CaptureContent": false            // put the tool input on the current span — double opt-in, see below
    }
  }
}
```

| Setting | Default | Meaning |
|---|---|---|
| `Beacon:Mcp:Audit:Required` | `false` | When `true`, a tool result whose audit row failed to write is replaced with an error instead of being returned. |
| `Beacon:Mcp:Audit:RetentionDays` | `null` | Days audit rows are kept. `null` keeps them forever (today's behaviour). Must be `> 0` when set — `0` or negative fails options validation at startup. |
| `Beacon:Mcp:Audit:RequestIdHeader` | `X-Request-Id` | The upstream header read into `UpstreamRequestId`. Must be a valid HTTP header token, or startup fails. Set to `null`/empty to read none. |
| `Beacon:Telemetry:CaptureContent` | `false` | Puts the tool input on the current span as `beacon.tool.input` — only where the project's own content retention also allows content (double opt-in, never from this setting alone). |

## What's on an audit row

`McpAuditLog` (one row per tool call) carries, beyond the tool name, timing, result row count and (redacted, per the
project's retention decision) parameters/error:

| Column | Source |
|---|---|
| `TraceId` / `SpanId` | `Activity.Current` (the MCP SDK's `tools/call` span) when one is current; otherwise `null`. |
| `McpSessionId` | The `Mcp-Session-Id` transport header. `null` for stateless clients. |
| `UpstreamRequestId` | The header named by `Beacon:Mcp:Audit:RequestIdHeader`, accepted only when it is at most 128 characters of `[A-Za-z0-9._:-]` — anything else is dropped, which blocks log injection. |
| `ApiKeyId` | The `api_key_id` claim of an API-key caller. No foreign key: the audit must outlive a deleted key. |
| `CallerKind` / `CallerHash` | Set for a mapped [Entra ID caller](/features/mcp-entra-callers/); `null` for API keys and cookie sessions. |

## The `Beacon.Audit` log event catalogue

Every event below is logged at category **`Beacon.Audit`**, structured, with identifiers and counts only — never
parameters, SQL, a question, or a raw error message (only its class, via `McpContentRedactor.ErrorClassOf`). Point
your log sink (CloudWatch, a SIEM) at this category to get the audit trail without reading Beacon's database.

| EventId | Name | Level | When | Notable fields |
|---|---|---|---|---|
| 9100 | `McpToolAudited` | Information | Once per `McpAuditService.LogToolCallAsync` call, including when the row failed to save. | `Tool`, `AuditId` (null if not persisted), `Persisted`, `ProjectId`, `DataSourceId`, `UserId`, `ApiKeyId`, `CallerKind`, `CallerHash`, `DurationMs`, `Rows`, `ErrorClass`, `TraceId`, `McpSessionId`, `UpstreamRequestId` |
| 9101 | `McpToolResultWithheld` | Warning | `Beacon:Mcp:Audit:Required = true` and the audit write for that call failed, or a successful result was not audited (MCP transport and Playground/REST tool runs). | `Tool`, `TraceId` |
| 9102 | `McpAuditPurged` | Information | The retention purge runs with `RetentionDays` set. | Deleted count, cutoff |
| 9103 | `McpAuditRead` | Information | `GET /beacon/api/mcp/audit` is called. | `RequestedByUserId`, `From`, `To`, `ProjectId`, `HasToolFilter` (bool — the admin-supplied `Tool` filter is free text, so only its presence is logged, never the value), `UserId`, `HasCallerHashFilter` (bool, same reasoning), `Page`, `PageSize`, `ReturnedCount` |

## Span attributes and metrics

The MCP SDK already opens one span per `tools/call` (activity source `Experimental.ModelContextProtocol`, tagged
`gen_ai.tool.name`, `mcp.method.name`, `mcp.session.id`, `error.type`, `jsonrpc.request.id`) and records
`mcp.server.operation.duration`. Beacon does not open a duplicate span — it tags the SDK's own current activity:

| Tag | Meaning |
|---|---|
| `beacon.project.id`, `beacon.data_source.id` | The resolved project/data source. |
| `beacon.caller.kind`, `beacon.caller.hash` | The mapped caller (Entra callers only). |
| `beacon.result.rows` | Rows returned, when the tool reports a count. |
| `beacon.error.class` | The redacted error class (never the raw message). |
| `beacon.audit.persisted` | Whether the audit row was written. |
| `beacon.tool.input` | The tool input, truncated to 4000 characters. **Double opt-in only** — see below. |

On the `Beacon` meter:

| Instrument | Kind | Tags |
|---|---|---|
| `beacon.mcp.tool.calls` | Counter | `gen_ai.tool.name`, `beacon.outcome` (`success`\|`error`), `error.type`, `beacon.caller.kind` |
| `beacon.mcp.tool.rows` | Histogram | `gen_ai.tool.name` |
| `beacon.mcp.audit.write_failures` | Counter | `gen_ai.tool.name` |

Duration is not duplicated on a Beacon instrument — the SDK's own `mcp.server.operation.duration` already covers it.

Tool names can be caller-chosen (an unknown `q_<anything>` saved-query name, for instance). The 9100/9101 `Tool` log
field carries the tool name only when it matches `^[A-Za-z0-9_.:\-]{1,200}$`; anything else is reported as the
constant `<invalid>`. This blocks log injection — logs are not a cardinality problem, so a charset/length bound is
enough there.

The `gen_ai.tool.name` **metric** tag is drawn from a closed set instead: a charset bound alone doesn't fix
cardinality, because a well-formed but caller-chosen name (`q_1`, `q_2`, ... each a distinct saved-query name) would
still open a new time series per name. A built-in tool — the attribute tools in `McpToolCatalog`, plus the fixed
`search_saved_queries` / `run_saved_query` / `search_api` / `call_api` dispatcher names — keeps its own value; an
unknown name starting with `q_` or `api_` collapses to the fixed bucket `q_*` / `api_*`; anything else is `<other>`.
That is a handful of possible values regardless of how many distinct tool names callers invent.

The audit row itself keeps the tool name as before, unredacted.

> The MCP SDK's own meter (also exported by `AddBeaconInstrumentation`) tags its instruments — including
> `mcp.server.operation.duration` — with `gen_ai.tool.name` set to the **raw** caller-chosen name; Beacon does not
> control that tag. A host worried about cardinality on the SDK's own meter can drop or re-bucket that tag with an
> OpenTelemetry [View](https://opentelemetry.io/docs/specs/otel/metrics/sdk/#view) in its `MeterProviderBuilder`.

### Content on spans is a double opt-in

`beacon.tool.input` is added to the current span only when **both**:
1. `Beacon:Telemetry:CaptureContent = true`, and
2. the calling project's effective settings retain query content (`ContentRetentionDecision.RetainQueryContent`) —
   which a deployment-level `Beacon:Mcp:ForceNoContentRetention` lock always pins off.

Log events and metrics never carry content, regardless of either setting.

## Turning tracing on

Beacon.Api exposes `BeaconTelemetryBuilderExtensions.AddBeaconInstrumentation()` for both `TracerProviderBuilder` and
`MeterProviderBuilder`. It only depends on `OpenTelemetry.Api` — it adds the `"Beacon"` source/meter plus the MCP
SDK's own `Experimental.ModelContextProtocol` source/meter to whatever provider the host builds. **Beacon never picks
an exporter.** The sample host (`Beacon.SampleProject/Program.cs`) enables OpenTelemetry only when
`OTEL_EXPORTER_OTLP_ENDPOINT` is set:

```csharp
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(x => x.AddService(serviceName: builder.Configuration["OTEL_SERVICE_NAME"] ?? "beacon"))
        .WithTracing(x => x
            .AddAspNetCoreInstrumentation(y => y.Filter = z => !z.Request.Path.StartsWithSegments("/beacon/api/health"))
            .AddHttpClientInstrumentation()
            .AddBeaconInstrumentation()
            .AddOtlpExporter())
        .WithMetrics(x => x
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddBeaconInstrumentation()
            .AddOtlpExporter());
}
```

With no `OTEL_EXPORTER_OTLP_ENDPOINT`, none of this runs and the host behaves exactly as it does today — no OTel
registration at all. Everything else is driven by the standard `OTEL_*` environment variables Beacon never reads
itself: `OTEL_SERVICE_NAME`, `OTEL_TRACES_SAMPLER`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS`, and
so on. No endpoint, header or credential is ever hardcoded in Beacon.

A host that embeds Beacon (rather than running the sample host) calls `.AddBeaconInstrumentation()` from its own
tracing/metrics configuration the same way, after adding whichever OpenTelemetry SDK and exporter packages it needs.

### Trace continuity into host endpoint dispatch

`HostEndpointDispatcher` runs the dispatched host endpoint under `ExecutionContext.SuppressFlow()` (inside a fresh
`Task.Run`), so the MCP request's ambient execution context — including `Activity.Current` — does not flow into the
synthetic request. Inside the dispatched host code, `Activity.Current` is **`null`**: host `ILogger` scopes built
there carry no `TraceId`/`SpanId`, and an outbound `HttpClient` call the host code makes starts a brand-new trace of
its own rather than a child span of the MCP call.

The only link between the two traces is `HttpContext.TraceIdentifier`. Before suppressing flow, the dispatcher reads
`Activity.Current?.Id` on the still-flowing caller side and carries it in explicitly as the synthetic request's
`TraceIdentifier` (falling back to a synthetic `beacon-mcp-…` id when no activity was current). `Activity.Id` is
W3C traceparent-format — `00-<32 hex trace id>-<16 hex span id>-01` — so the 32-hex segment in the middle of
`HttpContext.TraceIdentifier` is exactly the audit row's `TraceId`. Host code that wants to correlate its own logs
or spans to the MCP call has to read `HttpContext.TraceIdentifier` and parse out that segment; it cannot rely on
`Activity.Current` being set.

## CloudWatch and collectors

**JSON console logs, no OTel needed.** Set `Logging:Console:FormatterName=json` and
`Logging:Console:FormatterOptions:IncludeScopes=true`. ASP.NET Core's `ActivityTrackingOptions` already defaults to
including `TraceId`/`SpanId`/`ParentId`, so every log line (including `Beacon.Audit` events) carries them once
scopes are on. Ship that JSON stream with `awslogs` (or any agent that reads container stdout) straight into
CloudWatch Logs — this path needs no OTel exporter at all.

**An ADOT collector**, receiving OTLP from Beacon and exporting to X-Ray/CloudWatch (or to Phoenix for local/dev
tracing), looks like:

```yaml
receivers:
  otlp:
    protocols:
      http:
      grpc:
processors:
  batch:
exporters:
  awsxray:      # or: otlp/phoenix: { endpoint: "http://phoenix:6006/v1/traces" }
  awsemf:       # CloudWatch metrics
service:
  pipelines:
    traces:
      receivers: [otlp]
      processors: [batch]
      exporters: [awsxray]
    metrics:
      receivers: [otlp]
      processors: [batch]
      exporters: [awsemf]
```

Point `OTEL_EXPORTER_OTLP_ENDPOINT` at the collector; the collector holds the AWS credentials or the Phoenix
endpoint, never Beacon.

## Fail-closed audit

With `Beacon:Mcp:Audit:Required = true`, `McpAuditCallToolFilter` wraps every tool call (attribute tools, saved-query
tools and host-endpoint tools alike). The tool's result is replaced with an error — *"Result withheld: the audit
record for this call could not be written. Contact your Beacon administrator."* — and a `Beacon.Audit` 9101 event is
logged when either:

- the audit row for that call failed to write, or
- the tool returned a **successful** result and no audit row was written for it at all.

An error result that was not audited (for example *Unknown tool*) passes through unchanged — it carries no data.
With `Required = false` (the default), results are always returned unchanged regardless of the audit outcome.

This is enforced **after execution**: the underlying query has already run by the time the filter withholds the
result, and the audit and signal writes are never skipped. The guarantee is *no unaudited data leaves Beacon*, not
*no unaudited execution*.

The Playground and the "run tool" REST path (`POST /beacon/api/mcp/tools/run`, `RunMcpToolHandler` →
`McpPlaygroundService`) call tools directly, not through the SDK's request pipeline, so they apply the same decision
(`McpAuditCallToolFilter.ShouldWithhold`) against their own request scope: with `Required = true` they return the same
withheld error, and log the same 9101 event, under the same conditions.

## Retention

Audit rows are kept forever by default. Set `Beacon:Mcp:Audit:RetentionDays` to a positive number to purge rows
older than that window. The purge itself is `IMcpAuditRetentionService.PurgeExpiredAsync`
(`src/Beacon.Core/Services/Retention/McpAuditRetentionService.cs`) — a no-op while `RetentionDays` is `null`,
otherwise one `ExecuteDeleteAsync` plus a `Beacon.Audit` 9102 event with the deleted count and cutoff.

The sample host schedules it as the Warp recurring job `mcp-audit-retention` (`30 3 * * *`,
`PurgeExpiredMcpAuditLogsJob` / `PurgeExpiredMcpAuditLogsJobHandler` in
`Beacon.SampleProject/Warp/Jobs/McpMaintenanceJobs.cs`). A host embedding Beacon schedules this itself — for example
with Warp:

```csharp
await recurringJobPublisher.AddOrUpdateRecurringJob(new PurgeExpiredMcpAuditLogsJob(), "mcp-audit-retention", "30 3 * * *");
```

or by calling `IMcpAuditRetentionService.PurgeExpiredAsync(cancellationToken)` from its own scheduler.

## Reading the audit

`GET /beacon/api/mcp/audit` is an admin-only, paged JSON export. Rows come back exactly as stored — any retention
redaction was already applied at write time, so this endpoint never re-reads or reconstructs content. Reading the
audit is itself audited (9103, above).

| Query parameter | Required | Notes |
|---|---|---|
| `from`, `to` | Yes | UTC. A value without an offset is taken as UTC; a value with an offset is converted to UTC. The range is validated after that normalisation: `to` must not be earlier than `from`, and the span must not exceed 93 days. |
| `projectId`, `tool`, `callerHash`, `userId` | No | Exact-match filters. |
| `page` | No (default `1`) | Must be `≥ 1`. |
| `pageSize` | No (default `100`) | Must be between `1` and `500`. |

A non-admin caller gets `403`. An invalid range or paging parameter gets `400`. There is no UI for this endpoint and
no NSwag client is generated for it — it's an operational export, not a product surface.
