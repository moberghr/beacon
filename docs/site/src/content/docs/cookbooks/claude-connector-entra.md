---
title: Add Beacon to claude.ai and Claude Desktop with Entra sign-in
description: "Register Beacon's MCP endpoint as a custom connector in Claude, with people signing in through Microsoft Entra ID and Beacon mapping each person to projects, and check the sign-in path with beacon-doctor."
---

| | |
|---|---|
| **You end up with** | A custom connector in claude.ai and Claude Desktop that signs people in with Entra and gives them Beacon's tools in the projects you map them to |
| **Time** | about 45 minutes, plus tenant admin consent |
| **You need** | A running Beacon reachable over HTTPS (`Beacon:PublicBaseUrl` set, see [MCP and AI clients](/mcp/connect/#public-url-and-discovery)), Entra rights to create app registrations and grant admin consent, a project with data sources, and permission in Claude to add custom connectors (an organization owner may have to add the connector first) |
| **Applies to** | Beacon 5.0 · Full Beacon (a Beacon with data sources, mapping Entra callers to projects). For your own application's endpoints as tools, use the [MCP gateway](/mcp/gateway/) instead |

:::tip[Let your AI do it]
This recipe is half Entra portal and half Beacon configuration, so ask your agent to **guide you** through it and to make only the repository changes. Paste this into your coding agent.

```text wrap
Read https://moberghr.github.io/beacon/cookbooks/claude-connector-entra.md and guide me through it step by step.
You may edit this repository's Beacon configuration and code. For everything in the Entra portal or in Claude,
tell me exactly what to click and what value to enter, then wait for me to confirm. Never change tenant settings
yourself, never ask me to paste a client secret or token into the chat, and keep secrets in user secrets or my
secret store. Ask me before every decision the recipe marks as one, and show me the result of each
"Check it works" item.
```
:::

## Before you start

| Decision | Options | Default |
|---|---|---|
| Who may connect | Users holding an app role, members of groups, or every user of the tenant | An app role (`Beacon.Mcp.User`) |
| Projects they reach | One project for everyone, or extra projects per group or role (`GroupProjects`) | One project |
| Beacon user per person | Provisioned on first call (`AutoProvision`, role `DefaultRoleName`) or none | Provisioned, role `Viewer` |
| What they can run | `Viewer` reads the catalog and runs saved-query tools only; `ask`, `query` and `dry_run` need Editor | Raise `DefaultRoleName` to `Editor` only if people should run SQL |

Provisioned people get no project membership. For a restricted project, add each of them as a member, or they reach nothing in it. See [What a caller reaches](/mcp/entra-callers/#what-a-caller-reaches).

## 1. Create the API app registration

This is the registration whose tokens Beacon validates. Use your Beacon's existing one if there is one.

1. **Manifest**: set `api.requestedAccessTokenVersion` to `2`. Do this first; Entra accepts an HTTPS Application ID URI only for a v2 app.
2. **Expose an API → Application ID URI**: keep `api://<api-client-id>` and add the exact MCP URL, `https://beacon.example.com/beacon/mcp`, to `identifierUris`. No trailing slash. Claude sends that URL as the `resource` parameter, and a mismatch fails with `AADSTS9010010`. Entra may require the host name to be a verified domain of the tenant.
3. **Expose an API → Add a scope**: `mcp.access`, delegated. Clients request it as `api://<api-client-id>/mcp.access`.
4. **App roles**: create `Beacon.Mcp.User` for users or groups, and assign it to the people who may connect (**Enterprise applications → Users and groups**).

The tokens Claude receives are v2 access tokens: `iss` is `https://login.microsoftonline.com/<tenant-id>/v2.0`, `aud` is the API's client id GUID, `scp` is `mcp.access`.

## 2. Create a client registration for Claude

Entra has no Dynamic Client Registration, so Claude cannot register itself.

1. **New registration**, then **Authentication → Add a platform → Web**, redirect URI `https://claude.ai/api/mcp/auth_callback`.
2. **Certificates & secrets**: create a client secret and note its expiry. The connector stops working when it expires. Keep the value for step 4; do not commit it.
3. **API permissions → Add a permission → APIs my organization uses →** your API registration → **Delegated** → `mcp.access`.
4. **Grant admin consent** for the tenant.

## 3. Configure Beacon

Bearer validation must name issuers and audiences once any caller is configured; the host refuses to start otherwise.

```csharp
builder.Services.AddBeaconJwtAuthentication(x =>
{
    x.EnableBearerAuthentication = true;
    x.Validation.JwksEndpoint = "https://login.microsoftonline.com/<tenant-id>/discovery/v2.0/keys";
    x.Validation.ValidIssuers = ["https://login.microsoftonline.com/<tenant-id>/v2.0"];
    x.Validation.ValidAudiences = ["<api-client-id>", "api://<api-client-id>"];
});
```

```json
{
  "Beacon": {
    "PublicBaseUrl": "https://beacon.example.com",
    "Mcp": {
      "ProtectedResource": {
        "AuthorizationServers": [ "https://login.microsoftonline.com/<tenant-id>/v2.0" ],
        "ScopesSupported": [ "api://<api-client-id>/mcp.access", "offline_access" ]
      },
      "Callers": {
        "AllowedTenants": [ "<tenant-id>" ],
        "Users": {
          "Enabled": true,
          "RequiredRoles": [ "Beacon.Mcp.User" ],
          "ProjectIds": [ 4 ],
          "Scope": "Execute",
          "AutoProvision": true,
          "DefaultRoleName": "Viewer"
        }
      }
    }
  }
}
```

| Key | Why it matters here |
|---|---|
| `Beacon:PublicBaseUrl` | The `resource` Claude sends is built from it: `https://beacon.example.com/beacon/mcp`. Without it, Beacon uses the request's `Host` header. |
| `Mcp:ProtectedResource:AuthorizationServers` | Published in `/.well-known/oauth-protected-resource`. Use the **v2** issuer. Instead of listing it you can set `DeriveAuthorizationServerFromJwtIssuer` to `true`. |
| `Mcp:ProtectedResource:ScopesSupported` | Without it Beacon publishes `Execute`, which Entra does not know. `offline_access` gives Claude a refresh token; without it people sign in again every hour, and Beacon logs a startup warning. |
| `Mcp:Callers:AllowedTenants` | Required and non-empty once `Users:Enabled` is on. |
| `Mcp:Callers:Users:RequiredRoles` / `RequiredGroups` | With neither set, startup fails unless `AllowAnyTenantUser` is `true`. Prefer app roles: Entra leaves the `groups` claim out for users in more than about 200 groups. |
| `Mcp:Callers:Users:DefaultRoleName` | Must not be Admin or a functional role; startup fails otherwise. |

Keep the app's own project list small: every accepted user gets `ProjectIds`. Add `GroupProjects` for more. All keys are described in [Entra ID callers](/mcp/entra-callers/#configuration).

Deploy, then check the two discovery documents from any machine:

```bash
curl -s https://beacon.example.com/.well-known/oauth-protected-resource | jq
```

- [ ] `resource` is `https://beacon.example.com/beacon/mcp`.
- [ ] `authorization_servers[0]` is the v2 issuer and `scopes_supported` lists your scope and `offline_access`.

## 4. Add the connector in Claude

Claude's menus change; if a label below differs, follow Anthropic's current custom-connector documentation. Beacon needs only the URL and the client registration's id and secret.

1. In claude.ai, open **Settings → Connectors** and **Add custom connector**. An organization owner may have to add it for the organization first.
2. Enter the server URL `https://beacon.example.com/beacon/mcp`.
3. In **Advanced settings**, enter the **OAuth Client ID** and **OAuth Client Secret** of the registration from step 2.
4. Connect. Claude opens the Entra sign-in; the person consents, or admin consent already covers it.

Claude Desktop uses the connectors of the claude.ai account it is signed in to.

## Check the sign-in path with beacon-doctor

`beacon-doctor` checks the endpoint the way a connector sees it: the 401 challenge, the protected-resource metadata, the authorization server's metadata and signing keys, PKCE and `offline_access`.

```bash
dotnet tool install -g Moberg.Beacon.Doctor
beacon-doctor mcp https://beacon.example.com/beacon/mcp
```

Each line starts with `PASS`, `WARN` or `FAIL`; the exit code is `1` when any check fails and `0` otherwise. Add `--json` for machine-readable output. With an Entra access token for your API (`--token <token>`, or the `BEACON_MCP_TOKEN` environment variable) it also tries `initialize` and `tools/list`, which proves the token maps to a caller. The token is sent to that endpoint only. Without one, the `handshake` line warns that it was not tried.

## Check it works

- [ ] `beacon-doctor` reports no `FAIL`. A `WARN` for `offline_access` means sign-in will repeat hourly: add it to `ScopesSupported`.
- [ ] In Claude, the connector shows as connected after the Entra sign-in, and lists Beacon's tools.
- [ ] Ask in a new chat: *"Use Beacon's get_context to describe my project."* The answer names your project.
- [ ] In Beacon, a new user exists for the person (when `AutoProvision` is on), and their calls appear in the MCP audit (see [MCP observability and audit](/mcp/observability-and-audit/#reading-the-audit)).

## If something goes wrong

| Symptom | Cause and fix |
|---|---|
| `AADSTS9010010` at sign-in | The `resource` Claude sent is not an Application ID URI of the API registration, or a requested scope belongs to another registration. Compare `resource` in `/.well-known/oauth-protected-resource` with `identifierUris`, character for character. |
| `401` with `error="invalid_token"` | Beacon rejected the token. Compare `iss` and `aud` with `ValidIssuers` and `ValidAudiences`; a v2 token's `aud` is the client id GUID. |
| `403` after sign-in | The token is valid but the person is not an accepted caller: check `Users:Enabled`, `RequiredRoles` or `RequiredGroups` (is the role assigned to them?) and `AllowedTenants`. |
| "Access denied: you may read project N but not run ad-hoc queries in it." | The provisioned user is a `Viewer`. Raise `DefaultRoleName` or the user's role to Editor, and for a restricted project add them as an Editor member. |
| "No projects are available to you." | `ProjectIds` is empty for this person, or the project is restricted and they are not a member. |
| `503` with `Retry-After` | Beacon could not fetch the Entra signing keys and has none cached. Check outbound access to `login.microsoftonline.com`. |
| Sign-in repeats every hour | `offline_access` is missing from `ScopesSupported`. |
| Connector stopped working months later | The client secret in step 2 expired. Create a new one and update the connector. |
| `beacon-doctor` warns about PKCE | Entra's metadata does not advertise PKCE although it supports it. Whether Claude enforces that is for Anthropic's documentation to say; the warning is not a Beacon fault. |

## Next

- [Entra ID callers](/mcp/entra-callers/): system callers, token classification, replacing the mapper.
- [Ask your data questions with an AI agent](/cookbooks/ask-your-data/): what to say once the connector works.
- [MCP gateway](/mcp/gateway/): the same connector for your own application's endpoints, with no data sources.
- [Give an AI agent access to one project only](/cookbooks/agent-one-project/): limiting what a caller reaches.
