---
name: beacon-entra
description: Prepare the Microsoft Entra app registrations a Beacon MCP gateway needs for Claude's custom connector — v2 tokens, the MCP URL in identifierUris, the mcp.access scope, a confidential client for Claude with offline_access and admin consent. Use when connecting Claude to a Beacon gateway fails with AADSTS errors, or when someone asks how to register their app for Claude.
---

# Entra registration for a Beacon MCP gateway

These steps change a tenant: print them for the person to run (or run them only after they say so), one at a time, and
check each result. Read the "Entra app registration" section of `https://moberghr.github.io/beacon/llms-gateway.txt`.

Ask for: the application's client id (`AzureAd:ClientId`), the public URL clients will use (a verified custom domain of
the tenant — `*.azurecontainerapps.io` and similar cannot be added), and whether they already have a client registration
for Claude.

**The application's registration**

```bash
APP=<application client id>
MCP_URL=https://<domain>/beacon/mcp            # no trailing slash; exactly what Beacon:PublicBaseUrl + /beacon/mcp gives

az ad app update --id "$APP" --set api.requestedAccessTokenVersion=2       # first: Entra accepts an https identifier URI only on v2
az ad app update --id "$APP" --identifier-uris "api://$APP" "$MCP_URL"
```

Add the delegated scope `mcp.access` in the portal (Expose an API → Add a scope, admins and users, enabled) or with
`az ad app update --id "$APP" --set api.oauth2PermissionScopes=@scopes.json` (keep the existing scopes in the file; a new
scope needs a fresh GUID `id`). Read its id back:

```bash
SCOPE_ID=$(az ad app show --id "$APP" --query "api.oauth2PermissionScopes[?value=='mcp.access'].id" -o tsv)
```

**A client registration for Claude** (Entra has no dynamic client registration)

```bash
CLAUDE=$(az ad app create --display-name "Claude connector for <app>" \
  --web-redirect-uris https://claude.ai/api/mcp/auth_callback --query appId -o tsv)
az ad app credential reset --id "$CLAUDE" --display-name claude --years 1     # note the secret and its expiry
az ad app permission add --id "$CLAUDE" --api "$APP" --api-permissions "$SCOPE_ID=Scope"
OFFLINE=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "oauth2PermissionScopes[?value=='offline_access'].id" -o tsv)
az ad app permission add --id "$CLAUDE" --api 00000003-0000-0000-c000-000000000000 --api-permissions "$OFFLINE=Scope"
az ad app permission admin-consent --id "$CLAUDE"
```

The client id and secret go to whoever adds the connector in Claude (the organisation owner, or each person under
Advanced settings). Optionally list `$CLAUDE` in the application's `McpAccess:AllowedClientIds`.

**Check**: `beacon-doctor mcp "$MCP_URL"` — the `resource` must equal an identifier URI character for character
(`AADSTS9010010` otherwise), and `scopes_supported` must include `offline_access` (else Claude re-prompts hourly).
Entra does not advertise PKCE although it supports it; the doctor warns about that, and the first real connection settles it.
