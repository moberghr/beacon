# Project-Specific

> §9.x — Patterns unique to Beacon that aren't obvious from reading the code.

## UI rules

§9.1 **NEVER generate fake / seed / demo data in UI pages.** Pages start empty and populate from real data sources. This applies to dashboards, tables, charts, gauge widgets — everything.

§9.2 **React + Vite + Tailwind is the UI stack.** Blazor / MudBlazor were removed in the Phase 3 cutover. The React app lives at `src/Beacon.UI/web/`, builds into `src/Beacon.UI/wwwroot/`, and is served at the root URL by Kestrel via the `Beacon.UI` Razor Class Library. Tailwind CSS v3.4 drives the Beacon design system primitives in `src/components/beacon/`; the Tailwind config maps semantic names (`bg-brand-500`, `bg-surface`, `text-text-muted`, `bg-ok-bg`, …) onto CSS variables defined in `src/index.css :root`. Helper classes (`.mono`, `.subtle`, `.eyebrow`, `.eyebrow-sep`, `.eyebrow-pin`, `.kbd`, `.beacon-beam`, `.beacon-rings`, `.beacon-underline`, `.beacon-logo-dot`, `.tok-*`) live in the same file's `@layer components`. Use Beacon primitives (`Button`, `Card`, `Pill`, `KPI/KPIGrid`, `Banner`, `Modal`, `Input/Field`, `Seg`, `Kbd`, `PageHeader`, `BeaconHero`) instead of hand-rolling chrome; icons come from `lucide-react`.

§9.3 **In-app `<Link>` and `navigate()` paths must NOT include `/app/`** — `BrowserRouter` is mounted at `basename="/"` post-cutover. Write `/projects`, not `/app/projects`.

## LLM provider swapping

§9.4 **LLM provider is runtime-swappable.** `LlmProviderManager` holds the active provider; `DelegatingLlmProvider` proxies calls. Provider can change at runtime via admin settings — do NOT bake provider-specific assumptions (model names, token limits, response shape) into handlers. Read provider capabilities through the abstraction.

## MCP signal/audit

§9.5 **`McpSignalService` records usage signals for the learning loop**, and `McpAuditService` records the audit trail. Both fire on every MCP tool invocation, including the failure path. Never short-circuit either.

## Stale/legacy

§9.6 _(removed — `Beacon.Web/` and `src/Beacon.UI/` were both deleted in the Phase 3 cutover.)_

§9.7 ⚠️ **`Result<>` legacy in auth + `TemplateValidator`.** `src/Beacon.Core/Adapters/Shared/TemplateValidator.cs`, `src/Beacon.Core/Authorization/Providers/*.cs`, and `src/Beacon.Core/Authentication/Providers/{Hybrid,Jwt}*.cs` still use a `Result<>` shape. New code throws (`InvalidOperationException` / `BeaconException`) per §2.9. Do not propagate `Result<>` to new handlers.

## Error handling

§9.8 **Throw `InvalidOperationException` for business-rule violations** (entity not found, duplicate name, invalid state). Throw a `BeaconException` derivative for domain-specific errors. Do NOT introduce FluentValidation; validate inline in the handler.
