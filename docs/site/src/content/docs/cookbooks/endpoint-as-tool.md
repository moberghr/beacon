---
title: Expose an endpoint as an MCP tool, safely, and test it
description: "Turn one self-service endpoint of your ASP.NET Core application into a read tool and one into a write tool, bind the person's id from the caller, and pin the behaviour with an end-to-end test."
---

| | |
|---|---|
| **You end up with** | A read tool (`api_my_orders`) and a write tool (`api_request_leave`) on your MCP server, the person's id filled from the caller, refusals that reach the agent, and a test that lists the tools, calls one and checks another person's data is refused |
| **Time** | about 30 minutes |
| **You need** | An ASP.NET Core application with an MCP gateway ([MCP gateway](/mcp/gateway/)) or full Beacon with `AddHostEndpointTools` ([Host endpoint tools](/mcp/host-endpoint-tools/)); a PostgreSQL or SQL Server database with Beacon's schema migrated for the test |
| **Applies to** | Beacon 5.0 · Both (gateway: `x.Tools(...)`; full Beacon: `AddBeaconMcp().AddHostEndpointTools(...)`) |

:::tip[Let your AI do it]
Paste this into Claude Code (or another coding agent) in your application's repository.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/endpoint-as-tool.md and follow it step by step in this repository.
Ask me which endpoint to expose and whether it is self-service before marking anything. Ask me before every decision
the recipe marks as one. Never mark an admin endpoint, never commit secrets, and show me the result of each
"Check it works" item.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Which endpoint | One that reads or changes the signed-in person's own records | A `/api/my/...` read |
| Read or write | `ReadOnly = true`, or `ReadOnly = false` (needs `AllowWriteTools`) | Read |
| How the person's id arrives | Bound from a claim of the call's principal (`BindFromCaller`) | Bound, never asked of the agent |
| How the agent is held to self-service | Only mark self-service endpoints, and check `IsBeaconMcpCall()` where one endpoint also serves the browser | Mark only self-service endpoints |

## 1. Pick the endpoint and check it against the review list

Mark an endpoint only when every line holds. If one fails, add a `/api/my/...` sibling that filters on the caller and
takes no subject, and mark that.

- [ ] **Self-service.** It reads or changes the person's own records. It is not an admin endpoint and is not gated by a
      permission a person may hold over others.
- [ ] **Data minimisation.** It never returns other people's data to some callers (a manager's view, a list by
      sequential id). An agent reads free text and sends what it reads to a model.
- [ ] **Authorization metadata.** It has `[Authorize]` or a policy. Startup refuses an endpoint with none, unless the
      application has a fallback policy. Never `[AllowAnonymous]`.
- [ ] **Bound id.** The person's id is not an argument the agent supplies.
- [ ] **Description.** Written for an agent (step 3).
- [ ] **`ReadOnly` declared.** Always, on every tool. Beacon never infers it from the HTTP verb.

## 2. Bind the person's id from the caller

An agent does not know the person's id in your application. Bind the argument to a claim of the principal the call runs
as, so it leaves the tool's schema and carries the caller's own value.

In the gateway, the claim is one your `IMcpHostUserResolver` returns in `McpHostUser.Claims`:

```csharp
x.Tools(y =>
{
    y.AllowWriteTools = true;                       // only because step 4 marks a write tool
    y.BindFromCaller("userId", "app_user_id");
});
```

