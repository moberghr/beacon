# Architecture

> §2.x — Layers, slices, DI, patterns. Loaded automatically by Claude Code.

## CQRS via MediatR

§2.1 **Every UI action goes through `IMediator.Send()`.** Query/command records and handlers live in `src/Beacon.Core/Handlers/` and `src/Beacon.AI/Handlers/`. Each project registers its own assembly via `services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(...))`.

§2.1.1 **One MediatR handler = one HTTP endpoint.** Every `IRequest` / `IRequest<TResult>` must be reachable via `/beacon/api/*` exposed in `src/Beacon.SampleProject/Endpoints/{Area}Endpoints.cs`. Endpoints stay thin — accept path/body, call `mediator.Send(...)`, return the result. CI's `OpenApiContractTests.EveryMediatRHandlerIsExposedViaHttp` enforces this.

§2.2 **Handler file convention — one slice per file.** The handler class, request record, and result record all live in the same file. Handler is `internal sealed class` using primary-constructor injection.

```csharp
internal sealed class CreateQueryFolderHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<CreateQueryFolderCommand, CreateQueryFolderResult>
{
    public async Task<CreateQueryFolderResult> Handle(CreateQueryFolderCommand request, CancellationToken cancellationToken)
    { ... }
}

public record CreateQueryFolderCommand(...) : IRequest<CreateQueryFolderResult>;
public record CreateQueryFolderResult(...);
```

§2.3 **Naming:** `{Action}{Domain}Command` / `{Action}{Domain}Query` / `{Action}{Domain}Result`.

## Layer rules

§2.4 **All references point toward `Beacon.Core`.** Sibling projects never reference each other horizontally.

```
SampleProject (Host) → AI, MCP, Core, Core.PostgreSql/SqlServer, Connector.*
AI                   → Core
MCP                  → Core, AI
Connector.*          → Core
Core.PostgreSql/Sql  → Core
```

§2.5 _(removed — `Beacon.UI` and `Beacon.Web` were both deleted in the Phase 3 cutover. The host is `Beacon.SampleProject`; the UI is the React app at `src/Beacon.SampleProject/web/`.)_

## What this codebase deliberately does NOT use

§2.6 **No repository pattern.** Handlers and services access `BeaconContext` directly via `IDbContextFactory<BeaconContext>`.

§2.7 **No domain events / MediatR notifications.** Cross-concern communication is direct service calls.

§2.8 **No AutoMapper / Mapster.** All mapping is manual via `.Select()` or inline property assignment.

§2.9 **No Result pattern in new code.** Throw `InvalidOperationException` for business-rule violations; throw `BeaconException` (or a derivative) for domain-specific errors. ⚠️ **Inconsistency:** `src/Beacon.Core/Adapters/Shared/TemplateValidator.cs` and the auth providers (`src/Beacon.Core/Authorization/Providers/*`, `src/Beacon.Core/Authentication/Providers/Hybrid*`, `Jwt*`) still use a `Result<>` shape — leave existing code alone, but do NOT propagate the pattern to new handlers.

## DI conventions

§2.10 **Builder pattern for stack registration.** `BeaconBuilder` enables fluent chaining. Connectors register via `Add{Engine}Connector()` extension methods on `BeaconBuilder`. Do not bypass with raw `services.Add*` for connector wiring.

§2.11 **Connector/Provider pattern.** Data sources implement `IDataSourceProvider` and register with `ConnectorRegistry`. See `src/Beacon.Connector.PostgreSql/ServiceCollectionExtensions.cs` for the canonical example.

§2.12 **DI registration per project.** Each project has `ServiceConfiguration.cs` (Core, AI, MCP) or `ServiceCollectionExtensions.cs` (Connectors, DB providers). NEVER register services inline in `Program.cs`.

§2.13 **DI lifetimes:** Singletons for configuration / provider managers. Scoped for handlers (MediatR default) and user context. Transient for service classes (`TryAddTransient` is the standard in `Beacon.AI`). Use `TryAdd*` so the host can override.

## Soft delete

§2.14 **`ArchivableBaseEntity` gets a global `HasQueryFilter(x => x.ArchivedTime == null)` filter.** Call `.Archive()` instead of deleting. Use `.IgnoreQueryFilters()` ONLY when explicitly working with archived data.

## Background work

§2.15 **All recurring / scheduled work uses Hangfire + PostgreSQL storage.** Recurring jobs are registered in `src/Beacon.SampleProject/Program.cs`. Do NOT use `BackgroundService` or `IHostedService`.
