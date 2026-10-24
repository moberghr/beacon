---
title: Embedding saved queries
description: "Show one approved saved query as a read-only table inside another site: turn it on, create a grant, exchange it for a short-lived token from your backend, frame the page, and what the design does and does not protect."
---

Beacon can render one approved saved query as a read-only table inside an iframe on your own site. The viewer never signs in to Beacon and never sees a Beacon credential that lasts. This is separate from [embedding Beacon in your host](/integrate/) (the packages) and from [MCP](/mcp/connect/).

Embedding is **off by default** and is a deployment lock. While `Beacon:Embed:Enabled` is `false`, no embed route is mapped (each answers 404), no grant can be created and no key is needed. An [MCP gateway](/mcp/gateway/) host never serves the embed routes.

## How it works

There are two credentials, so that a long-lived authority never reaches the browser.

1. **Grant** (stored, revocable, long-lived). A SecurityAdmin or Admin creates it in Beacon. It pins one project, one saved query and one approved version of it, the sites allowed to frame it, an expiry, and a value or a lock for every query parameter. Creating it produces a **grant secret** (`bge_...`), shown once. Only its SHA-256 is stored.
2. **Token** (stateless, short-lived). Your backend sends the grant secret to Beacon and gets an HS256 token that lives 5 minutes by default and never more than 15. Your page puts the token in the iframe URL. The browser only ever holds the token.

A token buys one thing: a render of the grant's pinned query version. It opens no other route, runs no other SQL and cannot reach MCP.

## Set up

### 1. Generate a signing key and turn embedding on

The signing key is separate from `Beacon:EncryptionKey`. It must be base64 of at least 32 random bytes:

```bash
openssl rand -base64 32
```

Supply it as a secret, not in a file under source control:

```bash
export Beacon__Embed__Enabled=true
export Beacon__Embed__SigningKey='<output of the command above>'
```

Beacon refuses to start when the key is missing, short, plain text, a placeholder, or equal to `Beacon:EncryptionKey`, one of `Beacon:PreviousEncryptionKeys` or the obsolete `Beacon:EmbedToken` key. The message never contains the key. Changing the key invalidates every outstanding token (at most 15 minutes of them); your backend simply mints again. Every key and limit is in the [`Beacon:Embed` reference](/getting-started/configuration/#embedding-beaconembed).

If Beacon runs behind a reverse proxy or load balancer, the host must run `UseForwardedHeaders` with the proxy's address in `KnownProxies` or `KnownNetworks`, before `UseBeaconAuthentication`, and set `Beacon:PublicBaseUrl` when TLS ends at the proxy. Without it every viewer shares the proxy's address and one rate-limit budget, and every render is refused because the viewer's `https` origin does not match Beacon's `http` one. A host `FallbackPolicy` that names `AuthenticationSchemes` also replaces the embed caller and every call answers 401: leave it without schemes or exempt the embed paths.

### 2. Make sure the query can run

The query must have an approved, runnable version (the same condition as an [MCP saved-query tool](/mcp/)), and its project must link every data source the version reads. If a newer version is approved later, the grant stops working (`superseded`) and you create a new one: what was reviewed for embedding is what runs.

### 3. Create a grant

Open **Embeds** in the sidebar (shown only to a SecurityAdmin, and only while embedding is on). Choose the project and query, then:

- **Origins**: each is an exact `https://host[:port]`, with no path and no wildcard. 1 to `MaxOrigins` (default 10). `http://localhost` is accepted only in Development.
- **Expiry**: at most `MaxGrantDays` (default 90).
- **Parameters**: every parameter of the query is either *fixed* (a value stored on the grant) or *locked* (your backend supplies it when it asks for a token). Viewers cannot change either. An unknown or missing parameter is refused.

While the approval workflow is on (and self-approval is not allowed), whoever requested or approved the query's active version cannot create a grant for it; someone else must.

Copy the secret now; it is not shown again. The same operations are `POST /beacon/api/embed-grants`, `GET /beacon/api/embed-grants` and `POST /beacon/api/embed-grants/{id}/revoke`. A grant is never deleted, so the audit trail stays intact. To rotate a secret, revoke the grant and create a new one.

The render acts as the **grant's creator**, with at most Viewer rights and never the Admin bypass. Creation is refused when the creator could not render the query, for example an Admin who is not a member of a restricted project. If the creator is later disabled or loses the membership, the grant renders nothing.

### 4. Exchange the secret for a token on your backend

Never call this from a browser. The grant secret belongs on your server.

```bash
curl -sS -X POST https://beacon.example.com/beacon/api/embed/tokens \
  -H "Authorization: BeaconEmbedGrant $BEACON_EMBED_GRANT_SECRET" \
  -H "Content-Type: application/json" \
  -d '{ "parameters": { "customerId": "42" }, "ttlSeconds": 300 }'
```

```json
{
  "token": "eyJ...",
  "expiresAt": "2026-10-24T12:05:00Z",
  "embedPath": "/beacon/embed/3f9c...e1#t=eyJ..."
}
```

`parameters` holds a value for each *locked* parameter, as text. `ttlSeconds` is optional. Without it the token lives `TokenTtlSeconds` (default 300). The backend may ask for anything from 1 up to `MaxTokenTtlSeconds` (default 900), so it can also ask for a longer token than the default. The token never outlives the grant. To bound token lifetime for every caller, lower `MaxTokenTtlSeconds`.

The same call from a server in C#:

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, "https://beacon.example.com/beacon/api/embed/tokens");
request.Headers.TryAddWithoutValidation("Authorization", $"BeaconEmbedGrant {grantSecret}");
request.Content = JsonContent.Create(new { parameters = new { customerId = "42" } });

