---
title: Ship the MCP audit and security events to your SIEM
description: "Send Beacon's MCP audit events and security log events to your log pipeline, turn on OpenTelemetry traces and metrics, set retention and fail-closed audit, and read the MCP audit in the UI."
---

| | |
|---|---|
| **You end up with** | The `Beacon.Audit` log stream and the security log events in your SIEM, traces and metrics in your collector, retention and content locks set, and alerts on the signals that matter |
| **Time** | about 30 minutes |
| **You need** | A running Beacon host ([Integrate Beacon](/integrate/) or the [MCP gateway](/mcp/gateway/)), a log pipeline that reads container stdout (CloudWatch, a collector or a SIEM agent), and for traces an OpenTelemetry collector |
| **Applies to** | Beacon 5.0 · Both. The MCP audit (events 9100 to 9103, the UI page, tracing and metrics) applies to the gateway too. The security audit trail (9104 to 9112) and its page are full Beacon: the gateway serves no security audit API or page |

:::tip[Let your AI do it]
Paste this into Claude Code (or another coding agent) in your Beacon host's repository.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/audit-to-siem.md and follow it step by step in this repository.
Ask me which log pipeline and collector we use, and what retention policy applies. Never commit keys or endpoints
with credentials, never turn off an audit setting, and show me the result of each "Check it works" item.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Fail closed | Withhold a tool result when its audit row was not written (`Audit:Required`), or return it anyway | Fail closed where an unaudited answer is not acceptable |
| MCP audit retention | Keep forever, or purge rows older than N days | Forever (`null`): set it to your policy |
| Audit content | Keep the question and SQL of every call (`RequireContent`), let each project decide, or keep none (`ForceNoContentRetention`) | Each project decides. The two locks cannot both be on |
| Traces and metrics | OTLP to a collector, or none | None: Beacon registers no exporter |

## 1. Turn on structured logs

Beacon writes its audit events as structured log events under the category **`Beacon.Audit`**. JSON console logs carry
the event id, category, level and trace ids with no OpenTelemetry needed:

```json
{
  "Logging": {
    "Console": {
      "FormatterName": "json",
      "FormatterOptions": { "IncludeScopes": true }
    }
  }
}
```

Make sure the log level for the category lets Information through (`"Beacon.Audit": "Information"` under
`Logging:LogLevel`): the copies of committed events are Information, and a filtered category drops them.

Ship the stream with whatever reads your container's stdout, such as the `awslogs` driver into CloudWatch Logs.

## 2. Know what to route

`Beacon.Audit` holds identifiers, counts and digests only: never parameters, SQL, a question or a raw error message.

| EventId | Name | Level | Meaning |
|---|---|---|---|
| 9100 | `McpToolAudited` | Information | One per MCP tool call, with `Tool`, `ProjectId`, `CallerKind`, `CallerHash`, `DurationMs`, `Rows`, `ErrorClass`, `TraceId`; `Persisted` says whether the row was written |
| 9101 | `McpToolResultWithheld` | Warning | A result was withheld because its audit row failed (needs `Audit:Required`) |
| 9102 | `McpAuditPurged` | Information | The MCP audit retention purge ran |
| 9103 | `McpAuditRead` | Information | Someone read the MCP audit |
| 9104 | `SecurityAuditRecorded` | Information | A security audit event was committed (copy of the row; match by `EventGuid`) |
| 9105 | `SecurityAuditRead` | Information | The security trail was read or verified |
| 9106 | `SecurityAuditPurged` | Information or Warning | The security trail's retention purge ran |
| 9107 | `SecurityAuditWriteFailed` | Error | A security event could not be stored |
| 9108 | `SecurityAuditVerificationFailed` | Error | Stored security events failed verification |
| 9109 | `SecurityAuditCheckpoint` | Information | One per scheduled verification run: keep these, they anchor the trail outside the database |
| 9110 | `SecurityAuditPurgeFailed` | Error | The security trail's purge did not run |
| 9111 | `McpAuditVerificationFailed` | Error | Stored MCP audit rows failed verification |
| 9112 | `McpAuditCheckpoint` | Information | One per MCP audit verification run: keep these with the 9109 checkpoints |

