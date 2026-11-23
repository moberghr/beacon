---
title: Rate limits and request limits
description: "Every rate limit, request size and timeout bound on Beacon's routes, with keys, defaults, ranges, refusal answers and tuning examples."
---

Beacon limits how often a caller may use its routes and how much a single request may carry or take. The limits protect against password guessing, a runaway client, a request that never ends, and an upload that fills memory. Read this page when you size a deployment, when a client receives 429, or when you put Beacon behind a proxy.

There are two groups of settings.

- `Beacon:RateLimits`: how many requests a caller may send per time window.
- `Beacon:RequestLimits`: how large a request may be, how long it may run, and how many MCP sessions stay open.

Both apply to Beacon's routes only (`/beacon/api`, the hub under it and `/beacon/mcp`). They never touch the host's own routes, and they are independent of the host's `UseRateLimiter`.

## How the limits work

Each rate limit is a fixed window per partition. A partition is one caller. The window is `WindowSeconds` long (default 60). The first `PermitLimit` requests in a window pass. Later ones are refused until the window ends. Setting `PermitLimit` to `0` turns that limit off.

| Caller | Partition key | Used for |
|---|---|---|
| API key | `api-key:<id>` | REST, MCP, ask |
| Mapped MCP caller (bearer token) | `caller:<hash>` | REST, MCP, ask |
| Signed-in user | `user:<id>` | REST |
| Anonymous | `ip:<address>` | REST anonymous, login, setup |

For an IPv6 client the address is reduced to its /64 block, the block one site or subscriber normally gets, so one client cannot draw a fresh budget by changing its low bits. An IPv4-mapped IPv6 address counts as the IPv4 address.

Counts live in process memory. Each Beacon instance counts on its own, so with three instances a caller can send up to three times the limit across them. A restart clears the counts. The lockout is different: it is stored on the user and holds across instances.

`Beacon:RateLimits:MaxPartitions` bounds the memory. A new partition beyond the cap replaces the least recently used one, which then starts again with a full budget.

### The refusal

A request over a limit is answered 429 as problem+json with a `Retry-After` header in whole seconds.

```http
HTTP/1.1 429 Too Many Requests
Content-Type: application/problem+json
Retry-After: 17

{ "title": "Too Many Requests", "status": 429, "detail": "Too many requests; retry in 17 seconds." }
```

The body shows the fields Beacon sets; the framework may add its standard problem fields. Two answers have no body: the health route's 429 and the MCP `initialize` refusal.

A client should wait `Retry-After` seconds and retry. The `ask` tool answers in its own wording instead: `Too many questions for this credential; retry in {0} seconds.`

## Rate limits

| Key | Type | Default | Range | What it limits |
|---|---|---|---|---|
| `Beacon:RateLimits:Login:PermitLimit` | int | `10` | 0 to 100000 | Password sign-in, per client address. First-run setup has its own partition with the same size. |
| `Beacon:RateLimits:Api:PermitLimit` | int | `600` | 0 to 100000 | Every `/beacon/api` request, per authenticated caller (anonymous: per address). |
| `Beacon:RateLimits:Anonymous:PermitLimit` | int | `30` | 0 to 100000 | Anonymous requests Beacon answers 401 (no credential, unknown or expired key or token), per address. Over the limit they get 429. |
| `Beacon:RateLimits:Mcp:PermitLimit` | int | `60` | 0 to 100000 | Every request to `/beacon/mcp`, per API key or mapped caller. |
| `Beacon:RateLimits:Ask:PermitLimit` | int | `10` | 0 to 100000 | The `ask` tool (MCP and the playground), per caller. |
| `Beacon:RateLimits:<Limit>:WindowSeconds` | int | `60` | 1 to 3600 | Length of the window for that limit. |
| `Beacon:RateLimits:MaxPartitions` | int | `10000` | 100 to 1000000 | Partitions each limit keeps in memory. |

