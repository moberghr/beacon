---
title: Browser protections
description: "The response headers, Content-Security-Policy, Host and Origin check and antiforgery tokens that protect Beacon's own pages and API from other web pages, with every setting and a rollout plan."
---

Beacon serves a web application and a cookie-authenticated API. A browser sends the session cookie with every request to Beacon, including requests started by a page on another site. The protections on this page stop that page from reading Beacon's responses, framing Beacon, forging writes in a user's name or reaching Beacon under a host name you did not configure.

Read this page when you put Beacon behind a reverse proxy, when you enable `OriginGuard` enforcement, when your host application embeds Beacon's single-page app (SPA), or when a browser console reports a blocked resource on a Beacon page.

There are four protections:

| Protection | What it blocks | Where it runs |
|---|---|---|
| Security response headers | Content sniffing, framing, referrer leaks, device features | Every Beacon response |
| Content-Security-Policy (CSP) | Scripts, styles, frames and connections from other origins | Responses under `/beacon` |
| Host and Origin check | Cross-site writes, cross-site WebSockets, DNS rebinding | State-changing, realtime-hub and anonymous requests |
| Antiforgery token | Forged writes that carry the session cookie | Non-GET requests from a signed-in browser session |

All of them act on Beacon's own routes only: `/beacon` and the prefixes in `Beacon:Authentication:AdditionalPaths`. Your own routes keep whatever your application sends. See [Where Beacon credentials apply](/security/sign-in-and-sessions/#where-beacon-credentials-apply).

## Response headers

The middleware writes these headers when the response starts, so error pages and static files carry them too. It uses `TryAdd`: **a header the response already carries is kept**. A host or an endpoint can therefore send its own value and Beacon will not overwrite it.

| Header | Exact value | Notes |
|---|---|---|
| `X-Content-Type-Options` | `nosniff` | |
| `X-Frame-Options` | `DENY` | Not added when the response already has a `Content-Security-Policy` that names `frame-ancestors` (the embed page decides who may frame it). |
| `Referrer-Policy` | `strict-origin` | Not `no-referrer`: under `no-referrer` a browser sends `Origin: null` on the SPA's own POSTs, and the origin check would refuse them. |
| `Permissions-Policy` | `accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()` | Turns the listed device features off. |
| `Content-Security-Policy` | see [below](#content-security-policy) | Only under `/beacon`, and only in `Enforce` mode. |

### HSTS is the host's job

Beacon does not send `Strict-Transport-Security`. HSTS binds a whole host name, and Beacon shares that name with your application. Send it from the host (or the proxy):

```csharp
// Program.cs
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

// Optional: Beacon's middleware pipeline adds this itself when you leave it out.
// Call it early so the SPA's static files carry the headers too.
app.UseBeaconSecurityHeaders();

app.UseStaticFiles();
app.UseBeaconPipeline();
```

The reference host calls `UseBeaconSecurityHeaders()` before `UseStaticFiles()` for this reason. `UseBeaconAuthentication()` adds the headers itself when you do not call it, and calling it twice is harmless.

## Content-Security-Policy

`Beacon:Security:Csp` chooses how Beacon sends the policy.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Security:Csp` | enum | `Enforce` | `Enforce`, `ReportOnly`, `Off` | `Enforce` sends `Content-Security-Policy`: the browser blocks what the policy refuses. `ReportOnly` sends `Content-Security-Policy-Report-Only`: the browser logs violations in its console and blocks nothing. `Off` sends no policy; the other headers are still sent. |

The policy, exactly as the code builds it:

```text
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
```

In plain words:

- Scripts, workers, fonts and connections load only from Beacon's own origin. Nothing loads from a CDN.
- Styles may be inline, because the SQL editor and the diagram renderer inject style elements.
- Images may be `data:` URIs (editor icons).
- Plugins (`object-src`) are off, `<base>` may only point at Beacon, forms may only post to Beacon.
- No page can frame Beacon (`frame-ancestors 'none'`).

The policy is sent on `/beacon` paths only. A path that is Beacon's only because of `AdditionalPaths` (for example `/warp`) gets the four other headers but no CSP.

If the response already has a `Content-Security-Policy` or a `Content-Security-Policy-Report-Only` header, in either form, Beacon sends nothing of its own. That is how you replace the policy:

```csharp
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/beacon"))
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.ContentSecurityPolicy =
                "default-src 'self'; script-src 'self'; connect-src 'self' https://telemetry.example.com; frame-ancestors 'none'";
            return Task.CompletedTask;
        });
    }

    await next();
});
```

Register that middleware after `UseBeaconSecurityHeaders()`. `OnStarting` callbacks run in reverse order of registration, so yours runs first, and Beacon's callback then sees your header and keeps it.

### Moving from ReportOnly to Enforce

Use this when a proxy, a browser extension policy or a host customisation might load something the policy refuses.

1. Set `Beacon:Security:Csp` to `ReportOnly` in the environment you test in.
2. Use Beacon in a browser with the developer console open: sign in, open the query editor, open a diagram, open the admin pages.
3. Look for console messages that start with `[Report Only] Refused to ...`. Each names the directive and the blocked URL.
4. If there are none, set `Csp` to `Enforce` (or remove the key; `Enforce` is the default).
5. If there are some, find where the blocked resource is added. A proxy that injects a script, for example, needs to stop doing so, or you send your own policy as shown above.

```json
{
  "Beacon": {
    "Security": {
      "Csp": "ReportOnly"
    }
  }
}
```

Environment variable form:

```bash
Beacon__Security__Csp=ReportOnly
```

## The Host and Origin check

A page on another site can make a browser send a `POST` to Beacon with the user's cookie. The check refuses such a request by comparing the `Origin` header, and the `Host` header, with the origins you configured.

### Settings

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Security:OriginGuard` | enum | `LogOnly` | `LogOnly`, `Enforce` | `LogOnly`: a request the check would refuse goes on and a warning is logged. `Enforce`: the request is answered 403. |
| `Beacon:Security:AllowedOrigins` | string list | empty | each entry an `http` or `https` origin: scheme, host, optional port, no path, query or fragment (a trailing `/` is ignored) | The origins browsers reach Beacon from. |
| `Beacon:PublicBaseUrl` | URL | unset | an absolute `http` or `https` URL; its path is not part of the origin | Its origin is added to the allowed origins. Also used by the MCP discovery documents. |