The full field list is in [MCP observability and audit](/mcp/observability-and-audit/#the-beaconaudit-log-event-catalogue)
and [Shipping to a SIEM](/security/audit-trail/#shipping-to-a-siem).

The structured security log events 7101 to 7123 (membership and role changes, sessions, break-glass sign-ins, refused
access, row data sent to an LLM, and more) have their own ids: route them too. The list is in the
[security model](/security/#12-audit-and-security-events). Alert at least on 7119 (break-glass), 7113 and 7114 (role
changes, new users), 7115 (write targets) and 7116 (a statement tried to end SQL Server's read-only transaction).

The copy is best-effort: the database is the source of truth, and the 9107 lines and the 9109 checkpoints let you
notice when the two disagree.

## 3. Set retention, content and fail-closed audit

```jsonc
{
  "Beacon": {
    "Mcp": {
      "Audit": {
        "Required": true,          // withhold a result whose audit row was not written
        "RetentionDays": 400,      // purge MCP audit rows older than this; null keeps them forever
        "RequestIdHeader": "X-Request-Id",
        "RequireContent": false    // true pins question and SQL retention on, for everyone
      }
    },
    "Audit": {
      "RetentionDays": 400         // security audit trail; at least 365; unset keeps events forever
    }
  }
}
```

- `Required: true` replaces the result with *Result withheld: the audit record for this call could not be written.*
  and logs 9101. The query has already run by then: the guarantee is that no unaudited data leaves Beacon.
- `RequireContent: true` keeps the question and the SQL of every statement a call ran until the retention purge. Review
  that against your privacy policy first. It contradicts `Beacon:Mcp:ForceNoContentRetention`, and the host does not
  start with both on. The gateway pins no content retention unless you set `RetainToolContent = true`.
- Both purges are jobs you schedule: the MCP purge is `IMcpAuditRetentionService.PurgeExpiredAsync`, the security trail
  has `ISecurityAuditRetentionService.PurgeExpiredAsync` and an hourly `ISecurityAuditVerificationService.VerifyNewEventsAsync`.
  Set `Beacon:Audit:IntegrityKey` from a secret store before you change the encryption key. See
  [Retention](/security/audit-trail/#retention) and [Keys and rotation](/security/audit-trail/#keys-and-rotation).

## 4. Turn on traces and metrics (optional)

Beacon registers no exporter. Reference the OpenTelemetry packages your platform needs and add Beacon's sources:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(x => x.AddService(serviceName: "your-app"))
    .WithTracing(x => x
        .AddAspNetCoreInstrumentation()
        .AddBeaconInstrumentation()      // sources: "Beacon" and "Experimental.ModelContextProtocol"
        .AddOtlpExporter())
    .WithMetrics(x => x
        .AddAspNetCoreInstrumentation()
        .AddBeaconInstrumentation()      // meters: "Beacon" and "Experimental.ModelContextProtocol"
        .AddOtlpExporter());
```

Set `OTEL_EXPORTER_OTLP_ENDPOINT` (and `OTEL_SERVICE_NAME`) in the environment: Beacon reads no endpoint or credential
itself. Beacon tags the MCP SDK's own `tools/call` span with `beacon.project.id`, `beacon.caller.hash`,
`beacon.error.class` and similar, so one MCP call is one trace. Each audit row carries its `TraceId`, which joins the
SIEM, the trace and the row.

Alert on any increment of these counters:

| Counter | Meaning |
|---|---|
| `beacon.mcp.audit.write_failures` | An MCP audit row was not written |
| `beacon.security_audit.write_failures` | A security event was not stored, or a purge did not run |
| `beacon.security_audit.verification_failures` | Stored security events failed verification |

`beacon.mcp.tool.calls` (tagged `gen_ai.tool.name`, `beacon.outcome`) and `beacon.mcp.tool.rows` give usage and error
rates. Tool names on the metric are bucketed (`q_*`, `api_*`) so caller-chosen names cannot create unbounded series.
`beacon.tool.input` (the tool input on the span) stays off unless both `Beacon:Telemetry:CaptureContent` and the
project's content retention allow it.

A collector that receives OTLP and exports to X-Ray and CloudWatch metrics:

```yaml
receivers:
  otlp:
    protocols:
      http:
      grpc:
processors:
  batch:
exporters:
  awsxray:
  awsemf:
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

The collector holds the cloud credentials, never Beacon.

## 5. Read the audit in Beacon

The **MCP Audit** page (`/beacon/mcp-audit`, in the sidebar for people with the Auditor permission) lists tool calls
with time, tool, project, caller, user, duration, rows and outcome. Filter by From and To (UTC), Tool, Project id, User
id and Caller hash. The gateway's admin UI serves it too. Reading the audit is recorded: 9103 and, in full Beacon, a
security audit event.

The same data is available as a paged JSON export for an Auditor or an Admin (sign in as one):

```text wrap
GET https://beacon.example.com/beacon/api/mcp/audit?from=2026-10-01T00:00:00Z&to=2026-10-21T00:00:00Z&pageSize=100
```

`from` and `to` are required and at most 93 days apart; `pageSize` is 1 to 500. Rows carry `contentHash` and their
`statements`, with `sql` only where the project retained content. Full Beacon also has a **Security Audit** page for
the security trail.

## Check it works

- [ ] Make one MCP tool call. Your SIEM shows a `Beacon.Audit` event 9100 for it, and its `TraceId` matches the **MCP
      Audit** row.
- [ ] In full Beacon, change a project member. A 9104 event arrives with an `EventGuid` that matches the Security Audit
      row.
- [ ] With a collector configured, a tool call shows one trace tagged with `beacon.project.id`.
- [ ] With `Audit:Required` on, a tool result is never returned without its row; a failure shows as 9101 and an increment of `beacon.mcp.audit.write_failures`.
- [ ] The retention jobs run: 9102 (and 9106 in full Beacon) appear after the purge.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| No `Beacon.Audit` events in the SIEM | The category is filtered below Information, or the log driver does not ship stdout. Check `Logging:LogLevel` for `Beacon.Audit`. |
| 9100 events but no 9104 | 9104 is full Beacon only and best-effort: it is missing when the category is filtered or the process stops between commit and log. |
| Host fails to start with an options error | `RetentionDays` must be greater than 0 (security trail: 365 to 36500), `RequestIdHeader` must be a valid header token, and `RequireContent` cannot be on with `ForceNoContentRetention`. |
| `UpstreamRequestId` is empty | The header is absent, longer than 128 characters, or has characters outside `[A-Za-z0-9._:-]`. |
| No traces | `AddBeaconInstrumentation()` is not on the tracer provider, or `OTEL_EXPORTER_OTLP_ENDPOINT` is unset. |
| Logs in the dispatched endpoint carry no trace id | Host endpoint dispatch does not flow `Activity.Current`. See [Trace continuity](/mcp/observability-and-audit/#trace-continuity-into-host-endpoint-dispatch). |
| The audit page is missing from the sidebar | The user lacks the Auditor permission. |

## Next

- [MCP observability and audit](/mcp/observability-and-audit/): every field, tag and instrument.
- [Security audit trail](/security/audit-trail/): the event catalogue, integrity and verification.
- [Production checklist](/integrate/production/#audit-and-observability): what to set before real users.