In full Beacon, the same call is on the options of `AddHostEndpointTools`, and the claim comes from the
`IMcpHostPrincipalFactory` you register (see [Who the call runs as](/mcp/host-endpoint-tools/#who-the-call-runs-as)):

```csharp
builder.Services.AddBeaconMcp()
    .AddHostEndpointTools(y =>
    {
        y.ProjectName = "app";
        y.AllowWriteTools = true;
        y.BindFromCaller("userId", "app_user_id");
    });
```

On every tool, a route, query, header or form value named `userId` (ignoring case), or a top-level property of a JSON
body with that name, is removed from the schema. An agent that sends it anyway is refused with
`Invalid arguments: userId is filled from the caller and cannot be sent.` A principal with no value for the claim
refuses the call. A bound name that no tool takes is warned at startup, which usually means a typo.

## 3. Mark a read tool

```csharp
using Beacon.MCP.HostEndpoints;

app.MapGet("/api/my/orders", (int userId, OrderService orders) => orders.Of(userId))
    .RequireAuthorization()
    .WithBeaconTool(
        "my_orders",
        "The signed-in user's own orders, newest first. Returns id, placedAt (UTC, ISO 8601), status "
        + "(pending, shipped, cancelled) and total (EUR). Takes no arguments. Returns an empty list when the user has no orders.",
        readOnly: true);
```

The name must match `^[a-z][a-z0-9_]{2,47}$` and be unique. MCP exposes it as `api_my_orders`.

Write the description for the agent, not for a developer:

- What comes back, field by field, with units and time zones.
- What each argument means, and what leaving it out does.
- Every refusal the endpoint can produce.

Beacon appends the method and route to every description.

Your endpoint still checks ownership as it does for the browser: the bound id is the caller's, and the endpoint decides
what that person may see.

## 4. Mark a write tool

A write tool is off until `AllowWriteTools` is on (step 2). Declare `ReadOnly = false` and state `Destructive` and
`Idempotent`:

```csharp
using Beacon.Core.HostEndpoints;

[Authorize]
[BeaconTool("request_leave",
    Description = "Request leave for the signed-in user. Arguments: from and to (dates, yyyy-MM-dd, inclusive) and "
        + "reason (optional). Returns the new request's id and status. Refused (400) when the dates overlap an existing "
        + "request. Call it only when the person asked for this change.",
    ReadOnly = false, Destructive = false, Idempotent = false)]
[HttpPost("/api/time-off/requests")]
public IActionResult RequestLeave([FromBody] RequestLeaveBody body) { /* validate, check balance, save */ }
```

`[BeaconTool]` goes on a controller action, or on a handler class whose framework copies class attributes into its
endpoint (a Moberg Warp handler). On an MVC controller class it is refused at startup. A minimal API uses
`.WithBeaconTool(name, description, readOnly: false, destructive: false, idempotent: false)`.

What the opt-in changes:

- The tool carries `readOnlyHint = false`, so clients such as Claude Desktop and Claude Code ask the person before each
  call. Beacon adds no confirmation step of its own.
- It is listed to, and runs for, a signed-in user only. An app-only caller never sees it.
- The audit records it as `api_request_leave:write`.
- The endpoint is the authority: it validates input and applies its own business rules and record-level authorization.

## 5. Hold agents to self-service

A tool call runs your endpoint as a principal with the authentication type `BeaconMcpUser`. When one endpoint serves
both the browser and agents, use it to give agents less than the browser:

```csharp
using Beacon.Core.Mcp;

if (httpContext.User.IsBeaconMcpCall())
{
    // no permissions, no manager grants: the person's own records only
}
```

## 6. Make refusals reach the agent

Tool calls run your `IExceptionHandler`s in registration order, as `UseExceptionHandler()` does for the browser. A
deliberate refusal your application maps to a 403, 404 or 400 problem reaches the agent as that status and body. An
exception no handler takes becomes the generic error `The host endpoint behind api_<name> failed with an unexpected error.`
Set `UseHostExceptionHandlers = false` on the tool options to always answer the generic error.

## 7. Test it end to end

Add `Moberg.Beacon.Testing` to the test project. In the test host, after the gateway registration, trust test tokens
and register in-memory people:

```csharp
var tokens = new McpTestTokens(tenantId, clientId);
var users = new McpTestUsers()
    .Add(objectId: "oid-ada", id: "42", displayName: "Ada", new Claim("app_user_id", "42"))
    .Add(objectId: "oid-bob", id: "43", displayName: "Bob", new Claim("app_user_id", "43"));

builder.Services.TrustMcpTestTokens(tokens);   // after AddBeaconMcpGateway, in the test host only
builder.Services.UseMcpTestUsers(users);

await using var ada = await McpTestClient.ConnectAsync(app.GetTestClient(), tokens.For("oid-ada"));

// 1. Exactly the expected tools, and the bound id is not in the schema
var tools = await ada.ListToolsAsync();
tools.Select(x => x.Name).Should().BeEquivalentTo("api_my_orders", "api_request_leave", "get_context", "search", "get_documentation");
tools.Single(x => x.Name == "api_my_orders").InputSchema.GetRawText().Should().NotContain("userId");

// 2. A call returns only the caller's data
var mine = await ada.CallToolAsync("api_my_orders");
mine.IsError.Should().NotBe(true);
McpTestClient.Text(mine).Should().Contain("order-of-ada").And.NotContain("order-of-bob");

// 3. Naming somebody else's id is refused
var other = await ada.CallToolAsync("api_my_orders", new Dictionary<string, object?> { ["userId"] = 43 });
other.IsError.Should().BeTrue();
McpTestClient.Text(other).Should().Contain("filled from the caller");
```

The client is the official MCP SDK's, so the test goes through the bearer middleware, the caller mapping, the route
policy, the MCP filters and the audit, and your endpoint's own authorization. Listing and calling tools needs Beacon's
database (project access and the audit live there): run these tests against a real PostgreSQL or SQL Server with
Beacon's schema migrated. `McpTestClient.Json(result)` returns a successful call's structured content when you want to
assert on fields instead of text.

Keep a pinned list of the application's tools, so adding a tool, or turning a read into a write, fails a test until
someone reviews it.

## Check it works

- [ ] The application starts. A missing `ReadOnly`, a bad name, an endpoint without authorization or `ReadOnly = false`
      without `AllowWriteTools` fails startup with a message that names the endpoint.
- [ ] The test lists exactly the tools you marked, and `api_my_orders` has no `userId` argument.
- [ ] A call as Ada returns Ada's data only; a call as Bob returns Bob's.
- [ ] A call that sends `userId` is refused.
- [ ] In Claude, the write tool asks before it runs, and the MCP audit shows it as `api_request_leave:write`.

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| Startup names an endpoint and says `ReadOnly` was not declared | Set `ReadOnly = true` or `false`, or pass `readOnly:` to `.WithBeaconTool`. The verb is never trusted. |
| Startup refuses `ReadOnly = false` | Set `AllowWriteTools = true` in the tool options. |
| Startup refuses an endpoint with no authorization | Add `[Authorize]` or a policy, or define a fallback policy. |
| Startup refuses a policy that names authentication schemes | List the scheme in `TrustedAuthenticationSchemes` only if an authorized MCP caller is an acceptable stand-in for it. Never list a step-up or MFA scheme. |
| The tool is missing from the list | The caller is not mapped, cannot read the project, or the host project is not synced yet. A write tool is also hidden from system callers. |
| Every call returns `Forbidden.` | The endpoint's policy denied the principal. Check the claims your resolver or principal factory sets. In full Beacon, host middleware does not run on a dispatched call: see [dispatch middleware](/mcp/host-endpoint-tools/#host-middleware-and-ambient-state-dispatch-middleware). |
| `... failed with an unexpected error` | The endpoint threw and no `IExceptionHandler` took it. |
| A bound name is warned at startup | No tool takes that argument. Check the spelling against the endpoint's parameter. |

## Next

- [Host endpoint tools](/mcp/host-endpoint-tools/): every argument source, the tool catalog above `NamedToolLimit`, dispatch and audit details.
- [MCP gateway](/mcp/gateway/): the whole gateway recipe, including the Entra registration and `beacon-doctor`.
- [Ground AI answers in your own docs and data model](/cookbooks/ground-with-your-docs/): give the agent documents that explain your tools.