using var response = await httpClient.SendAsync(request);
response.EnsureSuccessStatusCode();
var issued = await response.Content.ReadFromJsonAsync<IssuedEmbedToken>();
// issued.EmbedPath is "/beacon/embed/{id}#t=<token>"; prefix it with Beacon's public origin.

public sealed record IssuedEmbedToken(string Token, DateTimeOffset ExpiresAt, string EmbedPath);
```

Mint per page view. Do not cache a token past its expiry or share one between viewers.

### 5. Frame the page

Put the origin and `embedPath` in the iframe's `src`. The token travels in the URL **fragment**, so it is never sent to a server and never appears in a `Referer` header or an access log. The embed page removes it from the address bar as soon as it loads and sends it only in an `Authorization` header.

```html
<iframe
  src="https://beacon.example.com/beacon/embed/3f9c...e1#t=eyJ..."
  title="Open invoices"
  style="width: 100%; height: 480px; border: 0"
  referrerpolicy="no-referrer"></iframe>
```

When the token expires, the page shows "expired, reload" and posts this message to the host page so it can mint a new token and set a new `src`. The message carries no data, only its `type`:

```js
window.addEventListener('message', (event) => {
  if (event.origin !== 'https://beacon.example.com') return;
  if (event.data?.type === 'beacon-embed:expired') {
    // fetch a fresh embedPath from your backend and set iframe.src
  }
});
```

The table shows the title, the columns and rows, a note when the result was cut at `MaxRows` (default 1,000, or the project's row limit if that is lower), and an empty state. It follows the viewer's light or dark setting.

## What Beacon enforces

- **One query, one version.** The render runs the pinned version's stored steps through the same read-only SQL gate and row-security resolver as every other Beacon query. Parameter values are bound as database parameters, never placed in the SQL text.
- **PII is always masked.** There is no per-grant or per-project opt-out; custom patterns still apply.
- **Access is checked live** on every exchange and every render. Revoking a grant, expiring it, superseding its version, disabling its creator or unlinking a data source stops every outstanding token at once.
- **No ambient credentials.** The three embed routes (`POST /beacon/api/embed/tokens`, `POST /beacon/api/embed/render`, `GET /beacon/embed/{id}`) are authenticated only by the embed credential. The Beacon sign-in cookie, API keys and bearer tokens do not authenticate them, and a Beacon cookie is never created or renewed on them. The `Authorization` scheme is `BeaconEmbed`, deliberately not `Bearer`.
- **Generic refusals.** Expired, badly signed, revoked, superseded and unknown all answer `401 {"error":"embed_unavailable"}`. The reason goes to the [audit trail](/security/audit-trail/), not to the caller.
- **Frame headers.** The embed page is served with `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors <the grant's origins>`, plus `Referrer-Policy: no-referrer`, `Cache-Control: no-store` and `X-Content-Type-Options: nosniff`. A grant that is unknown, revoked or expired gets 404 with `frame-ancestors 'none'`. Render responses are `no-store`.
- **Frame marker.** The served page carries a marker (an HMAC of the grant's public id under the signing key) that it sends back with every render. A render without it is refused, so a token alone cannot be replayed against the render route from a script or another page.
- **Same origin only.** A render whose `Origin` header names anything other than Beacon is answered 403. There is no CORS: the render is called by Beacon's own embed page, and the browser enforces the third-party list through `frame-ancestors`.
- **Rate limits.** Per grant for exchanges and renders, per client address for page loads and refusals, and a cap on renders running at once. Past a limit the answer is 429 (a full render pool is 503). See the [reference](/getting-started/configuration/#embedding-beaconembed).
- **Audit.** Grant creation and revocation, every token issued, every refusal (capped per minute) and every render are recorded without tokens, secrets, origins, parameter values or SQL. See [Embedding events](/security/audit-trail/).

## Threat model: what it does not protect

- **The token is readable.** It is signed, not encrypted. Anyone holding it, including the viewer, can base64-decode its payload and read the grant id and the **locked parameter values**. Do not put a secret in a locked parameter. Use a value that identifies what to show (a customer id), never one that authorises something.
- **Whoever holds a live token can render.** The token is the credential until it expires, so the short lifetime is the bound on theft. Lower `MaxTokenTtlSeconds` (it is the bound; `TokenTtlSeconds` is only the default) and mint per page view.
- **An unframed open still renders.** Opening `embedPath` in its own tab shows the same table. `frame-ancestors` stops other sites from framing the page; it is not a check on who is looking. The token holder is the intended viewer, so Beacon does not block a top-level visit.
- **Origins limit framing, not access.** The origin list is enforced by the viewer's browser. A server-side client can fetch the embed page for its own purposes, but without a token the page shows only "This view is unavailable".
- **The grant secret is a password.** Anyone with it can mint tokens until the grant is revoked or expires. Store it as a secret on your backend, never ship it to a browser or commit it. If it leaks, revoke the grant at once.
- **Data only as safe as the query.** The grant exposes what the pinned query returns to every viewer who can obtain a token. Review that version as you would for publishing it.
- **Row-level security that cannot be applied refuses the render.** The same `Filtered` and `Denied` decisions as for MCP apply to the grant's creator.

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| Every route answers 404 | `Beacon:Embed:Enabled` is `false`, or the host is an MCP gateway |
| Beacon does not start | The signing key is missing, weak or equal to another Beacon key. The startup message names the setting, not the key |
| Exchange answers 401 | Wrong or revoked secret, the grant expired, or its query version was superseded. The audit trail records which |
| Frame is blank and the console reports `frame-ancestors` | The embedding site's origin is not on the grant, or has a different scheme or port |
| Every render answers 403 behind a proxy | `UseForwardedHeaders` is missing, or `Beacon:PublicBaseUrl` is not set where TLS ends at the proxy |
| Every call answers 401 with a valid credential | A host `FallbackPolicy` names authentication schemes |
| 429 for every viewer | Without forwarded headers all viewers share the proxy's address |
| 503 with `Retry-After` | The render pool is full, or access could not be determined; retry |