An entry that is not a valid origin stops the application at startup. The message names the entry:

```text
Beacon:Security:AllowedOrigins:0 must be an http or https origin such as https://beacon.example.com: a scheme, a host and an optional port, without a path, query or fragment.
```

When neither `AllowedOrigins` nor `PublicBaseUrl` yields an origin, the allowed origin is the request's own: its scheme and `Host`, after the host's forwarded headers. When at least one is configured, they replace that default.

```json
{
  "Beacon": {
    "PublicBaseUrl": "https://beacon.example.com",
    "Security": {
      "OriginGuard": "Enforce",
      "AllowedOrigins": [ "https://beacon.example.com", "https://beacon-admin.example.com:8443" ]
    }
  }
}
```

```bash
Beacon__Security__OriginGuard=Enforce
Beacon__Security__AllowedOrigins__0=https://beacon.example.com
```

### Which requests are checked

A request on a Beacon route is checked when it is **any** of these:

- State-changing: any method other than `GET`, `HEAD`, `OPTIONS` and `TRACE`.
- A request to the realtime hub, `/beacon/api/hub` (the negotiate call and the WebSocket upgrade).
- Sent to an anonymous endpoint (sign-in, first-run setup, health).

Everything else passes. For a checked request the rules are, in order:

1. **Authorization header.** A request that carries an `Authorization` header (API key, bearer token) passes. A page on another origin cannot add that header without a CORS preflight, so only cookie-authenticated requests need the check.
2. **Host.** With configured origins, a request that looks like it came from a browser (it has an `Origin` or a `Sec-Fetch-Site` header) must arrive with a `Host` that matches one of them. A `Host` without a port matches an origin on its scheme's default port (80 or 443). Hosts are compared in punycode form. Without configured origins this step does not run, so there is no DNS-rebinding protection.
3. **Origin.** For state-changing and hub requests, the `Origin` must be one of the allowed origins. The opaque origin `null` is never allowed.
4. **No Origin.** For state-changing and hub requests with no `Origin`, the `Sec-Fetch-Site` header must not be `cross-site` or `same-site`.

Requests that no browser sent (no `Origin`, no `Sec-Fetch-Site`: scripts, API-key clients, MCP clients, health probes) pass.