Lockout and break-glass delay settings are on [Sign-in and session hardening](/security/sign-in-and-sessions/#lockout).

Out-of-range values fail startup, for example:

```text
Beacon:RateLimits:Api:PermitLimit must be between 0 and 100000.
Beacon:RateLimits:Mcp:WindowSeconds must be between 1 and 3600.
Beacon:RateLimits:MaxPartitions must be between 100 and 1000000.
```

```json
{
  "Beacon": {
    "RateLimits": {
      "Login": { "PermitLimit": 5, "WindowSeconds": 60 },
      "Api": { "PermitLimit": 300, "WindowSeconds": 60 },
      "Mcp": { "PermitLimit": 120, "WindowSeconds": 60 },
      "Ask": { "PermitLimit": 10, "WindowSeconds": 60 },
      "MaxPartitions": 10000
    }
  }
}
```

```bash
Beacon__RateLimits__Api__PermitLimit=300
Beacon__RateLimits__Mcp__WindowSeconds=60
```

### The health route

`GET /beacon/api/health` has a fixed limit that is not configurable: 120 database probes per minute per remote address (the full address string, not the /64 block). Over it the answer is a bare 429 with `Retry-After` and no body. Behind a proxy, a load balancer's probes all share the proxy address unless forwarded headers are on.

### MCP initialize

An MCP client opens a session with an `initialize` request. Beacon limits how many sessions one caller may open, because each session holds memory.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:RequestLimits:McpMaxInitializesPerCaller` | int | `60` | 0 or more (`0` off) | `initialize` requests per caller per window. The caller is the API key, the mapped caller, the user, or the whole client address. |
| `Beacon:RequestLimits:McpInitializeWindowSeconds` | int | `60` | 0 or more | Window length. |
| `Beacon:RequestLimits:McpMaxIdleSessions` | int | `1000` | 0 or more | Idle MCP sessions kept in memory. The idle timeout is 30 minutes. |

Over the initialize limit the answer is 429 with `Retry-After` set to the window length, before a session exists, and Beacon logs a Warning: `MCP initialize refused: too many sessions opened by one caller.`

### MCP query concurrency and ceilings

`MaxConcurrentQueriesPerKey` limits how many SQL-carrying tool calls (`query`, `ask` and similar) one credential may have running at once. A call over the limit is refused at once and never queues. The tool answers:

```text
Too many concurrent queries for this credential; retry shortly.
```

The stored MCP setting defaults to `4`. A host can cap what an Admin may raise it to with `Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey` (also `MaxRowLimit`, `StatementTimeoutSeconds`, `MaxResultBytes`). The regulated profile requires these four ceilings to be set. See [Deployment locks](/security/deployment-locks/) for the full list.

## Request limits

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:RequestLimits:ApiMaxBodyBytes` | long | `2097152` (2 MiB) | 0 or more (`0` off) | Largest request body on `/beacon/api`. |
| `Beacon:RequestLimits:McpMaxBodyBytes` | long | `4194304` (4 MiB) | 0 or more | Largest request body on `/beacon/mcp`. |
| `Beacon:RequestLimits:ApiTimeoutSeconds` | int | `300` | 0 or more | How long one `/beacon/api` request may run before its cancellation token is cancelled. |
| `Beacon:RequestLimits:HubMaxReceiveMessageBytes` | long | `8192` (8 KiB) | 0 or more | Largest message the realtime hub accepts from a client. The hub has no client-callable methods. |
| `Beacon:RequestLimits:HubMaxParallelInvocationsPerClient` | int | `1` | 0 or more | Hub invocations running at once per connection. |
| `Beacon:RequestLimits:HubClientTimeoutSeconds` | int | `30` | 0 or more | Seconds without a client message before the hub drops the connection. |

A negative value fails startup: `Beacon:RequestLimits:ApiTimeoutSeconds must not be negative (0 turns the bound off).`

Two things to know.

- A host's stricter limit wins. Beacon lowers Kestrel's `MaxRequestBodySize` for its routes when its own value is smaller, and never raises it.
- The Kestrel keep-alive and header timeouts, and the 5-minute `HttpClient` timeout the AI calls depend on, are not changed here.

### Refusals

| Case | Answer |
|---|---|
| Body over the limit | Beacon lowers the request's maximum body size to the limit. What the client then sees for an oversized body is decided by the web server, not by Beacon's code. |
| `/beacon/api` request over `ApiTimeoutSeconds` | When the handler stops with a cancellation and the response has not started: 504 with `{"status":504,"title":"The request took too long."}` as `application/problem+json`. |

### Routes exempt from the timeout

The hub and routes that run long work in the request are exempt from `ApiTimeoutSeconds` and keep their own timeouts. The body limit still applies to them. Beacon marks routes such as a migration run, a metadata refresh and an AI evaluation with `.WithBeaconLongRunning()`. A host can mark its own routes that Beacon's pipeline covers:

```csharp
app.MapPost("/beacon/api/my-long-job", async (IMyJob job, CancellationToken ct) =>
{
    await job.RunAsync(ct);
    return Results.Accepted();
})
.WithBeaconLongRunning();
```

```json
{
  "Beacon": {
    "RequestLimits": {
      "ApiMaxBodyBytes": 1048576,
      "ApiTimeoutSeconds": 120,
      "McpMaxBodyBytes": 4194304,
      "McpMaxIdleSessions": 500
    }
  }
}
```

```bash
Beacon__RequestLimits__ApiTimeoutSeconds=120
```

## Embed limits

The embed surface (`Beacon:Embed`, off by default) has its own limits, because its routes are anonymous. They are described fully on [Embedding](/integrate/embedding/). Every refusal answers the same body, `401 {"error":"embed_unavailable"}`, except the rate limit (429 with `Retry-After`) and a busy render slot (503 with `Retry-After`).

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Embed:RateLimits:ExchangePerGrantPerMinute` | int | `60` | 1 to 10000 | Token exchanges per grant. |
| `Beacon:Embed:RateLimits:RenderPerGrantPerMinute` | int | `120` | 1 to 10000 | Renders per grant. |
| `Beacon:Embed:RateLimits:RefusalsPerIpPerMinute` | int | `30` | 1 to 10000 | Refusals counted per client address. Once spent, the exchange and the page answer 429 before any lookup. |
| `Beacon:Embed:RateLimits:PagePerClientPerMinute` | int | `60` | 1 to 10000 | Page loads per client address and grant. |
| `Beacon:Embed:MaxConcurrentRenders` | int | `8` | 1 to 256 | Renders running at once in the process. |
| `Beacon:Embed:MaxConcurrentRendersPerGrant` | int | unset (a quarter of `MaxConcurrentRenders`, at least 1) | 1 to `MaxConcurrentRenders` | Renders running at once per grant. |
| `Beacon:Embed:MaxRows` | int | `1000` | 1 to 100000 | Rows a render returns. |
| `Beacon:Embed:TokenTtlSeconds` | int | `300` | 1 to `MaxTokenTtlSeconds` | Lifetime of an issued token. |
| `Beacon:Embed:MaxTokenTtlSeconds` | int | `900` | 1 to 900 | Longest token lifetime. |

## LLM queue limits

Every call to an LLM provider goes through one queue (`LlmRequestQueue`), so a burst of questions cannot open unlimited provider connections.

| Setting | Default | What it does |
|---|---|---|
| `MaxConcurrentRequests` | `50` | Calls running at once. |
| `MaxQueuedRequests` | `200` | Calls waiting for a slot. A call that finds the queue full gets `The LLM is busy. Try again later.` |
| `RequestsPerMinute` | `1000` | Enforced only while rate limits are on. Non-positive: unlimited. |
| `TokensPerMinute` | `80000` | Enforced only while rate limits are on. Non-positive: unlimited. |
| `RateLimitsEnabled` | `false` | Whether the two per-minute limits are enforced. |
| `MonthlyBudget` | `100.00` | Recorded, not enforced. |

Only an Admin may change the concurrency limit, the per-minute limits or whether they are enforced. For anyone else the save succeeds for the other settings and writes no limit row. A stored limit replaces the host-configured one, also at startup. An attempt by a non-Admin to change them answers: `Only an Admin can change the LLM concurrency limit, the requests or tokens per minute, or whether they are enforced.`

## Behind a reverse proxy

Per-address limits (login, setup, anonymous 401s, health) and the cookie's `Secure` flag use the address and scheme the host reports. Behind a proxy that is the proxy's own address unless the host runs `UseForwardedHeaders`. Without it, every client shares one budget and the first noisy client can lock everyone out of login.

Trust only your own proxies, and put the middleware first in the pipeline.

```csharp
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwarded.KnownProxies.Clear();
forwarded.KnownNetworks.Clear();
forwarded.KnownProxies.Add(IPAddress.Parse("10.0.0.5"));
app.UseForwardedHeaders(forwarded);
```

The reference host `Beacon.SampleProject` turns this on from configuration, and trusts nothing when the list is empty:

```json
{
  "Beacon": {
    "ForwardedHeaders": {
      "Enabled": true,
      "KnownProxies": [ "10.0.0.5" ]
    }
  }
}
```

`Beacon:ForwardedHeaders` is a setting of that sample host, not of Beacon itself. In your own host, call `UseForwardedHeaders` as shown above. Trusting a client-supplied `X-Forwarded-For` lets any client pick its own address and so draw a new budget on every request. See [Production checklist](/integrate/production/#reverse-proxy).

## Tuning examples

### Small internal deployment

A few dozen users, single sign-on, no public access. The defaults fit. Turn the lockout down only if your identity provider already enforces its own.

```json
{
  "Beacon": {
    "RateLimits": {
      "Api": { "PermitLimit": 600 },
      "Mcp": { "PermitLimit": 60 }
    }
  }
}
```

### Internet-facing deployment

Anonymous traffic reaches the login and 401 paths, so tighten those and bound memory.

```json
{
  "Beacon": {
    "RateLimits": {
      "Login": { "PermitLimit": 5, "WindowSeconds": 60 },
      "Anonymous": { "PermitLimit": 20, "WindowSeconds": 60 },
      "Api": { "PermitLimit": 300, "WindowSeconds": 60 },
      "MaxPartitions": 50000
    },
    "RequestLimits": {
      "ApiMaxBodyBytes": 1048576,
      "ApiTimeoutSeconds": 120
    }
  }
}
```

### Busy MCP deployment

Many agents share a small number of API keys. Raise the MCP limit per key, keep `ask` tight because each ask can cost several LLM calls, and cap idle sessions.

```json
{
  "Beacon": {
    "RateLimits": {
      "Mcp": { "PermitLimit": 240, "WindowSeconds": 60 },
      "Ask": { "PermitLimit": 10, "WindowSeconds": 60 }
    },
    "RequestLimits": {
      "McpMaxIdleSessions": 2000,
      "McpMaxInitializesPerCaller": 120
    }
  }
}
```

Several instances multiply every in-memory limit. Divide each `PermitLimit` by the number of instances, or use session affinity so a caller stays on one instance.

## How to observe the limits

| What | Where |
|---|---|
| A limited REST, MCP, login or setup request | The client sees 429 and `Retry-After`. Beacon writes no dedicated log event for it. |
| An MCP `initialize` refusal | Warning `MCP initialize refused: too many sessions opened by one caller.` |
| A locked account | Log event 7124 `UserLockedOut`, audit action `auth.user_locked_out`. |
| A request over `ApiTimeoutSeconds` | The client sees 504 when the cancellation reaches the middleware; Beacon writes no dedicated log event. |

Count 429 and 504 responses at your proxy or in your request logging to watch for pressure.

## Recommended values

| Setting | Development | Production | Regulated profile |
|---|---|---|---|
| `RateLimits:Login`, `Api`, `Anonymous`, `Mcp`, `Ask` | defaults | defaults or tighter | each `PermitLimit` must be above 0 |
| `RateLimits:Lockout` | on | on | must be on |
| `RequestLimits` bounds | defaults | defaults or tighter | not constrained |
| `Mcp:Ceilings` (four numeric ceilings) | unset | set | must be set; filled by the profile when unset |
| `UseForwardedHeaders` | off | on, own proxies only | on, own proxies only (the profile does not check it) |

The regulated profile (`Beacon:Profile = Regulated`) fails startup when any of the five rate limits is off (`PermitLimit` 0) or the lockout is off, naming each key in one message.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Everyone gets 429 on login | Behind a proxy without forwarded headers, so all clients share one address | Add `UseForwardedHeaders` with your proxies. |
| One automation job gets 429 on `/beacon/api` | It exceeds 600 requests per minute on one key | Raise `Api:PermitLimit`, or give each job its own key. |
| MCP client gets 429 right after connecting | Too many `initialize` calls from one caller | Reuse the session; raise `McpMaxInitializesPerCaller` if legitimate. |
| `Too many concurrent queries for this credential` | More running queries than `MaxConcurrentQueriesPerKey` | Retry shortly, or raise the setting within the host ceiling. |
| 504 on a long job | The route is not marked long-running | Use `.WithBeaconLongRunning()`, or raise `ApiTimeoutSeconds`. |
| A large import is refused | Body over `ApiMaxBodyBytes` | Raise it; a lower host Kestrel limit still wins. |
| `The LLM is busy. Try again later.` | The LLM queue is full | Raise `MaxQueuedRequests` or `MaxConcurrentRequests` (Admin). |
| Limit seems higher than configured | Several instances count separately | Divide the limit by instance count. |
| Startup fails on a limit | Out of range | The message names the key and range. |

## Related pages

- [Sign-in and session hardening](/security/sign-in-and-sessions/)
- [Browser protections](/security/browser-protections/)
- [API keys](/security/api-keys/)
- [Deployment locks](/security/deployment-locks/)
- [Regulated profile](/security/regulated-profile/)
- [Embedding](/integrate/embedding/)
- [Production checklist](/integrate/production/)
- [Configuration reference](/getting-started/configuration/#request-limits)
