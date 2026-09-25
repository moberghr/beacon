---
title: Host Endpoint Tools
description: Expose selected read-only endpoints of the host application as MCP tools, invoked in-process as the caller through the endpoint's own model binding, filters and authorization policies.
---

A host application already has endpoints that answer the questions an agent asks — a back-office grid of a
customer's loans, a lookup by id. `AddHostEndpointTools` turns the endpoints you mark into MCP tools. Each call runs
**in-process, through the endpoint's real pipeline** (model binding, action filters, authorization policies), as a
host identity built for the MCP caller — never as Beacon, and never with the caller's own token.

v1 is **read-only only**: an endpoint is exposed only when the host declares it read-only. The HTTP verb is not
trusted, because back offices read over POST (Kendo grids, `[FromForm]` filters).

## Quick start

```csharp
builder.Services.AddBeaconMcp()
    .AddHostEndpointTools(o =>
    {
        o.ProjectName = "Netgiro";                     // the tools belong to this Beacon project
        o.MaxResponseBytes = 1_048_576;                // default 1 MiB
        o.NamedToolLimit = 20;                         // above this: search_api + call_api
        o.RequestTimeout = TimeSpan.FromSeconds(30);   // default 30 s
    });
```

Mark a controller action:

```csharp
[HttpPost]
[Authorize]                                           // required: [Permission] is a filter, not authorization metadata
[Permission(AdminPermission.ViewLoans)]               // Netgiro's filter: runs in the action pipeline (needs a dispatch middleware, below)
[BeaconTool("customer_loans", Description = "Loans of one customer, newest first.", ReadOnly = true)]
public IActionResult CustomerLoans([DataSourceRequest] DataSourceRequest request, int customerId)
{
    // ...
    return Json(loans.ToDataSourceResult(request));
}
```

Or a minimal API endpoint:

```csharp
app.MapGet("/api/loans/{id:int}", (int id, LoanService loans) => loans.Find(id))
    .RequireAuthorization("ViewLoans")
    .WithBeaconTool("loan_by_id", "One loan by id.", readOnly: true);
```

The tool is exposed as `api_customer_loans` (or `api_loan_by_id`).