An OpenID Connect scheme's own paths also pass: its callback, signed-out callback and remote sign-out path. The identity provider posts there from its own origin. The check runs after `UseAuthentication`, so a remote sign-in callback is answered first.

In the [MCP gateway](/mcp/server/), the check runs before routing, so the anonymous-endpoint rule does not apply there. The state-changing and hub rules do.

### What a refusal looks like

Verdicts: `ForeignHost`, `ForeignOrigin` (including a `null` origin) and `CrossSiteRequest` (no `Origin`, cross-site fetch).

In `Enforce` mode the response is:

```http
HTTP/1.1 403 Forbidden
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "The request's origin is not allowed.",
  "traceId": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00"
}
```

The log line is a warning with no event id:

```text
The Beacon origin check refused POST /beacon/api/queries (ForeignOrigin).
```

In `LogOnly` mode the request is let through and the warning reads:

```text
The Beacon origin check would refuse POST /beacon/api/queries (ForeignOrigin); the request was let through because Beacon:Security:OriginGuard is LogOnly. List the origins browsers use in Beacon:Security:AllowedOrigins or Beacon:PublicBaseUrl.
```

The method and path are written on one line without control characters, at most 256 characters each. Nothing else about the request, such as the `Origin` value, is logged.

### Rollout: LogOnly to Enforce

1. **Set the public origin.** Set `Beacon:PublicBaseUrl` to the address users type, or list every address in `Beacon:Security:AllowedOrigins`. List each name a browser uses: the public name, an internal name, a name with a non-default port.
2. **Keep `OriginGuard` at `LogOnly`** and run a full release. Use the application normally, including SSO sign-in and any scripts that act on a user's cookie.
3. **Search the logs** for `The Beacon origin check would refuse`. Each line is a request that would be refused once enforced. Decide for each: is the origin one you own? Add it to `AllowedOrigins`. Is it an unknown site? Then the check is doing its work.
4. **Repeat** until a normal week has no such lines.
5. **Set `OriginGuard` to `Enforce`.** If no origin is configured at this point, Beacon logs this warning at startup:

   ```text
   Beacon:Security:OriginGuard is Enforce but no origin is configured (Beacon:Security:AllowedOrigins, Beacon:PublicBaseUrl), so the check compares each Origin with the request's own scheme and Host. Behind a TLS-terminating proxy that does not forward the scheme every browser write is refused, and without configured origins the Host check does not run, so it gives no DNS-rebinding protection.
   ```

6. After the change, watch for `The Beacon origin check refused` and for 403 responses with the detail `The request's origin is not allowed.`

## Antiforgery tokens

Antiforgery protects the cookie-authenticated API from forged writes. Beacon issues a token to the signed-in browser; the browser must send it back in a header on every write.

### Which requests need the token

| Request | Token required |
|---|---|
| `GET`, `HEAD`, `OPTIONS` | No |
| Anonymous non-GET request | No, except `POST /beacon/api/auth/login` and `POST /beacon/api/auth/logout`, which need it even for anonymous callers |
| Non-GET request from a signed-in browser session | Yes |
| A request authenticated by an `Authorization` header (API key, bearer token) | No. A browser does not send these on its own, so such a request cannot be forged. |

Sign-out needs the token for anonymous callers too. A cross-site form post arrives without the `SameSite=Lax` session cookie, yet the sign-out answer would still delete that cookie. Without a valid token nothing is signed out and no cookie is emitted.

### How the browser gets and sends it

1. The SPA calls `GET /beacon/api/csrf`. The response sets two cookies and returns the token in the body:
   - `.Beacon.Antiforgery`: HttpOnly, holds the cookie half of the token pair. The name is Beacon's own, so it does not collide with a host cookie of the default name.
   - `XSRF-TOKEN`: readable by JavaScript, `SameSite=Lax`, `Secure` when the request is HTTPS, path `/`.
2. On every write the SPA sends the token in the **`X-XSRF-TOKEN`** request header.
3. After sign-in and sign-out Beacon issues a fresh token bound to the new identity. When a stale token gets the 400 below, the SPA fetches a new one and retries once.

```http
GET /beacon/api/csrf HTTP/1.1
Cookie: Beacon.Auth=...
```

