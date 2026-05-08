# Project-Specific

> §9.x — Patterns unique to Beacon that aren't obvious from reading the code.

## UI rules

§9.1 **NEVER generate fake / seed / demo data in UI pages.** Pages start empty and populate from real data sources. This applies to dashboards, tables, charts, gauge widgets — everything.

§9.2 **React + Vite is the only UI stack.** Blazor / MudBlazor were removed in the Phase 3 cutover. The React app lives at `Beacon.SampleProject/web/`, builds into `Beacon.SampleProject/wwwroot/`, and is served at the root URL by Kestrel.

§9.3 **In-app `<Link>` and `navigate()` paths must NOT include `/app/`** — `BrowserRouter` is mounted at `basename="/"` post-cutover. Write `/projects`, not `/app/projects`.

## LLM provider swapping

§9.4 **LLM provider is runtime-swappable.** `LlmProviderManager` holds the active provider; `DelegatingLlmProvider` proxies calls. Provider can change at runtime via admin settings — do NOT bake provider-specific assumptions (model names, token limits, response shape) into handlers. Read provider capabilities through the abstraction.

## MCP signal/audit

§9.5 **`McpSignalService` records usage signals for the learning loop**, and `McpAuditService` records the audit trail. Both fire on every MCP tool invocation, including the failure path. Never short-circuit either.

## Stale/legacy

§9.6 _(removed — `Beacon.Web/` and `Beacon.UI/` were both deleted in the Phase 3 cutover.)_

§9.7 ⚠️ **`Result<>` legacy in auth + `TemplateValidator`.** `Beacon.Core/Adapters/Shared/TemplateValidator.cs`, `Beacon.Core/Authorization/Providers/*.cs`, and `Beacon.Core/Authentication/Providers/{Hybrid,Jwt}*.cs` still use a `Result<>` shape. New code throws (`InvalidOperationException` / `BeaconException`) per §2.9. Do not propagate `Result<>` to new handlers.

## Error handling

§9.8 **Throw `InvalidOperationException` for business-rule violations** (entity not found, duplicate name, invalid state). Throw a `BeaconException` derivative for domain-specific errors. Do NOT introduce FluentValidation; validate inline in the handler.