The endpoint must carry authorization metadata (`[Authorize]` on the action or controller, a policy, or
`[AllowAnonymous]`), or the host must have a fallback authorization policy — otherwise startup refuses it. A
permission check implemented as a filter (like Netgiro's `[Permission]`) is not metadata. Netgiro Web.Admin has
neither `[Authorize]` on its controllers nor a fallback policy, so every exposed action carries `[Authorize]` next to
`[BeaconTool]` and `[Permission]`, as above.

## What gets exposed

- Only endpoints carrying `[BeaconTool]` / `.WithBeaconTool(...)`. Everything else is invisible.
- The name must match `^[a-z][a-z0-9_]{2,47}$` and be unique. `ReadOnly = true` is mandatory: a missing or `false`
  flag **fails startup** with a message naming the endpoint. So do bad or duplicate names, file-upload parameters,
  `[AsParameters]`, and an endpoint with no authorization at all (see below).
- Endpoints are discovered when the host's pipeline is built (they do not exist at DI time), from the
  `EndpointDataSource`. Parameters come from **ApiExplorer** when it describes the endpoint (`[ApiController]`
  controllers, or `AddEndpointsApiExplorer()` for minimal APIs), otherwise from the action's parameters and binding
  attributes. Swagger / Swashbuckle is never needed.
- **Description**: the attribute's `Description`, else the action's XML doc `<summary>` (enable
  `GenerateDocumentationFile`; `<param>` docs describe the arguments), else the endpoint's summary/description
  metadata, else a generic "Calls POST /route" line. Every description ends with the method and route.

### Arguments

| Bound from | Tool argument |
| --- | --- |
| Route | one argument per route value, required unless optional in the template; URL-encoded into the path |
| Query / form, simple types | one argument per value; collections as arrays (sent as repeated keys) |
| Query / form, a model (`[FromForm] LoanFilter filter`, or an unannotated model on a plain MVC controller) | flattened: one argument per property (`CustomerId`, `Period.From`, up to 3 levels); collections of objects are skipped |
| `[FromBody]` | one argument named after the parameter, its schema from `System.Text.Json`'s `JsonSchemaExporter` |
| `[FromHeader]` | one argument named after the header. `Authorization`, `Cookie`, `Host` and content headers are refused |
| A custom model binder (Kendo's `[DataSourceRequest]`) | one object argument whose members are sent as raw fields: `{ "page": 1, "pageSize": 20, "sort": "CreatedDate-desc" }` |
| `CancellationToken`, `HttpContext`, `[FromServices]`, DI-injected minimal API parameters | not arguments |

Unknown and missing required arguments are rejected before anything is dispatched.

### Named tools or a catalog

Up to `NamedToolLimit` tools, each is its own MCP tool. Above it, agents get two tools instead, so a large host does
not flood the tool list: `search_api` (keywords → matching tools with their input schemas) and `call_api` (a name
plus arguments). All of them carry `readOnlyHint = true`, `destructiveHint = false`, `openWorldHint = false`. The
eight built-in tools are unaffected.

## Who the call runs as

Endpoint tools need a **mapped MCP caller** — an Entra user or system identity from
[Entra ID callers](/features/mcp-entra-callers/) — authorized for the tools' project. API-key callers and callers
outside the project neither see nor can call them (fail closed); neither can anyone while the project does not exist.

`IMcpHostPrincipalFactory` builds the host principal from the caller:

- **System caller** → identity `BeaconMcpSystem` with the caller's configured `HostClaims` plus its name.
- **User caller** → identity `BeaconMcpUser` with name, email, preferred username and object id copied from the
  MCP principal, plus any `HostClaims` the caller mapper assigned (none with the configured mapper). The default
  gives a user **no host permissions**, and deliberately does **not** copy Entra `roles` or `groups`: a host role
  that happens to share a name (`Admin`) would otherwise be granted implicitly. Map users to host permissions in
  your own factory.
- `null` → the call is refused.

A host whose permissions live in its own user store registers its own factory. Netgiro maps the Entra user to its
admin user and `AdminPermission` set:

```csharp
builder.Services.AddSingleton<IMcpHostPrincipalFactory, NetgiroMcpHostPrincipalFactory>();

internal sealed class NetgiroMcpHostPrincipalFactory(IServiceScopeFactory scopes) : IMcpHostPrincipalFactory
{
    public async Task<ClaimsPrincipal?> CreateAsync(McpCaller caller, ClaimsPrincipal mcpPrincipal, CancellationToken ct)
    {
        if (caller.Kind == McpCallerKind.System)
        {
            // HostClaims come from Beacon:Mcp:Callers:Systems[*]:HostClaims — keep the set minimal.
            return new ClaimsPrincipal(new ClaimsIdentity(caller.HostClaims, "BeaconMcpSystem"));
        }

        var email = mcpPrincipal.FindFirst(ClaimTypes.Email)?.Value;
        await using var scope = scopes.CreateAsyncScope();
        var admins = scope.ServiceProvider.GetRequiredService<AdminUserService>();
        var admin = email == null ? null : await admins.FindActiveByEmailAsync(email, ct);
        if (admin == null)
        {
            return null;                               // not an admin: refused
        }

        var claims = admin.Permissions
            .Select(x => new Claim(AdminClaimTypes.Permission, x.ToString()))
            .Append(new Claim(ClaimTypes.Name, admin.UserName));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "BeaconMcpUser"));
    }
}
```

System identity host claims, in configuration:

```json
"Beacon": { "Mcp": { "Callers": { "Systems": [ {
  "Name": "kvika-routines",
  "AppId": "…",
  "ProjectIds": [ 3 ],
  "HostClaims": [ { "Type": "permission", "Value": "ViewLoans" } ]
} ] } } }
```

## How a call is dispatched

1. The caller, project and arguments are checked; the host principal is built.
2. A **new DI scope** and a new `HttpContext` are created. The request carries the method, the route filled from the
   endpoint's route pattern, the query string, the declared headers only, and a JSON or
   `application/x-www-form-urlencoded` body. **Nothing of the MCP request is forwarded**: no `Authorization` header,
   no cookies, no connection info. It also sends `Accept: application/json` and `X-Requested-With: XMLHttpRequest`.
3. The endpoint and route values are set as routing would set them.
4. **Authorization** runs before the endpoint, exactly as the authorization middleware would, against the host
   principal: the endpoint's `[Authorize]` / policy / requirement metadata combined through the host's
   `IAuthorizationPolicyProvider`, evaluated with `IPolicyEvaluator`. `[AllowAnonymous]` is honoured. An endpoint
   without authorization metadata falls back to the host's `FallbackPolicy`; with no fallback policy either it is
   refused at startup (and at dispatch). A denial is the tool error `Forbidden.` with no detail. An endpoint marked
   `[AllowAnonymous]` is exposable and runs without an authorization check: the host itself declared it public, so
   exposing it to an authorized MCP caller grants nothing new.
5. The registered `IHostEndpointDispatchMiddleware` chain runs, in registration order, around the endpoint (see below).
6. The endpoint's `RequestDelegate` runs — for MVC that is the whole action pipeline: model binding, validation,
   authorization/action/exception filters, the result.
7. The response is captured into a buffer capped at `MaxResponseBytes`; the request is aborted after
   `RequestTimeout` or when the MCP call is cancelled.

Host code that reads `IHttpContextAccessor` sees the dispatched request. The dispatch runs in its own execution
context, so the MCP request's context (and the audit identity) is untouched.

### Host middleware and ambient state: dispatch middleware

Host **middleware does not run** on dispatch — only the endpoint's own pipeline. When the endpoint depends on state a
middleware sets, register an `IHostEndpointDispatchMiddleware`:

```csharp
Task InvokeAsync(HttpContext syntheticContext, McpCaller caller, Func<Task> next, CancellationToken cancellationToken);
```

Registered middlewares are chained in registration order, with the endpoint's request delegate as the innermost
`next`. The chain starts after `User`, `RequestServices`, the endpoint and route values are set and after Beacon's
authorization check. As in ASP.NET Core middleware, ambient state set before `await next()`
(`Thread.CurrentPrincipal`, an `AsyncLocal`) is visible to the endpoint and its filters — async work before it is
fine. A middleware that does not call `next` refuses the call; one that throws fails it. Both return a generic tool
error and are audited. The dispatch runs in its own execution context, so the MCP request's own ambient principal is
never changed.

Netgiro's `[Permission(AdminPermission.X)]` is an `IAsyncAuthorizationFilter` that checks
`Identity.Current.HasPermission(...)`, and `Identity.Current` reads `Thread.CurrentPrincipal`, which Web.Admin's
middleware sets with `Identity.Set(principal)`. Without a dispatch middleware every tool call would be denied. The
factory above maps the Entra user to admin permissions; the middleware makes that principal ambient:

```csharp
builder.Services.AddSingleton<IHostEndpointDispatchMiddleware, NetgiroIdentityMiddleware>();

internal sealed class NetgiroIdentityMiddleware : IHostEndpointDispatchMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, McpCaller caller, Func<Task> next, CancellationToken cancellationToken)
    {
        Identity.Set(ctx.User);                        // the principal IMcpHostPrincipalFactory built
        await next();
    }
}
```

### Antiforgery

Beacon **mints a real antiforgery token pair** for the host principal with the host's own `IAntiforgery` and puts it
on the dispatched request (the cookie token as a cookie, the request token in the configured header, or the form
field when the header is disabled). The host's own validation — `[ValidateAntiForgeryToken]`, a global
`AutoValidateAntiforgeryTokenAttribute`, minimal API form endpoints — therefore runs and passes. For minimal API
endpoints that require validation, Beacon validates the request the way `UseAntiforgery()` does and records the
result. Nothing is exempted and real browser requests are validated exactly as before: the tokens are minted
server-side and never leave the process (CSRF cannot apply to a request that never came from a browser). A host
with a custom anti-CSRF scheme (a bespoke header check, an `IAntiforgeryAdditionalDataProvider` that inspects the
request) must make sure it accepts these requests.

### Responses

- 2xx JSON → the JSON as text plus `structuredContent`. MCP structured content must be an object, so a top-level
  JSON array is wrapped as `{ "items": [...] }` (the text content is the unwrapped JSON).
- 2xx text → text. An empty body → a "no content" line.
- Any other content type → a tool error.
- Non-2xx → a tool error with the status and a short body excerpt. An exception in the endpoint → a generic tool
  error (the message is not returned or logged).
- Over `MaxResponseBytes` → a tool error; narrow the request.

## Audit and privacy

Every call — success, forbidden, refused, invalid arguments, timeout, error — writes an MCP audit row with the tool
name `api_<name>` (or `search_api`), the project, the duration, the caller kind and hash, and the outcome. The
arguments are stored only as the project's content-retention settings allow: under the
[retention lock](/features/mcp-server/) they are reduced to their size. Argument values and response bodies are
never written to logs.

`get_context format=agents_md` lists the project's endpoint tools in the brief (name and first description line).

## Limits

- Read-only only (writes come later, behind approval).
- Host **middleware does not run** — only the endpoint's own pipeline. Recreate what the endpoint needs from it (an
  ambient principal, a tenant, a culture, an admin user loaded into `HttpContext.Items`) in an
  `IHostEndpointDispatchMiddleware`.
  A global MVC `AuthorizeFilter` whose policy names authentication schemes re-authenticates by scheme, finds no
  cookie and denies; express such rules as endpoint metadata (`[Authorize]` / policy attributes) instead.
- File uploads and downloads are not supported; collections of objects in form models are not flattened.
- The request host is `localhost` and there is no remote IP address; do not rely on either for authorization.
- Parameter names from custom binders are not known, so their fields are free-form.