```json
{ "token": "<antiforgery request token>" }
```

A caller authenticated with an `Authorization` header gets no token. `GET /beacon/api/csrf` answers 400 with the title `Antiforgery tokens are issued to browser sessions only.` and the detail `Requests authenticated with an Authorization header do not need an antiforgery token.`

### A host that embeds the SPA

`AddApi()` sets the antiforgery header name to `X-XSRF-TOKEN` and the cookie name to `.Beacon.Antiforgery`. Antiforgery options are shared by the whole application, so these two settings apply to your own forms too.

If your host configures antiforgery with its own header name, for example `RequestVerificationToken`, Beacon still accepts the SPA's `X-XSRF-TOKEN`. It copies that header's value into your header name before validating. Only the header name is bridged; the token is still validated against the antiforgery cookie and the caller's identity.

Beacon's `UseBeaconPipeline()` includes `UseAntiforgery()` in the order Beacon needs (after authentication, so tokens are issued for the signed-in user). Do not move it. See [Host setup](/integrate/host-setup/).

### The failure response

```http
HTTP/1.1 400 Bad Request
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "Invalid antiforgery token.",
  "status": 400,
  "detail": "<message from the framework, for example: The required antiforgery header value \"X-XSRF-TOKEN\" is not present.>"
}
```

The `detail` is the framework's own message, so its wording depends on the failure (missing header, missing cookie, token for another identity). The server logs a warning:

```text
Antiforgery validation failed for POST /beacon/api/queries (<failure code>)
```

The log names the failure by code only; it never includes the token.

## Session cookie flags

