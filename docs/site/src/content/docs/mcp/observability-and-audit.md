---
title: MCP observability and audit
description: Every MCP tool call is auditable, correlated to a W3C trace, and optionally exported through OpenTelemetry, without Beacon choosing your telemetry backend.
---

Every tool call through the [MCP server](/mcp/server/) (the built-in tools, saved-query tools and
[host endpoint tools](/mcp/host-endpoint-tools/)) writes one `McpAuditLog` row and emits one structured log
event. That includes calls that fail: a tool's own validation errors (an unresolvable `project_id`, a bad `format`,
an `offset` out of range), project-access refusals, calls refused for a missing `Execute` scope (audited by the scope
filter, before any tool runs) and calls whose project access could not be determined. An unknown `q_<name>` is
audited by the saved-query tools. A tool name that belongs to no tool family is answered as a JSON-RPC error
(`Unknown tool`) and writes no row.

With `Beacon:Mcp:Audit:Required = true`, a result whose row could not be written, or a successful result that
produced no row, is withheld rather than returned; see [Fail-closed audit](#fail-closed-audit). Beacon never picks
an exporter or a backend itself: it tags the MCP SDK's own span and records its own metrics, and a host wires those
into whatever it already uses (OTLP to an ADOT collector, Phoenix, or another backend).

## Configuration

```jsonc
{
  "Beacon": {
    "Mcp": {
      "Audit": {
        "Required": false,               // fail closed: withhold a result whose audit row could not be written
        "RetentionDays": null,           // null or 0 = keep forever (default); 365 recommended; > 0 = purge older rows daily
        "RequestIdHeader": "X-Request-Id", // upstream correlation header; null/empty = read none
        "RequireContent": false          // pin content retention on, so audit content cannot be switched off at runtime
      }
    },
    "Telemetry": {
      "CaptureContent": false            // put the tool input on the current span: double opt-in, see below
    }
  }
}
```

| Setting | Default | Meaning |
|---|---|---|
| `Beacon:Mcp:Audit:Required` | `false` | When `true`, a tool result whose audit row failed to write is replaced with an error instead of being returned. While it is `false` the host logs a Warning at startup: such a call is then recorded only by the `Beacon.Audit` 9100 event (`Persisted=false`) and the audit write failure metric. |
| `Beacon:Mcp:Audit:RetentionDays` | `null` | Days audit rows are kept. `null` or `0` keeps them forever (`365` recommended); a negative value fails options validation at startup. Under `Beacon:Retention:Locked` it must be greater than 0. |
| `Beacon:Mcp:Audit:RequestIdHeader` | `X-Request-Id` | The upstream header read into `UpstreamRequestId`. Must be a valid HTTP header token, or startup fails. Set to `null`/empty to read none. |
| `Beacon:Mcp:Audit:RequireContent` | `false` | When `true`, pins `RetainQueryContent` on globally and for every project, so nobody, an Admin included, can switch the audit's question and SQL text off at runtime. See [Keeping audit content](#keeping-audit-content-the-requirecontent-lock). |
| `Beacon:Telemetry:CaptureContent` | `false` | Puts the tool input on the current span as `beacon.tool.input`, only where the project's own content retention also allows content (double opt-in, never from this setting alone). |

## Knowing the retention jobs run

Beacon schedules no maintenance job itself: the audit purges and the security audit verification run only where the host
schedules them (the sample host does, with Warp recurring jobs `mcp-audit-retention`, `security-audit-retention` and
`security-audit-verify`). Each of those services records a heartbeat (`MaintenanceHeartbeats`, one row per job: its name and
the time it last ran to its end) when it finishes. At startup the host looks the heartbeats up, bounded and best effort, and
logs a Warning for each job that should be running and has no recorded run, or none within 36 hours (6 for the
verification): a purge counts only while its `RetentionDays` is set. A first start has no heartbeat yet and warns once.
`IRetentionScheduleCheck` runs the same check for a host that wants it on a schedule of its own.

## What's on an audit row

`McpAuditLog` (one row per tool call) carries, beyond the tool name, timing, result row count and (redacted, per the
project's retention decision) parameters/error:

| Column | Source |
|---|---|
| `CorrelationId` | Minted by Beacon for every audited call (32 hex characters) and never read from a request: the one identifier on the row, and on the 9100 event, that a caller cannot choose. `null` on rows written before it existed. |
| `TraceId` / `SpanId` | `Activity.Current` (the MCP SDK's `tools/call` span) when one is current; otherwise `null`. |
| `McpSessionId` | The `Mcp-Session-Id` transport header. `null` for stateless clients. |
| `UpstreamRequestId` | Supplied by the caller and not verified: use `CorrelationId` as the identifier Beacon vouches for. The header named by `Beacon:Mcp:Audit:RequestIdHeader`, accepted only when it is at most 128 characters of `[A-Za-z0-9._:-]`; anything else is dropped, which blocks log injection. |
| `ApiKeyId` | The `api_key_id` claim of an API-key caller. No foreign key: the audit must outlive a deleted key. |
| `CallerKind` / `CallerHash` | Set for a mapped [Entra ID caller](/mcp/entra-callers/); `null` for API keys and cookie sessions. |
| `ContentHash` | A keyed hash of the call's parameters as received, written **whether or not** the parameters themselves are kept, so calls with the same input can be matched (and a disclosed input checked against the trail) without keeping it. See [Keyed hashes](#keyed-hashes). |
| `DataSourceId`, `ResultRowCount` | For `ask`: the data source and final row count of a single-source answer; `null` for a cross-source answer, whose sources and row counts are on its statements. |

### The statements a call ran

Each audit row also has one `McpAuditStatement` child row per statement the call sent to a data source, in the order
they ran, on the success and the failure path alike, whatever the learning settings. The executors that reach a data
source record them (the query execution service, the cross-source service and the value-grounding probes), and the
pipeline that runs them names why:

| Purpose | Statement |
|---|---|
| `initial` | The first execution of the SQL an `ask` generated. |
| `repair` | An execution of a repaired statement, after an execution error or an empty result. |
| `candidate` | A self-consistency candidate executed to vote on its result. |
| `probe` | A value-grounding probe for a literal of the question. |
| `dry_run` | A validation on the data source without running the statement (`EXPLAIN` and the like), by `dry_run` or before a cross-source step. |
| `source_step` | One data source's statement of a cross-source answer (the in-memory join that combines them reaches no data source and is not a statement). |
| `query` | A statement the `query` tool ran as asked, or one run outside any named purpose. |

A statement row holds its data source, its purpose, `SqlHash` (a keyed hash of the statement text, **always** kept),
`Sql` (the text: **content**, kept only while the call's project retains query content; the content-retention
interceptor strips it otherwise), the tables it names, its row count, its duration and the class of the error it ended
with (never the message; an empty error message counts as no error). Statement rows are deleted with their audit row, so
the retention purge covers them.

A row never reads as the whole of what a call ran when it is not:

- **At most 100 statements a call.** Further ones are counted, not stored: the call's statements then end with a marker
  row of purpose `truncated` (no data source and no text) whose `RowCount` is the number of statements not stored, and a
  Warning names the tool and the count.
- **Table lists.** A statement's table list holds as many names as fit its column; when not all fit, its last entry is
  `[+n more]`. A statement whose tables could not be read from its text has the list `["[unparsed]"]`: unknown, not
  empty. The tables a call's structural parameters name (kept when its content is not) leave these markers out.

### Keyed hashes

`ContentHash` and `SqlHash` are lowercase hex HMAC-SHA256 values under a key derived, for this purpose only (HMAC of a
fixed label), from the secret the MCP caller hashes come from: `Beacon:Mcp:CallerHashKey`, else `Beacon:EncryptionKey`.
An absent input has a hash of its own, apart from the empty text's.
The secret itself is never the key, and nobody without it can test guesses against the stored hashes. The hashes stay
comparable across an encryption-key change while `Beacon:Mcp:CallerHashKey` is set (see
[Replacing the Key](/getting-started/configuration/#replacing-the-key)).

## The `Beacon.Audit` log event catalogue

Every event below is logged at category **`Beacon.Audit`**, structured, with identifiers and counts only, never
parameters, SQL, a question, or a raw error message (only its class, via `McpContentRedactor.ErrorClassOf`). Point
your log sink (CloudWatch, a SIEM) at this category to get the audit trail without reading Beacon's database.

| EventId | Name | Level | When | Notable fields |
|---|---|---|---|---|
| 9100 | `McpToolAudited` | Information | Once per `McpAuditService.LogToolCallAsync` call, including when the row failed to save. | `Tool`, `AuditId` (null if not persisted), `Persisted`, `ProjectId`, `DataSourceId`, `UserId`, `ApiKeyId`, `CallerKind`, `CallerHash`, `DurationMs`, `Rows`, `ErrorClass`, `CorrelationId`, `TraceId`, `McpSessionId`, `UpstreamRequestId` |
| 9101 | `McpToolResultWithheld` | Warning | `Beacon:Mcp:Audit:Required = true` and the audit write for that call failed, or a successful result was not audited (MCP transport and Playground/REST tool runs). | `Tool`, `TraceId` |
| 9102 | `McpAuditPurged` | Information | The retention purge runs with `RetentionDays` set. | Deleted count, cutoff |
| 9103 | `McpAuditRead` | Information | `GET /beacon/api/mcp/audit` is called. | `RequestedByUserId`, `From`, `To`, `ProjectId`, `HasToolFilter` (bool: the admin-supplied `Tool` filter is free text, so only its presence is logged, never the value), `UserId`, `HasCallerHashFilter` (bool, same reasoning), `Page`, `PageSize`, `ReturnedCount` |

The same category carries the [security audit trail](/security/audit-trail/#shipping-to-a-siem)'s events 9104 to 9110: a copy of every committed security audit event, reads and verifications of the trail, purges, write and verification failures, and the checkpoints of its scheduled verification.

### Row data sent to the LLM provider

Prompts that carry data read from a data source are logged at category
`Beacon.AI.Services.LlmProviders.LlmRowDataEgress`, event **7123** `RowDataSentToLlm`, identifiers only, never a value
or prompt text, and no table is written:

| Level | When | Message fields |
|---|---|---|
| Information | A request carrying row data is handed to the LLM provider (written before the provider answers, since the data may leave even when it then fails). | `Categories`, `DataSourceIds`, `ProjectId`, `CallerId` |
| Warning | The provider then fails that request. | the same, plus `Failure`: the exception's type and error code, never its message |
| Information | A `get_query_context` response returns sample values or value matches to the MCP client. | `Categories`, `DataSourceIds`, `ProjectId`, `CallerId` |

The categories are `sample-values`, `value-matches`, `column-values` (documentation enum discovery), `result-rows`
(the eval judge), `database-errors` (lesson extraction) and `stored-documentation` (business-domain, data-model and
glossary sections, which can list discovered values). `CallerId` is the caller's Beacon user id; background work names
itself: `learning-aggregation`, `documentation-indexing`, `pattern-replay`, an eval run's starting user or `eval-run`,
documentation generation's requesting user; and a caller that cannot be resolved is `unknown` (the lookup failure is
logged as a Warning by type, and never fails the request). See
[What reaches the LLM provider](/mcp/server/#what-reaches-the-llm-provider).

## Span attributes and metrics

The MCP SDK already opens one span per `tools/call` (activity source `Experimental.ModelContextProtocol`, tagged
`gen_ai.tool.name`, `mcp.method.name`, `mcp.session.id`, `error.type`, `jsonrpc.request.id`) and records
`mcp.server.operation.duration`. Beacon does not open a duplicate span; it tags the SDK's own current activity:

| Tag | Meaning |
|---|---|
| `beacon.project.id`, `beacon.data_source.id` | The resolved project/data source. |
| `beacon.caller.kind`, `beacon.caller.hash` | The mapped caller (Entra callers only). |
| `beacon.result.rows` | Rows returned, when the tool reports a count. |
| `beacon.error.class` | The redacted error class (never the raw message). |
| `beacon.audit.persisted` | Whether the audit row was written. |
| `beacon.tool.input` | The tool input, truncated to 4000 characters. **Double opt-in only**. see below. |

On the `Beacon` meter:

| Instrument | Kind | Tags |
|---|---|---|
| `beacon.mcp.tool.calls` | Counter | `gen_ai.tool.name`, `beacon.outcome` (`success`\|`error`), `error.type`, `beacon.caller.kind` |
| `beacon.mcp.tool.rows` | Histogram | `gen_ai.tool.name` |
| `beacon.mcp.audit.write_failures` | Counter | `gen_ai.tool.name` |
| `beacon.error_redaction.fallbacks` | Counter | none: a database error that could not be [screened](/mcp/server/#database-errors) in time and was replaced as a whole |

Duration is not duplicated on a Beacon instrument; the SDK's own `mcp.server.operation.duration` already covers it.

Tool names can be caller-chosen (an unknown `q_<anything>` saved-query name, for instance). The 9100/9101 `Tool` log
field carries the tool name only when it matches `^[A-Za-z0-9_.:\-]{1,200}$`; anything else is reported as the
constant `<invalid>`. This blocks log injection; logs are not a cardinality problem, so a charset/length bound is
enough there.

The `gen_ai.tool.name` **metric** tag is drawn from a closed set instead: a charset bound alone doesn't fix
cardinality, because a well-formed but caller-chosen name (`q_1`, `q_2`, ... each a distinct saved-query name) would
still open a new time series per name. A built-in tool (the attribute tools in `McpToolCatalog`, plus the fixed
`search_saved_queries` / `run_saved_query` / `search_api` / `call_api` dispatcher names) keeps its own value; an
unknown name starting with `q_` or `api_` (including a write tool's `api_<name>:write` audit name) collapses to the
fixed bucket `q_*` / `api_*`; anything else is `<other>`.
That is a handful of possible values regardless of how many distinct tool names callers invent.

The audit row itself keeps the tool name as before, unredacted.

> The MCP SDK's own meter (also exported by `AddBeaconInstrumentation`) tags its instruments (including
> `mcp.server.operation.duration`) with `gen_ai.tool.name` set to the **raw** caller-chosen name; Beacon does not
> control that tag. A host worried about cardinality on the SDK's own meter can drop or re-bucket that tag with an
> OpenTelemetry [View](https://opentelemetry.io/docs/specs/otel/metrics/sdk/#view) in its `MeterProviderBuilder`.

### LLM completions and ask stages

Beacon opens its own spans on the `Beacon` activity source for the work behind a tool call. **No tag carries
content**: never the prompt, the system prompt, the completion, SQL or the question, whatever
`Beacon:Telemetry:CaptureContent` says. Tags hold names, counts and flags only.

`beacon.llm.completion` (kind Client): one span per LLM completion, opened around the request queue, so it covers the
wait for a slot, the retries and the provider call.

| Tag | Meaning |
|---|---|
| `gen_ai.provider.name` | The configured provider kind (`openai`, `claude`, `azureopenai`, `bedrock`), read from the same configuration snapshot as the provider that serves the call. |
| `gen_ai.request.model` | The configured model (a configuration value), from that same snapshot. |
| `gen_ai.response.model` | The model the provider reports, only when it reads as a model name (at most 128 letters, digits and `. - _ : / @`); absent otherwise. |
| `beacon.llm.operation` | What the completion is for, a fixed name such as `sql.generation`, `sql.self_consistency`, `sql.repair`, `ask.intent`, `ask.routing`, `ask.knowledge_answer`, `doc.indexing`, `learning.lesson`, `documentation.generate`, `actor.think`. Absent when the caller set none. |
| `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens` | Tokens the provider reports. |
| `gen_ai.usage.cache_read.input_tokens`, `gen_ai.usage.cache_creation.input_tokens` | Prompt-cache reads and writes, where the provider reports them (Claude, also on Bedrock); otherwise 0. |
| `beacon.llm.cache_hint` | Whether the request marked any message as a prompt-cache breakpoint. Only the Claude provider acts on it; Bedrock does not request prompt caching yet, and OpenAI and Azure OpenAI cache on their own. |
| `beacon.llm.truncated` | Whether the answer stopped at the token limit. |
| `beacon.outcome` | `success`, `error`, or `cancelled` when the call was cancelled (a cancellation sets no error status and no error type). |
| `error.type`, `beacon.error.root_type` | On an error only: the exception's type name and the innermost exception's type name (a provider wraps its failures, so the root shows the cause). Never a message. The span's status is Error. |

`beacon.ask.<stage>`: one span per stage of an `ask`, tagged `beacon.ask.stage` and counts only. A completion made in
a stage is its child span. A stage that throws is marked like a completion: `beacon.outcome` `cancelled` on a
cancellation, otherwise `error` with error status, `error.type` and `beacon.error.root_type` (a failing self-consistency
vote is marked so although the `ask` goes on with the single candidate).

| Span | Extra tags |
|---|---|
| `beacon.ask.intent` | `beacon.ask.intent` (`DataQuery` or `Knowledge`) |
| `beacon.ask.routing` | `beacon.ask.sources` (data sources considered) |
| `beacon.ask.generation` | `beacon.ask.tables` (distinct tables the candidate reads), `beacon.ask.context_chars` (characters of the schema context sent), `beacon.ask.tables_shown` and `beacon.ask.tables_omitted` (tables the context describes, and tables `Ceilings:AskSchemaContextChars` left out; these three are set before the call, so a failed generation carries them too) |
| `beacon.ask.self_consistency` | `beacon.ask.candidates` (candidates voted on) |
| `beacon.ask.repair` | `beacon.ask.repair.trigger` (`schema`, `lint`, `dry-run`, `execution`, `empty-result`), `beacon.ask.produced_sql` (whether a statement came back) |
| `beacon.ask.execution` | `beacon.ask.statement.purpose` (`initial`, `repair`, `candidate`), `beacon.result.rows`, `beacon.outcome` |

On the `Beacon` meter:

| Instrument | Kind | Tags |
|---|---|---|
| `beacon.llm.calls` | Counter | `gen_ai.provider.name`, `gen_ai.request.model`, `beacon.llm.operation`, `beacon.outcome` (`success`, `error`, `cancelled`), `error.type` (errors only) |
| `beacon.llm.tokens` | Histogram | `gen_ai.provider.name`, `gen_ai.request.model`, `beacon.llm.operation`, `gen_ai.token.type` (`input`, `output`, `cache_read`, `cache_write`); a type with no tokens records nothing |

A completion made outside a request queue's provider (the admin connection test) has no span.

### Content on spans is a double opt-in

`beacon.tool.input` is added to the current span only when **both**:
1. `Beacon:Telemetry:CaptureContent = true`, and
2. the calling project's effective settings retain query content (`ContentRetentionDecision.RetainQueryContent`),
   which a deployment-level `Beacon:Mcp:ForceNoContentRetention` lock always pins off.

Log events and metrics never carry content, regardless of either setting.

## Turning tracing on

Beacon.Api exposes `BeaconTelemetryBuilderExtensions.AddBeaconInstrumentation()` for both `TracerProviderBuilder` and
`MeterProviderBuilder`. It only depends on `OpenTelemetry.Api`; it adds the `"Beacon"` source/meter plus the MCP
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

With no `OTEL_EXPORTER_OTLP_ENDPOINT`, none of this runs: there is no OpenTelemetry registration at all. Everything else is driven by the standard `OTEL_*` environment variables Beacon never reads
itself: `OTEL_SERVICE_NAME`, `OTEL_TRACES_SAMPLER`, `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS`, and
so on. No endpoint, header or credential is ever hardcoded in Beacon.

A host that embeds Beacon (rather than running the sample host) calls `.AddBeaconInstrumentation()` from its own
tracing/metrics configuration the same way, after adding whichever OpenTelemetry SDK and exporter packages it needs.

### Trace continuity into host endpoint dispatch

`HostEndpointDispatcher` runs the dispatched host endpoint under `ExecutionContext.SuppressFlow()` (inside a fresh
`Task.Run`), so the MCP request's ambient execution context, including `Activity.Current`, does not flow into the
synthetic request. Inside the dispatched host code, `Activity.Current` is **`null`**: host `ILogger` scopes built
there carry no `TraceId`/`SpanId`, and an outbound `HttpClient` call the host code makes starts a brand-new trace of
its own rather than a child span of the MCP call.

The only link between the two traces is `HttpContext.TraceIdentifier`. Before suppressing flow, the dispatcher reads
`Activity.Current?.Id` on the still-flowing caller side and carries it in explicitly as the synthetic request's
`TraceIdentifier` (falling back to a synthetic `beacon-mcp-…` id when no activity was current). `Activity.Id` is
W3C traceparent-format, `00-<32 hex trace id>-<16 hex span id>-01`, so the 32-hex segment in the middle of
`HttpContext.TraceIdentifier` is exactly the audit row's `TraceId`. Host code that wants to correlate its own logs
or spans to the MCP call has to read `HttpContext.TraceIdentifier` and parse out that segment; it cannot rely on
`Activity.Current` being set.

## CloudWatch and collectors

**JSON console logs, no OTel needed.** Set `Logging:Console:FormatterName=json` and
`Logging:Console:FormatterOptions:IncludeScopes=true`. ASP.NET Core's `ActivityTrackingOptions` already defaults to
including `TraceId`/`SpanId`/`ParentId`, so every log line (including `Beacon.Audit` events) carries them once
scopes are on. Ship that JSON stream with `awslogs` (or any agent that reads container stdout) straight into
CloudWatch Logs; this path needs no OTel exporter at all.

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
tools and host-endpoint tools alike). The tool's result is replaced with an error, *"Result withheld: the audit
record for this call could not be written. Contact your Beacon administrator."*, and a `Beacon.Audit` 9101 event is
logged when either:

- the audit row for that call failed to write, or
- the tool returned a **successful** result and no audit row was written for it at all.

An error result that was not audited passes through unchanged; it carries no data.
With `Required = false` (the default), results are always returned unchanged regardless of the audit outcome.

This is enforced **after execution**: the underlying query has already run by the time the filter withholds the
result, and the audit and signal writes are never skipped. The guarantee is *no unaudited data leaves Beacon*, not
*no unaudited execution*.

The audit row itself is written synchronously, before the filter answers, never through a queue: a tool keeps its
call on the request's `McpToolCallRecorder` (tool, caller, input, project, data source, rows or error, tables, and
for `ask`, `query` and `dry_run` only, the learning signal), and the filter writes the row and then the signal after
the tool returned or threw, never while it runs, so the row holds the call's final outcome. A tool that throws after
starting its record is recorded as failed and answers the same caller-safe error the tools' own error handling gives
(a database error's text through the database error redactor while PII detection is on); a protocol error is written
first and then answered as the protocol error it is. A tool that wrote its own row is never given a second one (its
signal still is), and one whose own write failed is written again. A tool that throws before any row was written,
including a call to an unknown tool, still gets one (tool, caller and the error, the error's text only when the
call's project is known and its content retention keeps it), its exception unchanged.

The Playground and the "run tool" REST path (`POST /beacon/api/mcp/tools/run`, `RunMcpToolHandler` →
`McpPlaygroundService`) call tools directly, not through the SDK's request pipeline, so they run them through the same
audit path (`McpAuditCallToolFilter.InvokeAsync`) against their own request scope: the same rows, and with
`Required = true` the same withheld error and 9101 event under the same conditions. A playground call refused before
any tool ran is recorded through the same recorder.

## Keeping audit content: the RequireContent lock

Whether the audit keeps question and SQL text follows each project's content retention (`RetainQueryContent`, see
[Content retention](/mcp/server/#content-retention)), which an Admin or SecurityAdmin can switch off on the MCP
Settings page. A deployment that must keep that evidence sets `Beacon:Mcp:Audit:RequireContent: true`: it pins
`RetainQueryContent` on, globally and for every project, like the other
[deployment locks](/getting-started/configuration/#deployment-locks).

- A save that would switch content retention off, globally or as a project override, is refused with `409`
  (`/errors/setting-locked`, naming `Audit:RequireContent`), an Admin's too. The MCP Settings page shows the switch
  disabled with the reason, and `GET /beacon/api/mcp/settings/locks` lists the lock in `locks`
  (`{ "fieldName": "RetainQueryContent", "lockName": "Audit:RequireContent" }`).
- A stored value it overrides (retention switched off before the lock) is kept, has no effect, and is named in the
  startup warning about overridden settings.
- `RequireContent` and `Beacon:Mcp:ForceNoContentRetention` pin the same setting in opposite directions: with both on,
  the host fails to start.

With the lock on, every audited call keeps its question and the SQL of every statement it ran until the audit retention
purges them. Plan the privacy review for that, and set `Beacon:Mcp:Audit:RetentionDays` accordingly. When a project's
retention setting cannot be read while the lock is on, the call keeps its content (logged at Error); without the lock it
is audited structurally, as before.

The audit rows and their statements are **evidence, not sealed records**: they carry keyed hashes of their content
(`ContentHash`, `SqlHash`) but no signature over the row, so an edit made at the database is not detected the way the
[security audit trail](/security/audit-trail/#what-the-trail-does-and-does-not-prove) detects one. Ship the
`Beacon.Audit` events to a store you control for a copy the database cannot change.

## Retention

Audit rows are kept forever by default. Set `Beacon:Mcp:Audit:RetentionDays` to a positive number (`365` is
recommended) to purge rows older than that window. The purge itself is `IMcpAuditRetentionService.PurgeExpiredAsync`
(`src/Beacon.Core/Services/Retention/McpAuditRetentionService.cs`), a no-op while `RetentionDays` is `null` or `0`,
otherwise one `ExecuteDeleteAsync` plus a `Beacon.Audit` 9102 event with the deleted count and cutoff. A row's statements
are deleted with it.

The sample host schedules it as the Warp recurring job `mcp-audit-retention` (`30 3 * * *`,
`PurgeExpiredMcpAuditLogsJob` / `PurgeExpiredMcpAuditLogsJobHandler` in
`Beacon.SampleProject/Warp/Jobs/McpMaintenanceJobs.cs`). A host embedding Beacon schedules this itself, for example
with Warp:

```csharp
await recurringJobPublisher.AddOrUpdateRecurringJob(new PurgeExpiredMcpAuditLogsJob(), "mcp-audit-retention", "30 3 * * *");
```

or by calling `IMcpAuditRetentionService.PurgeExpiredAsync(cancellationToken)` from its own scheduler.

## Reading the audit

`GET /beacon/api/mcp/audit` is a paged JSON export for an Auditor or an Admin (the
[Auditor permission](/security/authorization/#functional-permissions)). Rows come back exactly as stored; any retention
redaction was already applied at write time, so this endpoint never re-reads or reconstructs content. The rows are not
filtered by project membership: an Auditor reads every project's tool calls, including the SQL and question text the
retention settings keep, though the Auditor role grants no other project data. Reading the audit is itself audited:
each read is recorded first in the [security audit trail](/security/audit-trail/) (`audit.mcp_audit_read`, with the
filters' shape and the page; an identical read by the same actor within 60 seconds once), and fails when that record
cannot be written; 9103 (above) follows on the log stream.

| Query parameter | Required | Notes |
|---|---|---|
| `from`, `to` | Yes | UTC. A value without an offset is taken as UTC; a value with an offset is converted to UTC. The range is validated after that normalisation: `to` must not be earlier than `from`, and the span must not exceed 93 days. |
| `projectId`, `tool`, `callerHash`, `userId` | No | Exact-match filters. |
| `page` | No (default `1`) | Must be `≥ 1`. |
| `pageSize` | No (default `100`) | Must be between `1` and `500`. |

Each row carries its `contentHash` and its `statements` (`id`, `purpose`, `dataSourceId`, `rowCount`, `durationMs`,
`errorClass`, `sqlHash`, and `sql`, which is `null` unless the call's project retained query content), in the order they
ran.

Any other caller gets `403`. An invalid range or paging parameter gets `400`. The **MCP Audit** page
(`/beacon/mcp-audit`, shown to Auditors and Admins) reads it, and the generated TypeScript client exposes it as
`getMcpAuditLogs`.
Security-relevant actions other than tool calls (sign-ins, users, keys, settings and the rest) are in the separate
[security audit trail](/security/audit-trail/).
