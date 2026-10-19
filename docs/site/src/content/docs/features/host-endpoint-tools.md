---
title: Host Endpoint Tools
description: Expose selected endpoints of the host application as MCP tools (read-only by default, write tools behind an explicit opt-in) invoked in-process as the caller through the endpoint's own model binding, filters and authorization policies.
---

A host application already has endpoints that answer the questions an agent asks: a back-office grid of a
customer's loans, a lookup by id. `AddHostEndpointTools` turns the endpoints you mark into MCP tools. Each call runs
**in-process, through the endpoint's real pipeline** (model binding, action filters, authorization policies), as a
host identity built for the MCP caller, never as Beacon, and never with the caller's own token.

Every exposed endpoint **declares** whether it is read-only. The HTTP verb is never trusted, because back offices
read over POST (Kendo grids, `[FromForm]` filters). By default only read-only endpoints are exposed; endpoints that
change data become tools only when the host opts in with `AllowWriteTools` (see [Write tools](#write-tools)).

## Quick start

```csharp
builder.Services.AddBeaconMcp()
    .AddHostEndpointTools(o =>
    {
        o.ProjectName = "Back Office";                 // the tools belong to this Beacon project
        o.MaxResponseBytes = 1_048_576;                // default 1 MiB
        o.NamedToolLimit = 20;                         // above this: search_api + call_api
        o.RequestTimeout = TimeSpan.FromSeconds(30);   // default 30 s
        // o.TrustedAuthenticationSchemes = ["..."];   // default none; see "Authentication schemes" below
        // o.AllowWriteTools = true;                   // default false; see "Write tools" below
    });
```

`ProjectName` names the **host project**, the same one `ExposeDbContext` and `ExposeDocs` use. Beacon finds it by
its host key (`Project.HostManagedKey = host:{name, lower-cased}`), never by its display name, so a project a Beacon
user creates with the same name never gets the tools (see [Host documentation](/features/host-docs/#the-host-project)).
The tools appear once the host has run `SyncBeaconHostAsync` and the project exists.

Mark a controller action:

```csharp
[HttpPost]
[Authorize]                                           // required: [Permission] is a filter, not authorization metadata
[Permission(AdminPermission.ViewLoans)]               // the host's own filter: runs in the action pipeline (needs a dispatch middleware, below)
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
`[AllowAnonymous]`), or the host must have a fallback authorization policy; otherwise startup refuses it. A
permission check implemented as a filter (like the `[Permission]` filter above) is not metadata. A host that has
neither `[Authorize]` on its controllers nor a fallback policy puts `[Authorize]` on every exposed action, next to
`[BeaconTool]` and its own permission filter, as above.

### Authentication schemes

Beacon authorizes the dispatched call against the host principal it built for the MCP caller; it never runs an
authentication handler. A policy that names **authentication schemes** (`[Authorize(AuthenticationSchemes = "StepUp")]`,
`new AuthorizationPolicyBuilder("PartnerApi")`, a default or fallback policy with schemes) would therefore be
evaluated as if that scheme had authenticated the caller: a step-up or MFA scheme would be bypassed. So an exposed
endpoint whose combined policy names any scheme **fails startup**, naming the endpoint and the schemes, unless the
host lists each scheme in `TrustedAuthenticationSchemes`:

```csharp
.AddHostEndpointTools(o =>
{
    o.ProjectName = "Back Office";
    o.TrustedAuthenticationSchemes = ["Cookies"];      // only if the MCP caller is an acceptable stand-in for it
});
```

List a scheme only when an authorized MCP caller really should count as authenticated by it. Never list a step-up,
MFA or partner scheme.

## What gets exposed

- Only endpoints carrying `[BeaconTool]` / `.WithBeaconTool(...)`. Everything else is invisible.
- The name must match `^[a-z][a-z0-9_]{2,47}$` and be unique. `ReadOnly` must be declared: a missing flag **fails
  startup** with a message naming the endpoint, and so does `ReadOnly = false` unless `AllowWriteTools` is on (see
  [Write tools](#write-tools)). So do bad or duplicate names, `Destructive = true` on a read-only tool, file-upload parameters,
  `[AsParameters]`, an endpoint with no authorization at all, and an endpoint whose policy names an authentication
  scheme that is not in `TrustedAuthenticationSchemes` (see above).
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
plus arguments). Read-only tools carry `readOnlyHint = true`, `destructiveHint = false`, `idempotentHint = true`,
`openWorldHint = false`; write tools carry the hints in [Write tools](#write-tools). `search_api` is always read-only.
`call_api` is read-only only while every tool it can reach for this caller is; once it can reach a write tool it
carries `readOnlyHint = false` (and `destructiveHint = true` if any of them is destructive), so clients confirm every
`call_api` call. Each `search_api` result carries `read_only` (and, for a write tool, `destructive` and `idempotent`).
The eight built-in tools are unaffected.

## Write tools

A host can let agents change data through its own endpoints: an HR app letting a user book or withdraw their own
leave from Claude Desktop. This is off unless the host opts in, and each endpoint still declares itself:

```csharp
.AddHostEndpointTools(o =>
{
    o.ProjectName = "Employee Portal";
    o.AllowWriteTools = true;                          // without it, ReadOnly = false fails startup
});

[HttpPost("leave")]
[Authorize(Policy = "Employee")]
[BeaconTool("book_leave", Description = "Book leave for yourself.", ReadOnly = false)]
public IActionResult BookLeave([FromBody] BookLeaveRequest request) { ... }

[HttpDelete("leave/{id:int}")]
[Authorize(Policy = "Employee")]
[BeaconTool("withdraw_leave", Description = "Withdraw one of your leave requests.",
    ReadOnly = false, Destructive = true, Idempotent = true)]
public IActionResult WithdrawLeave(int id) { ... }

// Minimal API: .WithBeaconTool("withdraw_leave", "...", readOnly: false, destructive: true, idempotent: true)
```

- **Opt-in.** `AllowWriteTools` (default `false`) is the host-level switch. `ReadOnly` must still be declared on every
  endpoint; Beacon never infers it from the verb, in either direction.
- **Annotations.** A write tool carries `readOnlyHint = false`, `destructiveHint = Destructive` (default `false`),
  `idempotentHint = Idempotent` (default `false`) and `openWorldHint = false`. Declare `Destructive = true` for an
  endpoint that deletes or irreversibly overwrites data. Its description says it changes data and should be called
  only when the user asked for the change.
- **Confirmation is the client's.** `readOnlyHint = false` is what makes MCP clients (Claude Desktop, Claude Code) ask
  the user before running the tool. Beacon does not add its own confirmation step: MCP elicitation is not used for
  write tools, so a client that auto-approves tools runs them without asking.
- **User callers only.** A write tool is listed to, and runs for, a mapped Entra **user** caller only. A **system**
  caller (an app-only token or service user) never sees write tools (not in the tool list, not in `search_api`) 
  and a call to one is refused with `Forbidden: api_<name> changes data in the host application, and write tools run
  only for a signed-in user; a system caller cannot call them.`, whatever `HostClaims` it holds. A system identity
  acts with standing permissions and has no person who asked for the change and no client to confirm it. API-key
  callers cannot call any endpoint tool.
- **The endpoint is the authority.** Beacon checks the caller, the project and the endpoint's authorization policy,
  exactly as for reads, and adds nothing else. The endpoint must do its own input validation, business rules and
  record-level authorization ("only your own leave") against the host principal your `IMcpHostPrincipalFactory`
  builds: treat a tool call like any other request from that user. Antiforgery passes the same way as for reads
  (see [Antiforgery](#antiforgery)), so it is not a defence here.
- **Audit.** Write calls are audited under the tool name `api_<name>:write` (see [Audit and privacy](#audit-and-privacy)).

`IBeaconToolMetadata.Destructive` and `Idempotent` are default interface members: a host's own implementation of the
interface keeps compiling and behaves as `false` for both.

## Who the call runs as

Endpoint tools need a **mapped MCP caller**, an Entra user or system identity from
[Entra ID callers](/features/mcp-entra-callers/), configured for the tools' project and allowed to read it by its
[project access](/features/authorization/#project-access): a user caller reaches a restricted project only while its
Beacon user is a member (see [What a caller reaches](/features/mcp-entra-callers/#what-a-caller-reaches)). API-key
callers and callers outside the project neither see nor can call them (fail closed); neither can anyone while the
project does not exist.

`IMcpHostPrincipalFactory` builds the host principal from the caller:

- **System caller** → identity `BeaconMcpSystem` with the caller's configured `HostClaims` plus its name.
- **User caller** → identity `BeaconMcpUser` with name, email, preferred username and object id copied from the
  MCP principal, plus any `HostClaims` the caller mapper assigned (none with the configured mapper). The default
  gives a user **no host permissions**, and deliberately does **not** copy Entra `roles` or `groups`: a host role
  that happens to share a name (`Admin`) would otherwise be granted implicitly. Map users to host permissions in
  your own factory.
- `null` → the call is refused.

A host whose permissions live in its own user store registers its own factory. The example below maps the Entra user
to the host's admin user and its `AdminPermission` set. The host's permission claim is `HostClaimTypes.Permission`
(`http://schemas.example.com/claims/permission`) and its value is the permission's **integer** value
(`AdminPermission.ViewLoans.ToValueString()` → `"1007"`), which is what the host's `Identity.HasPermission` compares
against. The admin lookup is a **placeholder**: use whatever service the host already has for loading an active
admin and their permissions:

```csharp
builder.Services.AddSingleton<IMcpHostPrincipalFactory, HostPrincipalFactory>();

internal sealed class HostPrincipalFactory(IServiceScopeFactory scopes) : IMcpHostPrincipalFactory
{
    public async Task<ClaimsPrincipal?> CreateAsync(McpCaller caller, ClaimsPrincipal mcpPrincipal, CancellationToken ct)
    {
        if (caller.Kind == McpCallerKind.System)
        {
            // HostClaims come from Beacon:Mcp:Callers:Systems[*]:HostClaims: keep the set minimal.
            return new ClaimsPrincipal(new ClaimsIdentity(caller.HostClaims, "BeaconMcpSystem"));
        }

        var email = mcpPrincipal.FindFirst(ClaimTypes.Email)?.Value;
        await using var scope = scopes.CreateAsyncScope();
        // PLACEHOLDER: your own lookup of an active admin user and their AdminPermission set.
        var admins = scope.ServiceProvider.GetRequiredService<IYourAdminLookup>();
        var admin = email == null ? null : await admins.FindActiveAdminByEmailAsync(email, ct);
        if (admin == null)
        {
            return null;                               // not an admin: refused
        }

        var claims = admin.Permissions                 // IEnumerable<AdminPermission>
            .Select(x => new Claim(HostClaimTypes.Permission, x.ToValueString()))
            .Append(new Claim(ClaimTypes.Name, admin.UserName));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "BeaconMcpUser"));
    }
}
```

System identity host claims, in configuration (the value is the integer `AdminPermission` value; `1007` is
`ViewLoans`):

```json
"Beacon": { "Mcp": { "Callers": { "Systems": [ {
  "Name": "routine-runner",
  "ClientId": "<routine-runner-app-client-id>",
  "ProjectIds": [ 3 ],
  "HostClaims": [ { "Type": "http://schemas.example.com/claims/permission", "Value": "1007" } ]
} ] } } }
```

## How a call is dispatched

1. The caller, project and arguments are checked; the host principal is built.
2. A **new DI scope** and a new `HttpContext` are created. The request carries the method, the route filled from the
   endpoint's route pattern, the query string, the declared headers only, and a JSON or
   `application/x-www-form-urlencoded` body. **Nothing of the MCP request is forwarded**: no `Authorization` header,
   no cookies, no connection info. It also sends an `Accept` header preferring JSON (`application/json, text/plain;q=0.9, */*;q=0.1`) and `X-Requested-With: XMLHttpRequest`.
3. The endpoint and route values are set as routing would set them.
4. **Authorization** runs before the endpoint, exactly as the authorization middleware would, against the host
   principal: the endpoint's `[Authorize]` / policy / requirement metadata combined through the host's
   `IAuthorizationPolicyProvider`, evaluated with `IPolicyEvaluator`. `[AllowAnonymous]` is honoured. No
   authentication handler runs, which is why a policy naming an untrusted scheme is refused at startup (see
   [Authentication schemes](#authentication-schemes)). An endpoint
   without authorization metadata falls back to the host's `FallbackPolicy`; with no fallback policy either it is
   refused at startup (and at dispatch). A denial is the tool error `Forbidden.` with no detail. An endpoint marked
   `[AllowAnonymous]` is exposable and runs without an authorization check: the host itself declared it public, so
   exposing it to an authorized MCP caller grants nothing new.
5. The registered `IHostEndpointDispatchMiddleware` chain runs, in registration order, around the endpoint (see below).
6. The endpoint's `RequestDelegate` runs; for MVC that is the whole action pipeline: model binding, validation,
   authorization/action/exception filters, the result.
7. The response is captured into a buffer capped at `MaxResponseBytes`; the request is aborted after
   `RequestTimeout` or when the MCP call is cancelled.

Host code that reads `IHttpContextAccessor` sees the dispatched request. The dispatch runs in its own execution
context, so the MCP request's context (and the audit identity) is untouched.

### Host middleware and ambient state: dispatch middleware

Host **middleware does not run** on dispatch: only the endpoint's own pipeline. When the endpoint depends on state a
middleware sets, register an `IHostEndpointDispatchMiddleware` (namespace `Beacon.Core.HostEndpoints`, shipped in
`Moberg.Beacon.MCP`):

```csharp
Task InvokeAsync(HttpContext syntheticContext, McpCaller caller, Func<Task> next, CancellationToken cancellationToken);
```

Registered middlewares are chained in registration order, with the endpoint's request delegate as the innermost
`next`. The chain starts after `User`, `RequestServices`, the endpoint and route values are set and after Beacon's
authorization check. As in ASP.NET Core middleware, ambient state set before `await next()`
(`Thread.CurrentPrincipal`, an `AsyncLocal`) is visible to the endpoint and its filters; async work before it is
fine. A middleware that does not call `next` refuses the call; one that throws fails it. Both return a generic tool
error and are audited. The dispatch runs in its own execution context, so the MCP request's own ambient principal is
never changed.

Take a host whose `[Permission(AdminPermission.X)]` is an `IAsyncAuthorizationFilter` that checks
`Identity.Current.HasPermission(...)`, where `Identity.Current` reads `Thread.CurrentPrincipal`, which the host's own
middleware sets with `Identity.Set(identity)`: it takes a `ClaimsIdentity`, not a principal. Without a dispatch middleware every tool call would be denied. The
factory above maps the Entra user to admin permissions; the middleware makes that principal ambient:

```csharp
builder.Services.AddSingleton<IHostEndpointDispatchMiddleware, HostIdentityMiddleware>();

internal sealed class HostIdentityMiddleware : IHostEndpointDispatchMiddleware
{
    public async Task InvokeAsync(HttpContext ctx, McpCaller caller, Func<Task> next, CancellationToken cancellationToken)
    {
        // Identity.Set takes a ClaimsIdentity: the identity of the principal IMcpHostPrincipalFactory built.
        Identity.Set((ClaimsIdentity)ctx.User.Identity!);
        await next();
    }
}
```

### Antiforgery

Beacon **mints a real antiforgery token pair** for the host principal with the host's own `IAntiforgery` and puts it
on the dispatched request (the cookie token as a cookie, the request token in the configured header, or the form
field when the header is disabled). The host's own validation (`[ValidateAntiForgeryToken]`, a global
`AutoValidateAntiforgeryTokenAttribute`, minimal API form endpoints) therefore runs and passes. For minimal API
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

Every call (success, forbidden, refused, invalid arguments, timeout, error) writes an MCP audit row with the tool
name `api_<name>` (or `search_api`), the project, the duration, the caller kind and hash, and the outcome. A call to a
**write tool** is recorded as `api_<name>:write` (whether it was called by name or through `call_api`, and also when
it was refused) so writes can be filtered in the audit log (`tool=api_book_leave:write`) without a schema change.
Metrics still bucket it under `api_*`. The
arguments are stored only as the project's content-retention settings allow: under the
[retention lock](/features/mcp-server/) they are reduced to their size. Argument values and response bodies are
never written to logs.

`get_context format=agents_md` lists the project's endpoint tools in the brief (name and first description line);
write tools are marked *(writes)*.

## Limits

- Write tools are off unless `AllowWriteTools` is on, run for user callers only, and rely on the client's tool
  approval for confirmation (no Beacon-side approval or elicitation step).
- Host **middleware does not run**: only the endpoint's own pipeline. Recreate what the endpoint needs from it (an
  ambient principal, a tenant, a culture, an admin user loaded into `HttpContext.Items`) in an
  `IHostEndpointDispatchMiddleware`.
  A global MVC `AuthorizeFilter` whose policy names authentication schemes re-authenticates by scheme, finds no
  cookie and denies; express such rules as endpoint metadata (`[Authorize]` / policy attributes) instead.
- Endpoint metadata that names an authentication scheme fails startup unless the scheme is in
  `TrustedAuthenticationSchemes`.
- File uploads and downloads are not supported; collections of objects in form models are not flattened.
- The request host is `localhost` and there is no remote IP address; do not rely on either for authorization.
- Parameter names from custom binders are not known, so their fields are free-form.