The `Beacon.Auth` session cookie is `HttpOnly`, `SameSite=Lax`, and `Secure` as the request arrives, or always with `Beacon:Authentication:Cookie:SecureAlways`. The flags, the plain-HTTP startup warning and the sign-in settings are on [Sign-in and session hardening](/security/sign-in-and-sessions/#the-session-cookie).

## The embed page

The anonymous embed page (`GET /beacon/embed/{publicId}`) is the one Beacon page meant to be framed. It sends its own response headers, and `SecurityHeadersMiddleware` keeps them:

| Header | Value |
|---|---|
| `Content-Security-Policy` | `default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors <origins>` |
| `Referrer-Policy` | `no-referrer` |
| `Cache-Control` | `no-store` |
| `X-Content-Type-Options` | `nosniff` |

`<origins>` is the list of `https` frame origins stored on the grant, separated by spaces. A grant with no origin, or one that is not live, gets `frame-ancestors 'none'`. Because the response names `frame-ancestors`, Beacon does not add `X-Frame-Options: DENY`. See [Embedding](/integrate/embedding/).

## Proxied deployments

Behind a reverse proxy that terminates TLS, three things decide whether these protections work:

1. **`Beacon:PublicBaseUrl`.** Set it to the public https address. Without it the origin check compares each `Origin` with the request's own scheme and `Host`. Both come from the request, which a client or a misconfigured proxy influences. Beacon also logs, at startup, once the MCP discovery routes are mapped:

   ```text
   Beacon:PublicBaseUrl is not configured — MCP discovery documents and WWW-Authenticate challenges will reflect the request Host header. Set Beacon:PublicBaseUrl on public deployments so advertised URLs cannot be influenced by the caller.
   ```

2. **Forwarded headers.** The host must run `UseForwardedHeaders` first in the pipeline, listing only your proxies, so the request's scheme is `https` and the client address is the real one. Without the scheme, a request made over https looks like http to Beacon: the cookie loses its `Secure` flag unless `SecureAlways` is on, and the `Origin` comparison without configured origins fails.

   ```csharp
   var forwarded = new ForwardedHeadersOptions
   {
       ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
   };
   forwarded.KnownProxies.Clear();
   forwarded.KnownNetworks.Clear();
   forwarded.KnownProxies.Add(IPAddress.Parse("10.0.0.5"));
   app.UseForwardedHeaders(forwarded);
   ```

   The reference host wraps the same code in a switch: `Beacon:ForwardedHeaders:Enabled` and a `Beacon:ForwardedHeaders:KnownProxies` list. Those two keys belong to the sample host, not to the Beacon packages. An empty proxy list trusts none, so the headers are ignored (fails safe).

3. **`AllowedHosts`.** The ASP.NET Core `AllowedHosts` setting defaults to `*`. Set it to your real host names. It is the host's own filter, and it works with the Host check above.

## Recommended values

| Setting | Development | Production | Regulated |
|---|---|---|---|
| `Beacon:Security:Csp` | `Enforce` (default) | `Enforce` | `Enforce` or unset |
| `Beacon:Security:OriginGuard` | `LogOnly` (default) | `Enforce` after a clean `LogOnly` release | `Enforce` |
| `Beacon:Security:AllowedOrigins` | empty | the public origins, if more than `PublicBaseUrl` | list every origin first |
| `Beacon:PublicBaseUrl` | unset | your public https address | required, https |
| `Beacon:Authentication:Cookie:SecureAlways` | off | on | on |

With `Beacon:Profile = Regulated`, the startup check fails, listing all findings in one message, when any of these hold (from `RegulatedProfile.Assess`):

- `Beacon:Security:Csp` is set to anything but `Enforce` (unset is accepted). Message: `must be Enforce (or unset): browsers do not block what Beacon's Content-Security-Policy refuses`.
- `Beacon:Security:OriginGuard` is not `Enforce`. Message: `must be Enforce (list the origins browsers use in Beacon:Security:AllowedOrigins first): a request from another origin is only logged`.
- `Beacon:PublicBaseUrl` is not an https URL. Message: `must be set to Beacon's https address: links, discovery documents and the origin check otherwise follow the request's Host header`.
- `Beacon:Authentication:Cookie:SecureAlways` is not true. The profile fills it when you leave it unset.

The profile judges `Csp` and `OriginGuard` on the bound options, so a value set in code with `Configure` or `PostConfigure` counts as one set in configuration. The profile never overrides a value you configured.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| 403 `The request's origin is not allowed.` on every save after switching to `Enforce` | The browser's `Origin` is not in the allowed list. Common cause: a proxy that does not forward the scheme, so the "own origin" is `http://...` while the browser sends `https://...`. | Set `Beacon:PublicBaseUrl` or `AllowedOrigins`. Run `UseForwardedHeaders`. |
| Same 403, only on a second host name or port | That name is not listed. With configured origins, the `Host` must match one of them. | Add the origin, including a non-default port, to `AllowedOrigins`. |
| Startup fails: `Beacon:Security:AllowedOrigins:0 must be an http or https origin...` | The entry has a path, query, fragment, user info, a non-http scheme or is not a URL. | Use `https://host` or `https://host:port`. |
| Warning: `Beacon:PublicBaseUrl is not an absolute http or https URL, so the Beacon origin check does not count it as an allowed origin.` | The value is a bare host name or another scheme. | Write the full URL, for example `https://beacon.example.com`. |
| 400 `Invalid antiforgery token.` after a long idle time or a sign-in in another tab | The token is bound to the identity that fetched it. | Reload the page. The SPA retries once on its own. |
| 400 `Invalid antiforgery token.` from your own script that uses the session cookie | The script sends no `X-XSRF-TOKEN`. | Use an API key or bearer token instead, or fetch `GET /beacon/api/csrf` and send the token. |
| Blank page; console says `Refused to load the script ... Content-Security-Policy` | Something injects a script (proxy, extension, host middleware). | Test with `ReportOnly`, remove the injection, or send your own policy. |
| `Beacon` embedded in an `<iframe>` is blank | `frame-ancestors 'none'` and `X-Frame-Options: DENY`. This is by design. | Frame only the embed page, which has its own `frame-ancestors` list. |
| The origin warning appears for a request from a script that sends `Origin` | A script copied browser headers. | Authenticate it with an API key: requests with an `Authorization` header are not checked. |

## Related pages

- [Sign-in and session hardening](/security/sign-in-and-sessions/): the cookie, sessions and SSO settings.
- [Rate limits and request limits](/security/rate-and-request-limits/): per-address limits and why forwarded headers matter.
- [Host setup](/integrate/host-setup/#security-headers-and-the-origin-check): the middleware pipeline and its order.
- [Production checklist](/integrate/production/): the reverse-proxy and HSTS checklist.
- [Embedding](/integrate/embedding/): the embed page and its frame origins.
- [Configuration reference](/getting-started/configuration/#security-headers-and-the-origin-check-beaconsecurity): the settings in one table.
