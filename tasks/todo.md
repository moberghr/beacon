# MCP compliance & observability — Beacon side (2026-09-28)

Spec: `docs/specs/2026-09-28-mcp-telemetry-audit.md` · Plan: `docs/plans/2026-09-28-mcp-telemetry-audit.md`
Rigor: HIGH (4 batches, 38 manifest entries, security_impact=requires-audit-trail)

## B1 — Audit correlation schema
- [x] Tests first: `McpAuditCorrelationTests` (trace/span/session/request/api-key ids; bad request id dropped), `McpAuditOptionsValidationTests`
- [x] `McpAuditLog` + 5 columns; context max lengths + `CreatedTime` index
- [x] `McpAuditOptions` under `Beacon:Mcp:Audit` + validation
- [x] `McpAuditService` fills the columns; fix constructor call sites in fixtures
- [x] Deny list: 4 Structural rules
- [x] PG migration (scaffold) + SQL Server migration (hand-written) + both snapshots
- [x] Checkpoint: build + test

## B2 — Telemetry, log stream, fail-closed
- [x] Tests first: `McpAuditTelemetryTests` (log event, span tags, content matrix, metrics), `McpAuditCallToolFilterTests`
- [x] `BeaconTelemetry` (Core, BCL only) + `BeaconTelemetryOptions`
- [x] `McpAuditOutcome` scoped; audit service sets it
- [x] `Beacon.Audit` 9100 event — ids and counts only
- [x] Span tags on `Activity.Current`; `beacon.tool.input` only with CaptureContent AND retain-content
- [x] `McpAuditCallToolFilter` registered via `AddCallToolFilter`
- [x] Checkpoint: build + test

## B3 — Retention + export
- [x] Tests first: `McpAuditRetentionTests`, `McpAuditQueryTranslationTests`, `GetMcpAuditLogsHandlerTests`, `McpAuditEndpointTests`
- [x] `IMcpAuditRetentionService` + Warp job `mcp-audit-retention` (30 3 * * *)
- [x] `GetMcpAuditLogsHandler` + `GET /beacon/api/mcp/audit` (admin) + 9103 read event
- [x] Checkpoint: build + test

## B4 — Host wiring, continuity, docs
- [x] Package legitimacy: OpenTelemetry.* 1.18.0
- [x] `AddBeaconInstrumentation` (tracer + meter) in Beacon.Api
- [x] Sample host OTel (only when `OTEL_EXPORTER_OTLP_ENDPOINT` set)
- [x] `HostEndpointDispatcher` TraceIdentifier from `Activity.Current`
- [x] Docs: `features/mcp-observability-and-audit.md`
- [x] Tests: `BeaconTelemetryBuilderExtensionsTests`, `HostEndpointDispatcherTraceTests`
- [x] Full build + test

## Review
- [x] Spec drift check
- [x] compliance-reviewer, test-reviewer, architecture-reviewer (3 iterations)
- [x] Fix findings, simplify, lessons
